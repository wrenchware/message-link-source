using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

/// <summary>
/// How far each conversation was read on this PC while private reading kept its service from being told. The service
/// goes on reporting it unread — that is the point — so without these the row, the badge, the tray list, "Still unread"
/// repeats and the "New messages" line would all come back with every refresh and every launch. Each mark is the
/// conversation's newest activity when it was read: everything up to it counts as read here, anything newer is unread
/// as usual, and the conversation's read watermark (where "New messages" goes) moves up to it. Kept in the protected
/// store under the profile, like the pin order. A mark goes once the service itself says the conversation is read
/// (read on the phone, a reply sent, a receipt sent from here), since there is then nothing left for it to cover.
/// </summary>
/// <remarks>
/// <para>Thread-safe: the list applies marks on the interface thread, the notification repeats on the pool.</para>
/// <para><b>A client of a Messages Server</b> (SWITCHBOARD.md stage 6, slice 7). The marks are one row the server's other clients write too, so a private read
/// on one device clears the row's bold on the others. Each change (a mark, a mark dropped because the service caught up, a service forgotten) is kept until
/// the host has taken the write it went in, and when the write lost to another device's the replica hands over the host's row (<see cref="ISharedRowFollower"/>)
/// and the changes still kept are made again over it: the merge is the later moment per conversation, then the cap. Dropping a mark is made again only over a
/// mark no newer than the one dropped, so a read made on another device meanwhile stays. The in-process app and the loopback keep the store they always had
/// (<see cref="PrivateState.Shared"/> is null there) and run exactly the code that is not under the line that says so.</para>
/// </remarks>
public sealed class ReadHereMarks(PrivateState? state = null, PrivateReading? reading = null)
{
    /// <summary>At most this many conversations are remembered; the ones read here longest ago go first.</summary>
    public const int Kept = 2000;
    private const string Category = "preferences";
    private const string Id = "read-here";
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> marks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim loading = new(1, 1);
    private readonly SemaphoreSlim saving = new(1, 1);
    private string? profile;
    private bool loaded;
    // A read made before the saved marks were loaded: saved once they are, merged with them, never written over them.
    private bool markedBeforeLoad;
    private long version;

    /// <summary>Whether a read of this conversation stays on this PC: its service has private reading on.</summary>
    public bool Wants(string chatId) => reading?.IsOn(chatId) == true;

    public int Count { get { lock (gate) return marks.Count; } }

    /// <summary>How far this conversation was read here, or null when it has no mark.</summary>
    public DateTimeOffset? Through(string chatId) { lock (gate) return marks.TryGetValue(chatId, out var at) ? at : null; }

    /// <summary>Reads the saved marks for the current profile, once per profile.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (state is null) return;
        if (state.Shared is not null)
        {
            await EnsureLoadedSharedAsync();
            return;
        }
        await loading.WaitAsync();
        try
        {
            var next = await state.ProfileAsync();
            lock (gate) if (loaded && next == profile) return;
            Dictionary<string, DateTimeOffset>? saved = null;
            try { saved = next is null ? null : await state.ReadAsync<Dictionary<string, DateTimeOffset>>(next, Category, Id); }
            catch (Exception) { /* Unreadable marks only mean those conversations show unread again, as the service has them. */ }
            bool pending;
            lock (gate)
            {
                // A profile change starts from what that profile saved; the old profile's marks do not carry over.
                if (loaded && next != profile) marks.Clear();
                foreach (var (chatId, at) in saved ?? [])
                    if (!string.IsNullOrEmpty(chatId) && (!marks.TryGetValue(chatId, out var current) || at > current)) marks[chatId] = at;
                Trim(marks);
                profile = next; loaded = true;
                pending = markedBeforeLoad; markedBeforeLoad = false;
            }
            if (pending) SaveSoon();
        }
        finally { loading.Release(); }
    }

    /// <summary>The conversation was read here through <paramref name="through"/> without its service being told.</summary>
    public void Mark(string chatId, DateTimeOffset through)
    {
        var following = state?.Shared is not null;
        lock (gate)
        {
            if (!Put(marks, chatId, through)) return;
            if (following)
            {
                // Recorded even while the row is being read again (stale): replayed over it then, so a private read is never left to show unread.
                var change = Record(map => Put(map, chatId, through));
                // A mark the host does not keep yet (a host older than 7, or none heard) is merged over the host's row when it comes (CatchUpAsync).
                if (!SharesNow()) NoteUnshared(chatId, through, change.Tag);
                return;
            }
            if (!loaded) { markedBeforeLoad = true; return; }
        }
        SaveSoon();
    }

    // The later moment wins, then the cap. The one rule a mark is made by, here and over another device's row.
    private static bool Put(Dictionary<string, DateTimeOffset> map, string chatId, DateTimeOffset through)
    {
        if (map.TryGetValue(chatId, out var current) && current >= through) return false;
        map[chatId] = through;
        Trim(map);
        return true;
    }

    /// <summary>
    /// The conversation as read here: nothing unread up to its mark, and its read watermark no earlier than the mark.
    /// Changes nothing else, and nothing for a conversation with no mark. Safe to apply more than once.
    /// </summary>
    public ChatSummary Apply(ChatSummary chat)
    {
        DateTimeOffset through;
        lock (gate) if (!marks.TryGetValue(chat.Id, out through)) return chat;
        // Only a service that says how far reading went has a watermark to move; the others place no "New messages".
        var readUpTo = chat.ReadUpTo is { } upTo && upTo < through ? through : chat.ReadUpTo;
        var unread = chat.UnreadCount > 0 && chat.LastActivity is { } last && last <= through ? 0 : chat.UnreadCount;
        return readUpTo == chat.ReadUpTo && unread == chat.UnreadCount ? chat : chat with { ReadUpTo = readUpTo, UnreadCount = unread };
    }

    /// <summary>
    /// Drops the marks the service has caught up with: the conversations among these, as the service reports them, that
    /// it says have nothing unread. Given the service's own rows, never ones a mark was already applied to.
    /// </summary>
    public void Settle(IEnumerable<ChatSummary> fromService)
    {
        var changed = false;
        var following = state?.Shared is not null;
        lock (gate)
        {
            if (marks.Count == 0) return;
            var dropped = new List<(string ChatId, DateTimeOffset At)>();
            foreach (var chat in fromService)
            {
                // This device's copy of the service's row can be older than a read made on another device since: it says nothing unread, but its newest activity is
                // before the mark, and the mark (everywhere) is dropped only once the service has caught up with it.
                if (following && !(marks.TryGetValue(chat.Id, out var covered) && chat.LastActivity is { } last && last >= covered)) continue;
                if (chat.UnreadCount <= 0 && marks.Remove(chat.Id, out var at)) { changed = true; dropped.Add((chat.Id, at)); }
            }
            if (changed && following) Record(map => Drop(map, dropped));
        }
        if (changed && !following) SaveSoon();
    }

    // A mark dropped is made again only over a mark no newer than the one that was dropped: a conversation read here since, on another device, keeps its mark.
    private static bool Drop(Dictionary<string, DateTimeOffset> map, List<(string ChatId, DateTimeOffset At)> dropped)
    {
        var changed = false;
        foreach (var (chatId, at) in dropped)
            if (map.TryGetValue(chatId, out var current) && current <= at) { map.Remove(chatId); changed = true; }
        return changed;
    }

    /// <summary>Forgets the marks of the conversations the test matches (a service signed out, a Google reset).</summary>
    public void Forget(Func<string, bool> matches)
    {
        var changed = false;
        var following = state?.Shared is not null;
        lock (gate)
        {
            foreach (var chatId in marks.Keys.Where(matches).ToArray()) { marks.Remove(chatId); changed = true; }
            if (changed && following) Record(map => ForgetWhere(map, matches));
        }
        if (changed && !following) SaveSoon();
    }

    private static bool ForgetWhere(Dictionary<string, DateTimeOffset> map, Func<string, bool> matches)
    {
        var gone = map.Keys.Where(matches).ToArray();
        foreach (var chatId in gone) map.Remove(chatId);
        return gone.Length > 0;
    }

    // The oldest reads go first. Called under the lock.
    private static void Trim(Dictionary<string, DateTimeOffset> map)
    {
        if (map.Count <= Kept) return;
        foreach (var chatId in map.OrderBy(pair => pair.Value).Take(map.Count - Kept).Select(pair => pair.Key).ToArray()) map.Remove(chatId);
    }

    // One write at a time, of the newest marks; a write that finds a newer one queued behind it leaves the work to that one.
    private void SaveSoon()
    {
        if (state is null) return;
        _ = SaveAsync(Interlocked.Increment(ref version));
    }

    private async Task SaveAsync(long mine)
    {
        try
        {
            await saving.WaitAsync();
            try
            {
                if (Interlocked.Read(ref version) != mine) return;
                string owner; Dictionary<string, DateTimeOffset> snapshot;
                lock (gate)
                {
                    // Until the saved marks are read, writing would replace them; the load saves what was made meanwhile.
                    if (!loaded || profile is null) return;
                    owner = profile; snapshot = new(marks, StringComparer.Ordinal);
                }
                await state!.WriteAsync(owner, Category, Id, snapshot);
            }
            finally { saving.Release(); }
        }
        catch (Exception) { /* Unsaved marks only mean those conversations show unread again after a restart. */ }
    }

    /// <summary>For the offline checks: waits for the save under way, if any.</summary>
    public async Task FlushAsync()
    {
        await saving.WaitAsync();
        saving.Release();
        Task timer;
        lock (gate) timer = flushing;
        await timer;
        Task shared;
        lock (gate) shared = sharedSaving;
        await shared;
    }

    // ---- A client of a Messages Server (stage 6, slice 7): everything below runs only when PrivateState.Shared is not null ----

    private sealed record Change(Func<Dictionary<string, DateTimeOffset>, bool> Apply, long Tag);
    // Under the lock: the changes the host has not taken yet, in the order they were made; and those made before the row was read, which are made over it then.
    private readonly List<Change> unconfirmed = [];
    private readonly List<Change> beforeLoad = [];
    // The chain of writes: each lands after the one before, so the last change is the one the replica holds.
    private Task sharedSaving = Task.CompletedTask;
    // The replica could not bring the marks to the host's row (AbandonAsync, or a row this build cannot read): no change is made until the row is read again.
    private bool stale;
    // The host's row handed over while the load that registered the follower was still between its read and its assignment: newer than what that load read.
    private Dictionary<string, DateTimeOffset>? handed;
    private string? loadingFor;
    // Marks made (or held) while the host did not keep the row: merged by the later moment over the host's row when it comes, so the marks of a device from
    // before version 7 are not replaced by another device's row (CatchUpAsync). Each with the tag of the change that made it, so a write the host took clears it.
    private readonly Dictionary<string, (DateTimeOffset At, long Tag)> unshared = new(StringComparer.Ordinal);
    // The write of the marks is made at most once in SharedWriteDelay however many marks come (each is the whole row, up to the cap): the changes are kept at once,
    // and the newest tag is what the one write carries.
    private bool flushScheduled;
    private Task flushing = Task.CompletedTask;
    private long lastTag;

    /// <summary>How long a mark waits for others before the row is written (client of a server only). Settable so the offline checks need not sit through it.</summary>
    public TimeSpan SharedWriteDelay { get; init; } = TimeSpan.FromSeconds(2);

    // Under the lock, after a change was made on the marks held: kept to be made again over the host's row if the write of it loses.
    private Change Record(Func<Dictionary<string, DateTimeOffset>, bool> apply)
    {
        var change = new Change(apply, SharedRowTags.Next());
        if (!loaded || stale) beforeLoad.Add(change);
        else Keep(change);
        return change;
    }

    // Under the lock.
    private bool SharesNow() => profile is { } owner && state!.Shares(owner, Category, Id);

    private void NoteUnshared(string chatId, DateTimeOffset at, long tag)
    {
        if (unshared.TryGetValue(chatId, out var held)) unshared[chatId] = (held.At >= at ? held.At : at, Math.Max(held.Tag, tag));
        else unshared[chatId] = (at, tag);
    }

    // Under the lock, loaded. Kept only while the host keeps the row (protocol 7 and later): before that it stays here, nothing is confirmed, and nothing needs
    // making again.
    private void Keep(Change change)
    {
        if (profile is null) return;
        if (SharesNow()) unconfirmed.Add(change);
        lastTag = Math.Max(lastTag, change.Tag);
        if (flushScheduled) return;
        flushScheduled = true;
        flushing = FlushLaterAsync();
    }

    private async Task FlushLaterAsync()
    {
        try
        {
            if (SharedWriteDelay > TimeSpan.Zero) await Task.Delay(SharedWriteDelay).ConfigureAwait(false);
            else await Task.Yield();
        }
        catch (Exception) { /* a timer that fails writes now */ }
        Task write;
        lock (gate)
        {
            flushScheduled = false;
            // Nothing is written over a row this device could not bring up to date; the read that follows writes what was made meanwhile.
            if (stale || !loaded || profile is not { } owner) return;
            write = sharedSaving = WriteSharedAsync(sharedSaving, owner, new Dictionary<string, DateTimeOffset>(marks, StringComparer.Ordinal), lastTag);
        }
        await write.ConfigureAwait(false);
    }

    private async Task WriteSharedAsync(Task previous, string owner, Dictionary<string, DateTimeOffset> snapshot, long tag)
    {
        try { await previous.ConfigureAwait(false); } catch (Exception) { /* That change's own writer heard about it. */ }
        try { await Task.Run(() => state!.WriteAsync(owner, Category, Id, snapshot, tag)).ConfigureAwait(false); }
        catch (Exception) { /* Unsaved marks only mean those conversations show unread again after a restart. */ }
    }

    private async Task EnsureLoadedSharedAsync()
    {
        await loading.WaitAsync();
        try
        {
            var next = await state!.ProfileAsync();
            lock (gate) if (loaded && next == profile && !stale) return;
            // A write still queued lands first, so a row read again never comes back without this device's latest change.
            Task queued;
            lock (gate) { queued = sharedSaving; loadingFor = next; handed = null; }
            try { await queued; } catch (Exception) { /* That change's own writer heard about it. */ }
            Dictionary<string, DateTimeOffset>? row = null;
            var readable = true;
            if (next is not null)
            {
                // The row is read and this becomes its follower in one step, so a host row the replica takes after the read is handed over (CatchUpAsync)
                // and none taken before it is missed.
                try { row = await state.FollowAsync<Dictionary<string, DateTimeOffset>>(next, Category, Id, new Follower(this, next)); }
                catch (Exception) { readable = false; /* Marks that cannot be read only mean those conversations show unread again. */ }
            }
            Task? write = null;
            lock (gate)
            {
                // A profile change starts from what that profile saved; the old profile's marks do not carry over.
                if (loaded && next != profile) unconfirmed.Clear();
                var host = Clean(handed ?? row);
                handed = null; loadingFor = null;
                Replace(host);
                // What was read here before the row was, made over it now, and written.
                var replayed = new List<Change>();
                foreach (var change in beforeLoad)
                    if (change.Apply(marks)) replayed.Add(change);
                beforeLoad.Clear();
                profile = next; loaded = true; stale = !readable;
                var shares = next is not null && state.Shares(next, Category, Id);
                // A host that does not keep the row yet: what this device holds is its own, merged over the host's row when it comes.
                if (!shares) foreach (var (chatId, at) in marks) NoteUnshared(chatId, at, SharedRowTags.Next());
                if (replayed.Count > 0 && next is not null && readable)
                {
                    if (shares) unconfirmed.AddRange(replayed);
                    lastTag = Math.Max(lastTag, replayed[^1].Tag);
                    write = sharedSaving = WriteSharedAsync(sharedSaving, next, new Dictionary<string, DateTimeOffset>(marks, StringComparer.Ordinal), replayed[^1].Tag);
                }
            }
            if (write is not null) await write;
        }
        finally { loading.Release(); }
    }

    private static Dictionary<string, DateTimeOffset> Clean(Dictionary<string, DateTimeOffset>? row)
    {
        var clean = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var (chatId, at) in row ?? []) if (!string.IsNullOrEmpty(chatId)) clean[chatId] = at;
        Trim(clean);
        return clean;
    }

    private static bool Same(Dictionary<string, DateTimeOffset> a, Dictionary<string, DateTimeOffset> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var at) && at == pair.Value);

    // The host's row, handed over by the replica: each change still kept is made again over it, as it was made here (a mark is the later moment, a drop leaves a
    // newer mark). When anything of them is left, the result is written before this returns; the writes of changes made before are awaited first (they were
    // made over the old row and land ahead of this one), so the replica sends nothing in between.
    private async Task<SharedRowCaughtUp> CatchUpAsync(string owner, byte[]? current)
    {
        Task queued;
        lock (gate) queued = sharedSaving;
        try { await queued.ConfigureAwait(false); } catch (Exception) { /* That change's own writer heard about it. */ }
        var host = Clean(current is null ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(current)
            ?? throw new InvalidDataException("The read-here marks could not be read."));
        Task? write = null;
        SharedRowCaughtUp outcome;
        lock (gate)
        {
            // A read of this row is under way (the first, or a stale one read again): it takes this row, which is newer than the one it read, and nothing is
            // replaced here from the older one. Asked before anything else, since the same profile is loaded then.
            if (owner == loadingFor)
            {
                handed = host;
                return new(false, 0);
            }
            if (owner != profile || !loaded) return new(false, 0);
            var next = new Dictionary<string, DateTimeOffset>(host, StringComparer.Ordinal);
            var kept = new List<Change>();
            // The changes the host has not taken, and those held back while the row was read again (stale), made over the host's row in the order they were made.
            var replay = unconfirmed.Concat(beforeLoad).ToList();
            foreach (var change in replay)
                if (change.Apply(next)) kept.Add(change);
            beforeLoad.Clear();
            // Marks made before the host kept the row merge in by the later moment, whatever was recorded of how.
            var folded = false;
            foreach (var (chatId, held) in unshared) folded |= Put(next, chatId, held.At);
            unshared.Clear();
            var through = unconfirmed.Count > 0 ? unconfirmed[^1].Tag : 0;
            unconfirmed.Clear();
            if ((kept.Count > 0 || folded) && !Same(next, host))
            {
                unconfirmed.AddRange(kept);
                Replace(next);
                var tag = Math.Max(lastTag, kept.Count > 0 ? kept[^1].Tag : 0);
                lastTag = tag;
                write = sharedSaving = WriteSharedAsync(sharedSaving, owner, new Dictionary<string, DateTimeOffset>(next, StringComparer.Ordinal), tag);
                outcome = new(true, through);
            }
            else
            {
                // Nothing of this device's is left to send (it is in the host's row already, or was dropped over it): the host's row as it is.
                Replace(host);
                outcome = new(false, through);
            }
            stale = false;
        }
        if (write is not null) await write.ConfigureAwait(false);
        else
        {
            // The writes of changes made before, which the replica drops now, have all landed.
            Task landed;
            lock (gate) landed = sharedSaving;
            try { await landed.ConfigureAwait(false); } catch (Exception) { /* That change's own writer heard about it. */ }
        }
        return outcome;
    }

    // Under the lock.
    private void Replace(Dictionary<string, DateTimeOffset> with)
    {
        marks.Clear();
        foreach (var (chatId, at) in with) marks[chatId] = at;
    }

    private void Confirm(long tag)
    {
        lock (gate)
        {
            unconfirmed.RemoveAll(change => change.Tag <= tag);
            foreach (var chatId in unshared.Where(pair => pair.Value.Tag <= tag).Select(pair => pair.Key).ToArray()) unshared.Remove(chatId);
        }
    }

    // The replica could not bring the marks to the host's row: the changes kept go, nothing changes until the row is read again, and every write made before
    // has landed when this returns (the replica then drops them).
    private async Task AbandonAsync(string owner)
    {
        Task queued;
        lock (gate)
        {
            if (owner != profile) return;
            stale = true;
            // Held back to be made again over the row when it is read, not let go: a private read must not be left to show unread.
            beforeLoad.AddRange(unconfirmed);
            unconfirmed.Clear();
            queued = sharedSaving;
        }
        try { await queued.ConfigureAwait(false); } catch (Exception) { /* That change's own writer heard about it. */ }
    }

    /// <summary>How many changes are kept to be made again over the host's row. For the offline checks.</summary>
    public int Unconfirmed { get { lock (gate) return unconfirmed.Count; } }

    private sealed class Follower(ReadHereMarks owner, string profile) : ISharedRowFollower
    {
        public Task<SharedRowCaughtUp> CatchUpAsync(byte[]? current, CancellationToken cancellationToken) => owner.CatchUpAsync(profile, current);
        public void Confirmed(long tag) => owner.Confirm(tag);
        public Task AbandonAsync() => owner.AbandonAsync(profile);
    }
}
