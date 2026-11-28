using System.Diagnostics;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Presentation.ViewModels;
using MessageLink.Protocol;

namespace MessageLink.App.Services;

/// <summary>
/// What another device changed reaching what this one shows, in a client of a Messages Server (<c>local</c> and <c>remote</c>)
/// only: a pin, mute, marked unread, archive here, snooze or name (the rows of <see cref="ReplicaChatPreferences"/>, <see cref="ReplicaContactNames"/> and
/// <see cref="ReplicaChatSnoozes"/>, once they hold the change), the pinned order, the merges and the labels (rows of the replica itself; protocol 5 for the
/// last two, which also tells the list's notice when one of this device's changes was left out), and the move between this PC's files and the
/// rows (<see cref="ReplicatedStateStore.ServerProtocolChanged"/>), which can change every row at once. The list and the open conversations' headers follow
/// with no reload; Windows' snooze reminders already follow through ObservedChatSnoozes.
/// </summary>
/// <remarks>
/// <para>A burst is applied once. A snapshot changes hundreds of rows in pages, and each category's rows tell of them separately, so changes are gathered
/// until <see cref="Quiet"/> passes without one (never longer than <see cref="Longest"/>), and then: the loaded conversations are read on the interface
/// thread, the ones whose keys changed are found by their digests and applied again off it (<see cref="LocalPreferenceChatReader.ReapplyAsync"/>, which asks
/// no service), and the list takes them on the interface thread with one redraw. A row read again in between (its copy's version moved) is left to the
/// newer read and the burst gathered again, and a burst that met a load or an event batch under way is gathered again once the list is idle. Measured from
/// the earliest arrival to the list applied, in fixed words and counts: <c>shared list applied rows= chats= [all=true] [stale=] ms=</c>. (<c>shared
/// applied</c> is the replica's own line, the event to the replica.)</para>
/// <para>Registered by <see cref="ClientPortRegistrations.AddLocal"/> alone. The in-process app and the loopback never make one, so nothing here subscribes
/// or runs for them.</para>
/// </remarks>
internal sealed class SharedListChanges : IDisposable
{
    private sealed record Target(IUiDispatcher Ui, ChatListViewModel List, Action<IReadOnlyList<ReappliedChat>>? Shown, Action<string>? Told);

    /// <summary>
    /// What the window says when another device's merges or labels left out a change made here: the conversation is in another merge now, or the
    /// label was deleted. Said in the window's action notice, which stays until closed: the list's own line is the list's summary again at its next redraw,
    /// which the same change brings.
    /// </summary>
    public const string ChangedElsewhereNotice = "Changed on another device.";
    private sealed record Burst(IReadOnlyList<string> Keys, bool All, long Arrived);

    private readonly ReplicatedStateStore replica;
    private readonly ReplicaRows[] categories;
    private readonly IAccessTokenStore tokens;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly HashSet<string> keys = new(StringComparer.Ordinal);
    private bool all, draining;
    // The earliest arrival among what is gathered (for the measure), and when the first and the last of it were heard here (for the wait).
    private long arrived, firstHeard, lastHeard;
    private volatile Target? target;
    private volatile LocalPreferenceChatReader? reader;
    // Digests of (profile, id) for the ids and names the loaded rows have, kept between bursts (used by the drain alone, one burst at a time): a burst of
    // names would otherwise hash every row, member and participant again.
    private string? digestProfile;
    private Dictionary<string, string> digests = new(StringComparer.Ordinal);

    /// <summary>How long without another change before a burst is applied.</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromMilliseconds(40);
    /// <summary>The longest a burst waits for quiet, so a steady trickle is still drawn.</summary>
    public TimeSpan Longest { get; init; } = TimeSpan.FromMilliseconds(400);

    public SharedListChanges(ReplicatedStateStore replica, ReplicaChatPreferences preferences, ReplicaContactNames names, ReplicaChatSnoozes snoozes,
        IAccessTokenStore tokens, Action<string>? log = null)
    {
        this.replica = replica; this.tokens = tokens; this.log = log;
        categories = [preferences.Rows, names.Rows, snoozes.Rows];
        foreach (var rows in categories) rows.Changed += OnRows;
        replica.RemoteChanged += OnReplica;
        replica.ServerProtocolChanged += OnSource;
        replica.Told += OnTold;
    }

    /// <summary>The reader whose copies of the conversations are applied again: the one under the merges that applies this PC's settings (App.xaml.cs).</summary>
    public void ReappliesWith(LocalPreferenceChatReader reader) => this.reader = reader;

    /// <summary>
    /// The list that follows, on the interface thread <paramref name="ui"/> runs on, and what else shows these conversations (the open conversations'
    /// headers), told on the same thread with the rows the list took. Until then, and after <see cref="Detach"/>, changes are let go: the list reads every
    /// row with its settings as they are when it loads. <paramref name="told"/> says <see cref="ChangedElsewhereNotice"/>, on the same thread.
    /// </summary>
    public void Attach(IUiDispatcher ui, ChatListViewModel list, Action<IReadOnlyList<ReappliedChat>>? shown = null, Action<string>? told = null) =>
        target = new(ui, list, shown, told);

    public void Detach(ChatListViewModel list)
    {
        if (target is { } held && ReferenceEquals(held.List, list)) target = null;
    }

    // ---- Gathering ----

    private void OnRows(object? sender, SharedRowsChange change) => Gather(change.Keys, change.Arrived, everything: false);

    // The pinned order, the merges and the labels are rows of the replica itself (PrivateState reads them there), so they are told as soon as the replica
    // holds them: the merges and the labels once MergedConversations and LabelStore have taken them, which the replica does first (ISharedRowFollower). Only
    // their categories are kept: the rows of the other categories are told by their ReplicaRows once those hold them, and a key here is matched against the
    // profile later.
    private void OnReplica(object? sender, SharedRowsChange change)
    {
        var own = change.Keys.Where(key => key.StartsWith("preferences:", StringComparison.Ordinal) || key.StartsWith(SharedCategories.Merges + ":", StringComparison.Ordinal)
            || key.StartsWith(SharedCategories.Labels + ":", StringComparison.Ordinal)).ToArray();
        if (own.Length > 0) Gather(own, change.Arrived, everything: false);
    }

    /// <summary>
    /// The merges and labels the list draws, whose changes another device's left out are told (<see cref="ChangedElsewhereNotice"/>). A merge left
    /// out had taken the labels of its conversations (LabelStore.Merge, as the window merges): they are handed back to them, so none is left on a merge that
    /// was never made. The read-here marks are re-read here too when the replica could not bring them to the server's row; the redraw applies them.
    /// </summary>
    public void Follows(MergedConversations merges, LabelStore labels, ReadHereMarks? marks = null)
    {
        this.merges = merges;
        this.labels = labels;
        this.marks = marks;
        merges.ChangedElsewhere += (_, dropped) =>
        {
            // Only a merge that does not exist: one this device's change made again turned out to hold already keeps its labels.
            foreach (var (id, members) in dropped.Unmade)
                if (merges.Find(id) is null) labels.Unmerge(id, members);
            Tell(ChangedElsewhereNotice, "changed_elsewhere");
        };
        labels.ChangedElsewhere += (_, _) => Tell(ChangedElsewhereNotice, "changed_elsewhere");
    }

    /// <summary>
    /// What the window says when a merge or label change made here keeps losing to another device's and waits a while before it goes again (the replica's
    /// <see cref="SharedRowTold.Held"/>); this device shows its own meanwhile.
    /// </summary>
    public const string HeldNotice = "Another device is changing your merged conversations or labels too. Yours will be tried again shortly.";

    private MergedConversations? merges;
    private LabelStore? labels;
    private ReadHereMarks? marks;

    private void OnTold(object? sender, SharedRowTold what) =>
        Tell(what == SharedRowTold.Held ? HeldNotice : ChangedElsewhereNotice, what == SharedRowTold.Held ? "held" : "replaced");

    private void Tell(string notice, string why)
    {
        if (target is not { } shown || stop.IsCancellationRequested) return;
        log?.Invoke("shared list " + why);
        if (shown.Told is not { } told) return;
        _ = shown.Ui.InvokeAsync(() =>
        {
            told(notice);
            return Task.CompletedTask;
        });
    }

    // The stores went from this PC's files to the rows, or back: any row the list holds may now read otherwise.
    private void OnSource(object? sender, EventArgs e) => Gather([], Stopwatch.GetTimestamp(), everything: true);

    private void Gather(IReadOnlyList<string> changed, long at, bool everything)
    {
        if (target is null || stop.IsCancellationRequested) return;
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (keys.Count == 0 && !all) { arrived = at; firstHeard = now; }
            else if (at < arrived) arrived = at;
            lastHeard = now;
            keys.UnionWith(changed);
            all |= everything;
            if (draining) return;
            draining = true;
        }
        _ = Task.Run(DrainAsync);
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                while (true)
                {
                    TimeSpan wait;
                    lock (gate)
                    {
                        var quiet = Quiet - Stopwatch.GetElapsedTime(lastHeard);
                        var longest = Longest - Stopwatch.GetElapsedTime(firstHeard);
                        wait = quiet < longest ? quiet : longest;
                    }
                    if (wait <= TimeSpan.Zero) break;
                    await Task.Delay(wait, stop.Token).ConfigureAwait(false);
                }
                Burst burst;
                lock (gate)
                {
                    if (keys.Count == 0 && !all) { draining = false; return; }
                    burst = new([.. keys], all, arrived);
                    keys.Clear(); all = false;
                }
                try { await ApplyAsync(burst).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                // The rows are in the replica either way; the list shows them at its next read of the conversation.
                catch (Exception failure) { log?.Invoke($"shared list failed error={failure.GetType().Name}"); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    // ---- Applying ----

    private async Task ApplyAsync(Burst burst)
    {
        if (target is not { } shown || reader is not { } copies) return;
        var token = stop.Token;
        if (await NotificationIdentity.ProfileAsync(tokens, token).ConfigureAwait(false) is not { } profile) return;
        // The keys are digests of (profile, id) under each category's prefix (SharedRows); a preference's ends in its setting's name.
        var chats = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in burst.Keys)
        {
            if (Digest(key, SharedCategories.ChatPreference) is { } preference) chats.Add(preference[..Math.Max(0, preference.LastIndexOf('.'))]);
            else if (Digest(key, SharedCategories.Snooze) is { } snooze) chats.Add(snooze);
            else if (Digest(key, SharedCategories.ContactName) is { } name) names.Add(name);
        }
        var pinnedOrder = burst.Keys.Contains(PrivateState.KeyOf(profile, "preferences", "pinned-order"), StringComparer.Ordinal);
        // Another device's merges or labels: already held where the list reads them, so one redraw builds the merged rows and the pills again.
        var regroup = burst.Keys.Contains(SharedCategories.KeyOf(profile, SharedCategories.Merges, SharedCategories.MergesRow), StringComparer.Ordinal)
            || burst.Keys.Contains(SharedCategories.KeyOf(profile, SharedCategories.Labels, SharedCategories.LabelsRow), StringComparer.Ordinal);
        // Another device's private reads: ReadHereMarks holds them (the replica hands them over first, as it does the merges and labels), and the
        // redraw applies them to every row (ChatListViewModel.Present), clearing the bold of a conversation read on another device. Nothing to read again.
        var readHere = burst.Keys.Contains(SharedCategories.ReadHereKey(profile), StringComparer.Ordinal);
        regroup |= readHere;
        // Quick replies, another profile's rows: nothing the list shows.
        if (chats.Count == 0 && names.Count == 0 && !pinnedOrder && !regroup && !burst.All) return;
        // Merges or labels the replica could not bring to the server's (abandoned) read the row again here, before the redraw that shows it.
        if (regroup)
            try
            {
                if (merges is not null) await merges.EnsureLoadedAsync().ConfigureAwait(false);
                if (labels is not null) await labels.EnsureLoadedAsync().ConfigureAwait(false);
                if (marks is not null) await marks.EnsureLoadedAsync().ConfigureAwait(false);
            }
            catch (Exception failure) { log?.Invoke($"shared list reload failed error={failure.GetType().Name}"); }

        IReadOnlyList<ChatSummary> loaded = [];
        long read = 0;
        if (chats.Count > 0 || names.Count > 0 || burst.All)
            await shown.Ui.InvokeAsync(() =>
            {
                loaded = shown.List.AllLoadedChats;
                read = copies.LatestVersion;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        // The copies of conversations the list no longer holds go; one read since the rows were taken may be on its way to the list.
        if (loaded.Count > 0) copies.KeepOnly(loaded.Select(chat => chat.Id).ToHashSet(StringComparer.Ordinal), read);

        if (!string.Equals(profile, digestProfile, StringComparison.Ordinal)) { digests = new(StringComparer.Ordinal); digestProfile = profile; }
        var known = digests;
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        string Hash(string id)
        {
            if (kept.TryGetValue(id, out var digest)) return digest;
            if (!known.TryGetValue(id, out digest)) digest = NotificationIdentity.Hash(profile + "\n" + id);
            return kept[id] = digest;
        }
        // A row draws on its members' settings too (LocalPreferenceChatReader), and its names on each participant's.
        static IEnumerable<string> Named(ChatSummary chat) => chat.Participants.Where(person => person.IsSelf != true && !string.IsNullOrWhiteSpace(person.Id))
            .Select(person => AvatarOverrideIdentity.ForParticipant(chat, person.Id)).Prepend(AvatarOverrideIdentity.ForChat(chat));
        bool Touched(ChatSummary chat) =>
            chats.Count > 0 && chat.MemberChatIds.Prepend(chat.Id).Any(id => chats.Contains(Hash(id)))
            || names.Count > 0 && Named(chat).Any(identity => names.Contains(Hash(identity)));
        var matched = 0;
        var rows = new List<ReappliedChat>();
        foreach (var chat in loaded)
        {
            if (!burst.All && !Touched(chat)) continue;
            matched++;
            if (await copies.ReapplyAsync(chat, token).ConfigureAwait(false) is { } row) rows.Add(row);
        }
        if (loaded.Count > 0)
        {
            // Digests already made for what the list holds stay for the next burst (a burst of pins hashes no names); nothing else is kept.
            foreach (var chat in loaded)
                foreach (var identity in chat.MemberChatIds.Prepend(chat.Id).Concat(Named(chat)))
                    if (known.TryGetValue(identity, out var digest)) kept.TryAdd(identity, digest);
            digests = kept;
        }

        var changed = 0;
        var stale = 0;
        await shown.Ui.InvokeAsync(async () =>
        {
            // A row whose conversation was read again since its copy was taken (an event batch that has already landed, so the list is not busy) is newer
            // than what was applied here: it is left as it is, and the burst is applied again from the newer copy. Asked by the list after its own await
            // (the pinned order's read), with nothing awaited between the question and the redraw, since a batch can land during that read too.
            if (rows.Count > 0 || pinnedOrder || regroup)
            {
                var (count, taken) = await shown.List.ApplySharedAsync(rows, pinnedOrder, row => copies.CopyVersion(row.Chat.Id) == row.Version, regroup);
                changed = count;
                stale = rows.Count - taken.Count;
                shown.Shown?.Invoke(taken);
            }
            if (stale > 0) Gather(burst.Keys, burst.Arrived, burst.All);
            // A load or an event batch under way read its rows before these changes reached the replica, and puts them back as it ends (or holds rows
            // the list has not been given yet: the saved list at a start): applied again once it has.
            if (shown.List.IsBusy) AgainWhenIdle(shown.List, burst);
        }).ConfigureAwait(false);
        if (matched == 0 && !pinnedOrder && !regroup) return;
        log?.Invoke($"shared list applied rows={burst.Keys.Count} chats={changed}{(burst.All ? " all=true" : "")}{(stale > 0 ? $" stale={stale}" : "")} ms={(long)Stopwatch.GetElapsedTime(burst.Arrived).TotalMilliseconds}");
    }

    // On the interface thread. Once per burst: the next change of IsBusy to false gathers its keys again.
    private void AgainWhenIdle(ChatListViewModel list, Burst burst)
    {
        System.ComponentModel.PropertyChangedEventHandler? idle = null;
        idle = (_, e) =>
        {
            if (e.PropertyName != nameof(ChatListViewModel.IsBusy) || list.IsBusy) return;
            list.PropertyChanged -= idle;
            Gather(burst.Keys, burst.Arrived, burst.All);
        };
        list.PropertyChanged += idle;
    }

    private static string? Digest(string key, string category) =>
        key.Length > category.Length + 1 && key.StartsWith(category, StringComparison.Ordinal) && key[category.Length] == ':' ? key[(category.Length + 1)..] : null;

    public void Dispose()
    {
        stop.Cancel();
        target = null;
        foreach (var rows in categories) rows.Changed -= OnRows;
        replica.RemoteChanged -= OnReplica;
        replica.ServerProtocolChanged -= OnSource;
        replica.Told -= OnTold;
    }
}
