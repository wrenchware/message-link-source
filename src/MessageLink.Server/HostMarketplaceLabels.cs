using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

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
/// <para><b>Where.</b> The book is the clients' shared row, written through the host's own view of it (<see cref="SharedSpaceView{T}"/>) as a client's
/// <c>state.put</c> writes it: conditional on the rev it was read at, so an edit another device made meanwhile is never written over; a lost write is made again
/// from the new book, a few times, since the store only refuses a write that lost and leaves the change to the writer. The memory is a row of the host's own private
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
    // The book's one row. Read strictly: a book that cannot be read is a failure, never an empty book to write over it.
    private readonly SharedSpaceView<ChatLabelBook> books;
    private readonly PrivateState state;
    private readonly SharedMergeMembership? merges;
    private readonly Action<string> log;
    private readonly Channel<string?> queue = Channel.CreateBounded<string?>(new BoundedChannelOptions(QueueLimit) { FullMode = BoundedChannelFullMode.DropWrite });
    // Whether each conversation looked at is settled: false, not a Marketplace one; true, labelled now or before, or let be while the label is deleted (and
    // looked at again when it comes back). A conversation's kind never changes, so each is read once.
    private readonly ConcurrentDictionary<string, bool> known = new(StringComparer.Ordinal);
    private int swept;
    private int bookQueued;

    private HostMarketplaceLabels(IRealtimeFeed inner, IChatReader chats, SharedStateStore store, PrivateState state, SharedMergeMembership? merges, Action<string> log)
    {
        this.inner = inner; this.chats = chats; this.store = store; this.state = state; this.merges = merges; this.log = log;
        books = store.View<ChatLabelBook>(SharedCategories.Labels, decode: bytes => JsonSerializer.Deserialize<ChatLabelBook>(bytes) ?? new ChatLabelBook([], []));
    }

    public static IRealtimeFeed Over(IRealtimeFeed inner, IChatReader chats, SharedStateStore store, PrivateState state, SharedMergeMembership? merges,
        Action<string> log) => new HostMarketplaceLabels(inner, chats, store, state, merges, log);

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
            // A conversation its service has no record of (four Messenger ones its helper answers unknown_chat to, named by every start's burst of
            // events, 2026-11-17): set aside for the run, as the history check leaves it, under the history's tag so the two lines can be matched.
            catch (MessageLink.Core.Backend.BackendException unknown)
                when (unknown.Failure == MessageLink.Core.Backend.BackendFailure.Unavailable && id is not null && id != BookCheck)
            {
                known[id] = false;
                Write($"outcome=unreadable chat={ConversationHistory.Tag(id)}");
            }
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
        if (await books.FiledAsync(cancellationToken).ConfigureAwait(false) is not { } filed) return;
        var memory = await ReadMemoryAsync(filed.Profile).ConfigureAwait(false);
        // Nothing labelled yet: the first Marketplace conversation decides.
        if (memory.LabelId is null && !memory.Stopped) return;
        var (book, _) = await ReadBookAsync(filed, cancellationToken).ConfigureAwait(false);
        await SettleAsync(filed.Profile, memory, book, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Labels one Marketplace conversation if it was never labelled. Returns whether it is settled (labelled now or before, or the label deleted).</summary>
    internal async Task<bool> LabelAsync(ChatSummary chat, CancellationToken cancellationToken)
    {
        // Nothing is signed in and nothing is pinned: there is no book to write to yet.
        if (await books.FiledAsync(cancellationToken).ConfigureAwait(false) is not { } filed) return false;
        // The host's own memory, in its private state, is kept per profile as the book is.
        var profile = filed.Profile;
        var row = merges is null ? chat.Id : await merges.DisplayIdAsync(chat, cancellationToken).ConfigureAwait(false);
        var memory = await ReadMemoryAsync(profile).ConfigureAwait(false);
        if (memory.Labelled.Contains(row, StringComparer.Ordinal)) return true;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var (book, rev) = await ReadBookAsync(filed, cancellationToken).ConfigureAwait(false);
            memory = await SettleAsync(profile, memory, book, cancellationToken).ConfigureAwait(false);
            if (memory.Labelled.Contains(row, StringComparer.Ordinal)) return true;
            // Deleted, and no label is named Marketplace again: let be, not remembered, so it is labelled if the label comes back.
            if (memory.Stopped)
            {
                Write("outcome=stopped");
                return true;
            }
            // The label it made or took, or the one the user named Marketplace, or a new one: the book's own rules for a name and a colour.
            var labelId = memory.LabelId ?? book.Ensure(LabelName, out _).Id;
            memory = memory with { LabelId = labelId };
            if (!book.Assign(row, labelId))
            {
                await RememberAsync(profile, memory, row, "already", cancellationToken).ConfigureAwait(false);
                return true;
            }
            bool written;
            try { written = await filed.WriteIfAsync(SharedCategories.LabelsRow, book.ToSaved(), rev, cancellationToken).ConfigureAwait(false); }
            // A book the store will not keep (past a row's size): nothing was written, and trying again would not change that.
            catch (ArgumentException)
            {
                Write("outcome=refused");
                return false;
            }
            if (written)
            {
                await RememberAsync(profile, memory, row, "labelled", cancellationToken).ConfigureAwait(false);
                return true;
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
    private async Task<Memory> SettleAsync(string profile, Memory memory, LabelBook book, CancellationToken cancellationToken)
    {
        var next = memory;
        var changed = false;
        if (memory.LabelId is { } id)
        {
            if (book.Find(id) is not null)
            {
                var removed = memory.Labelled.Where(row => !book.Has(row, id)).ToArray();
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
        if (next.Stopped && book.Named(LabelName) is { } named)
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

    private async Task<Memory> ReadMemoryAsync(string profile) =>
        await state.ReadAsync<Memory>(profile, MemoryCategory, MemoryRow).ConfigureAwait(false) ?? new Memory(null, []);

    // Read as a client reads it (LabelBook.From): what a client would mend is mended here too, and written so with the host's next change.
    private static async Task<(LabelBook Book, long Rev)> ReadBookAsync(SharedSpaceView<ChatLabelBook>.Filed filed, CancellationToken cancellationToken)
    {
        var stored = await filed.ReadAsync(SharedCategories.LabelsRow, cancellationToken).ConfigureAwait(false);
        return (LabelBook.From(stored?.Value), stored?.Rev ?? 0);
    }

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
