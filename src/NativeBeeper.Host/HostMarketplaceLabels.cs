using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host;

/// <summary>
/// Puts the user's own "Marketplace" label on each Facebook Marketplace conversation once (owner, 2026-11-12), run by a server's host over the providers'
/// feed so there is one labeller and one memory for every device. The label is an ordinary one in the shared label book (<see cref="ChatLabels"/>): the user
/// renames, recolors or deletes it, or takes it off a conversation, from any device, and each of those sticks, because the host remembers which conversations
/// it has labelled and which label it made, and never labels one twice.
/// </summary>
/// <remarks>
/// <para><b>Deleted and back.</b> Deleting the label stops the labelling: the host makes no label of its own again. A label the user then names
/// "Marketplace" (made anew or renamed) brings it back (owner, 2026-11-12, rather than a setting every device would need): the host takes that label as its own,
/// and puts it on the Marketplace conversations in the newest page that do not have it, those that came while it was gone included, but never on one the user
/// had taken it off. That is why the host watches the book's commits and keeps which labelled conversations no longer have the label (<see cref="Memory.Removed"/>):
/// once the label is deleted the book no longer says.</para>
/// <para><b>Where.</b> The book is the clients' shared row, written here as a client's <c>state.put</c> writes it: conditional on the rev it was read at, so
/// an edit another device made meanwhile is never written over; a lost write is made again from the new book. The memory is a row of the host's own private
/// state (<c>auto-labels:</c>, never a shared category), and goes into a server backup with the rows LocalStateBackup lists.</para>
/// <para><b>When.</b> A conversation is looked at when an event names it, once per run (its kind never changes), and the first page of conversations once when
/// Messenger connects, so one that already existed is labelled too, and again when the label comes back. Every event passes on at once; the work is queued
/// behind it and dropped, never waited for, when the queue is full.</para>
/// <para><b>Logs.</b> Fixed words and counts only.</para>
/// </remarks>
internal sealed class HostMarketplaceLabels : IRealtimeFeed
{
    public const string LabelName = "Marketplace";
    public const string MemoryCategory = "auto-labels";
    public const string MemoryRow = "marketplace";
    private const int QueueLimit = 256;
    private const int KnownLimit = 10_000;
    private const int RememberedLimit = 2_000;
    private const int Attempts = 4;
    // In the queue beside the conversation ids (which all start "messenger:"): look at the book after a commit changed it.
    private const string BookCheck = "book";

    /// <summary>
    /// What the host remembers: the label it made (or found by name), the rows it has put it on, oldest first, and which of those the user has taken it off
    /// since. <paramref name="Stopped"/>: the label was deleted, and until a label is named "Marketplace" again nothing is labelled or made; the rows labelled
    /// before are forgotten then, except the ones taken off.
    /// </summary>
    public sealed record Memory(string? LabelId, string[] Labelled, string[]? Removed = null, bool Stopped = false);

    private readonly IRealtimeFeed inner;
    private readonly IChatReader chats;
    private readonly SharedStateStore store;
    private readonly Func<CancellationToken, Task<string?>> localKey;
    private readonly PrivateState state;
    private readonly SharedMergeMembership? merges;
    private readonly Action<string> log;
    private readonly Channel<string?> queue = Channel.CreateBounded<string?>(new BoundedChannelOptions(QueueLimit) { FullMode = BoundedChannelFullMode.DropWrite });
    // Whether each conversation looked at is settled: false, not a Marketplace one; true, labelled now or before, or let be while the label is deleted (and
    // looked at again when it comes back). A conversation's kind never changes, so each is read once.
    private readonly ConcurrentDictionary<string, bool> known = new(StringComparer.Ordinal);
    private int swept;
    private int bookQueued;

    private HostMarketplaceLabels(IRealtimeFeed inner, IChatReader chats, SharedStateStore store, Func<CancellationToken, Task<string?>> localKey,
        PrivateState state, SharedMergeMembership? merges, Action<string> log)
    {
        this.inner = inner; this.chats = chats; this.store = store; this.localKey = localKey; this.state = state; this.merges = merges; this.log = log;
    }

    public static IRealtimeFeed Over(IRealtimeFeed inner, IChatReader chats, SharedStateStore store, Func<CancellationToken, Task<string?>> localKey,
        PrivateState state, SharedMergeMembership? merges, Action<string> log) => new HostMarketplaceLabels(inner, chats, store, localKey, state, merges, log);

    public async Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var worker = Task.Run(() => WorkAsync(stop.Token), CancellationToken.None);
        store.Committed += OnCommitted;
        try
        {
            await inner.RunAsync(async (change, token) =>
            {
                await receive(change, token).ConfigureAwait(false);
                Notice(change);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            store.Committed -= OnCommitted;
            await stop.CancelAsync().ConfigureAwait(false);
            try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    // Under the store's gate: only queued, never read here. One look covers every commit before it.
    private void OnCommitted(object? sender, SharedChange change)
    {
        if (change.Rows.Any(row => row.Key.StartsWith(SharedCategories.Labels + ":", StringComparison.Ordinal))
            && Interlocked.Exchange(ref bookQueued, 1) == 0 && !queue.Writer.TryWrite(BookCheck))
            Volatile.Write(ref bookQueued, 0);
    }

    // Only Messenger knows a Marketplace conversation. Null in the queue asks for the sweep of the first page.
    private void Notice(RealtimeEvent change)
    {
        if (change.Kind == RealtimeKind.Connected && change.Scope is { } scope && scope.StartsWith("messenger:", StringComparison.Ordinal)
            && Interlocked.Exchange(ref swept, 1) == 0)
        {
            queue.Writer.TryWrite(null);
            return;
        }
        var ids = change.Kind switch
        {
            RealtimeKind.MessageChanged when change.IsNewMessage == true && change.ChatId is { } chatId => [chatId],
            RealtimeKind.ChatChanged => change.Ids ?? (change.ChatId is { } chatId ? [chatId] : []),
            _ => [],
        };
        foreach (var id in ids)
            if (id.StartsWith("messenger:", StringComparison.Ordinal) && !known.ContainsKey(id) && !queue.Writer.TryWrite(id)) Write("dropped");
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        await foreach (var id in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (id is null)
                {
                    var page = await chats.GetChatsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                    foreach (var chat in page.Items) await LookAsync(chat, cancellationToken).ConfigureAwait(false);
                }
                else if (id == BookCheck)
                {
                    Volatile.Write(ref bookQueued, 0);
                    await CheckBookAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (!known.ContainsKey(id))
                    await LookAsync(await chats.GetChatAsync(id, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception failure)
            {
                // Not remembered as looked at, so the next event naming it tries again.
                Write($"outcome=failed error={failure.GetType().Name}");
            }
        }
    }

    private async Task LookAsync(ChatSummary chat, CancellationToken cancellationToken)
    {
        if (known.Count >= KnownLimit) known.Clear();
        if (!chat.IsMarketplace)
        {
            known[chat.Id] = false;
            return;
        }
        if (await LabelAsync(chat, cancellationToken).ConfigureAwait(false)) known[chat.Id] = true;
    }

    /// <summary>After a commit to the book: notes the conversations the label was taken off, and whether it was deleted or is back.</summary>
    internal async Task CheckBookAsync(CancellationToken cancellationToken)
    {
        if (await ProfileAsync(cancellationToken).ConfigureAwait(false) is not { } profile) return;
        var memory = await ReadMemoryAsync(profile).ConfigureAwait(false);
        // Nothing labelled yet: the first Marketplace conversation decides.
        if (memory.LabelId is null && !memory.Stopped) return;
        var (book, _) = await ReadBookAsync(profile, cancellationToken).ConfigureAwait(false);
        await SettleAsync(profile, memory, book, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Labels one Marketplace conversation if it was never labelled. Returns whether it is settled (labelled now or before, or the label deleted).</summary>
    internal async Task<bool> LabelAsync(ChatSummary chat, CancellationToken cancellationToken)
    {
        // Nothing is signed in and nothing is pinned: there is no book to write to yet.
        if (await ProfileAsync(cancellationToken).ConfigureAwait(false) is not { } profile) return false;
        var row = merges is null ? chat.Id : await merges.DisplayIdAsync(chat, cancellationToken).ConfigureAwait(false);
        var memory = await ReadMemoryAsync(profile).ConfigureAwait(false);
        if (memory.Labelled.Contains(row, StringComparer.Ordinal)) return true;
        var bookKey = SharedCategories.KeyOf(profile, SharedCategories.Labels, SharedCategories.LabelsRow);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var (book, rev) = await ReadBookAsync(profile, cancellationToken).ConfigureAwait(false);
            memory = await SettleAsync(profile, memory, book, cancellationToken).ConfigureAwait(false);
            if (memory.Labelled.Contains(row, StringComparer.Ordinal)) return true;
            // Deleted, and no label is named Marketplace again: let be, not remembered, so it is labelled if the label comes back.
            if (memory.Stopped)
            {
                Write("outcome=stopped");
                return true;
            }
            var labels = (book.Labels ?? []).ToList();
            var assignments = new Dictionary<string, string[]>(book.Assignments ?? [], StringComparer.Ordinal);
            string labelId;
            if (memory.LabelId is { } made) labelId = made;
            else if (Named(labels) is { } named) labelId = named.Id;
            else
            {
                labelId = Guid.NewGuid().ToString("N");
                var used = labels.Select(label => label.Color).ToHashSet(StringComparer.Ordinal);
                var color = (ChatLabelPalette.Colors.FirstOrDefault(c => !used.Contains(c.Key)) ?? ChatLabelPalette.Colors[labels.Count % ChatLabelPalette.Colors.Count]).Key;
                labels.Add(new ChatLabel(labelId, LabelName, color));
            }
            memory = memory with { LabelId = labelId };
            var on = assignments.GetValueOrDefault(row) ?? [];
            if (on.Contains(labelId, StringComparer.Ordinal))
            {
                await RememberAsync(profile, memory, row, "already", cancellationToken).ConfigureAwait(false);
                return true;
            }
            assignments[row] = [.. on, labelId];
            var next = JsonSerializer.SerializeToElement(new ChatLabelBook([.. labels], assignments));
            var put = await store.PutAsync(new StatePutParams("host-labels-" + Guid.NewGuid().ToString("N"), [new SharedWrite(bookKey, next, rev)]),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            switch (put.Results.Single().Status)
            {
                case SharedWriteStatus.Written:
                    await RememberAsync(profile, memory, row, "labelled", cancellationToken).ConfigureAwait(false);
                    return true;
                case SharedWriteStatus.Refused:
                    Write("outcome=refused");
                    return false;
            }
            // Another device changed the book since it was read: made again from the new one.
        }
        Write("outcome=conflict");
        return false;
    }

    /// <summary>
    /// Brings the memory up to the book: the labelled rows that lack the label are the ones the user took it off; a label that is gone stops the labelling; and
    /// while stopped, a label named Marketplace is taken as the label from then on, and the newest page is looked at again for what came meanwhile.
    /// </summary>
    private async Task<Memory> SettleAsync(string profile, Memory memory, ChatLabelBook book, CancellationToken cancellationToken)
    {
        var labels = book.Labels ?? [];
        var assignments = book.Assignments ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
        var next = memory;
        var changed = false;
        if (memory.LabelId is { } id)
        {
            if (labels.Any(label => label.Id == id))
            {
                var removed = memory.Labelled.Where(row => !(assignments.GetValueOrDefault(row) ?? []).Contains(id, StringComparer.Ordinal)).ToArray();
                if (!removed.SequenceEqual(memory.Removed ?? [], StringComparer.Ordinal))
                {
                    next = memory with { Removed = removed };
                    changed = true;
                }
            }
            else
            {
                var kept = memory.Removed ?? [];
                next = new Memory(null, kept, kept, Stopped: true);
                changed = true;
                Write("outcome=deleted");
            }
        }
        var back = false;
        if (next.Stopped && Named(labels) is { } named)
        {
            next = next with { LabelId = named.Id, Stopped = false };
            changed = back = true;
        }
        if (!changed) return memory;
        await state.WriteAsync(profile, MemoryCategory, MemoryRow, next).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (back)
        {
            Write("outcome=back");
            // What was let be while it was gone is looked at again, from the newest page now and as events name it.
            foreach (var settled in known.Where(pair => pair.Value).Select(pair => pair.Key).ToArray()) known.TryRemove(settled, out _);
            queue.Writer.TryWrite(null);
        }
        return next;
    }

    private static ChatLabel? Named(IEnumerable<ChatLabel> labels) =>
        labels.FirstOrDefault(label => string.Equals(label.Name?.Trim(), LabelName, StringComparison.OrdinalIgnoreCase));

    private async Task<string?> ProfileAsync(CancellationToken cancellationToken) =>
        await localKey(cancellationToken).ConfigureAwait(false) is { } key ? NotificationIdentity.ProfileOf(key) : null;

    private async Task<Memory> ReadMemoryAsync(string profile) =>
        await state.ReadAsync<Memory>(profile, MemoryCategory, MemoryRow).ConfigureAwait(false) ?? new Memory(null, []);

    private async Task<(ChatLabelBook Book, long Rev)> ReadBookAsync(string profile, CancellationToken cancellationToken)
    {
        var bookKey = SharedCategories.KeyOf(profile, SharedCategories.Labels, SharedCategories.LabelsRow);
        var stored = (await store.LiveRowsWithRevAsync(bookKey, cancellationToken).ConfigureAwait(false)).FirstOrDefault(found => found.Key == bookKey);
        return (stored.Value is { } bytes ? JsonSerializer.Deserialize<ChatLabelBook>(bytes) ?? Empty() : Empty(), stored.Rev);
    }

    private static ChatLabelBook Empty() => new([], []);

    private async Task RememberAsync(string profile, Memory memory, string row, string outcome, CancellationToken cancellationToken)
    {
        var labelled = memory.Labelled.Append(row).ToArray();
        if (labelled.Length > RememberedLimit) labelled = labelled[^RememberedLimit..];
        await state.WriteAsync(profile, MemoryCategory, MemoryRow, memory with { Labelled = labelled }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        Write($"outcome={outcome}");
    }

    private void Write(string words)
    {
        try { log("marketplace label " + words); } catch (Exception) { /* a logger that fails must not fail the feed */ }
    }
}
