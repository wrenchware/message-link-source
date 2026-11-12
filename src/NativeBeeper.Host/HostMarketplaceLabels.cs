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
/// it has labelled and which label it made, and never labels one twice or makes a deleted label again.
/// </summary>
/// <remarks>
/// <para><b>Where.</b> The book is the clients' shared row, written here as a client's <c>state.put</c> writes it: conditional on the rev it was read at, so
/// an edit another device made meanwhile is never written over; a lost write is made again from the new book. The memory is a row of the host's own private
/// state (<c>auto-labels:</c>, never a shared category), and goes into a server backup with the rows LocalStateBackup lists.</para>
/// <para><b>When.</b> A conversation is looked at when an event names it, once per run (its kind never changes), and the first page of conversations once when
/// Messenger connects, so one that already existed is labelled too. Every event passes on at once; the work is queued behind it and dropped, never waited
/// for, when the queue is full.</para>
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

    /// <summary>What the host remembers: the label it made (or found by name), and the rows it has put it on, oldest first.</summary>
    public sealed record Memory(string? LabelId, string[] Labelled);

    private readonly IRealtimeFeed inner;
    private readonly IChatReader chats;
    private readonly SharedStateStore store;
    private readonly Func<CancellationToken, Task<string?>> localKey;
    private readonly PrivateState state;
    private readonly SharedMergeMembership? merges;
    private readonly Action<string> log;
    private readonly Channel<string?> queue = Channel.CreateBounded<string?>(new BoundedChannelOptions(QueueLimit) { FullMode = BoundedChannelFullMode.DropWrite });
    // Whether each conversation looked at is a Marketplace one: a conversation's kind never changes, so each is read once.
    private readonly ConcurrentDictionary<string, bool> known = new(StringComparer.Ordinal);
    private int swept;

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
            await stop.CancelAsync().ConfigureAwait(false);
            try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
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

    /// <summary>Labels one Marketplace conversation if it was never labelled. Returns whether it is settled (labelled now or before, or the label deleted).</summary>
    internal async Task<bool> LabelAsync(ChatSummary chat, CancellationToken cancellationToken)
    {
        // Nothing is signed in and nothing is pinned: there is no book to write to yet.
        if (await localKey(cancellationToken).ConfigureAwait(false) is not { } key || NotificationIdentity.ProfileOf(key) is not { } profile) return false;
        var row = merges is null ? chat.Id : await merges.DisplayIdAsync(chat, cancellationToken).ConfigureAwait(false);
        var memory = await state.ReadAsync<Memory>(profile, MemoryCategory, MemoryRow).ConfigureAwait(false) ?? new Memory(null, []);
        if (memory.Labelled.Contains(row, StringComparer.Ordinal)) return true;
        var bookKey = SharedCategories.KeyOf(profile, SharedCategories.Labels, SharedCategories.LabelsRow);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var stored = (await store.LiveRowsWithRevAsync(bookKey, cancellationToken).ConfigureAwait(false)).FirstOrDefault(found => found.Key == bookKey);
            var book = stored.Value is { } bytes ? JsonSerializer.Deserialize<ChatLabelBook>(bytes) ?? Empty() : Empty();
            var labels = (book.Labels ?? []).ToList();
            var assignments = new Dictionary<string, string[]>(book.Assignments ?? [], StringComparer.Ordinal);
            string labelId;
            if (memory.LabelId is { } made)
            {
                // The label made before is gone: the user deleted it, and it is never made again.
                if (!labels.Any(label => label.Id == made))
                {
                    await RememberAsync(profile, memory, row, "deleted", cancellationToken).ConfigureAwait(false);
                    return true;
                }
                labelId = made;
            }
            else if (labels.FirstOrDefault(label => string.Equals(label.Name, LabelName, StringComparison.OrdinalIgnoreCase)) is { } named) labelId = named.Id;
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
            var put = await store.PutAsync(new StatePutParams("host-labels-" + Guid.NewGuid().ToString("N"), [new SharedWrite(bookKey, next, stored.Rev)]),
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
