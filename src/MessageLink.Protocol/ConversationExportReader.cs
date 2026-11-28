using MessageLink.Core.Messaging;

namespace MessageLink.Protocol;

/// <summary>
/// What Export conversation writes: a conversation's messages as the server holds them (protocol 33, <see cref="HistoryOlderParams.HeldOnly"/>), and, when
/// asked, older ones read from the service a page at a time. The held read asks no service anything past the newest page an open would read; the read
/// further back is one request at a time, a pause between, bounded in messages, and never Instagram's, every page of which is a request on the real
/// account. Nothing here is kept by the client.
/// </summary>
public sealed class ConversationExportReader(ProtocolClient client)
{
    /// <summary>The most messages read further back from one service's conversation.</summary>
    public int MaxOlderMessages { get; init; } = 20_000;
    /// <summary>The pause between two reads further back, so an export is never a burst on a service. Settable for the checks.</summary>
    public TimeSpan OlderPause { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Whether the server can answer from its copy alone; below 33 the export writes what the window has loaded.</summary>
    public bool Available => client.Version >= ProtocolVersion.HeldHistory;

    public static bool MayReadOlder(string chatId) => !chatId.StartsWith("instagram:", StringComparison.Ordinal);

    /// <summary>One conversation as the server holds it: its messages oldest first, and whether the service has anything older.</summary>
    public sealed record Held(string ChatId, IReadOnlyList<ChatMessage> Messages, bool Complete);

    public async Task<Held> ReadHeldAsync(string chatId, CancellationToken cancellationToken = default)
    {
        if (!Available) throw new NotSupportedException("The server does not answer from its copy alone.");
        var newest = await client.CallAsync<HistorySyncParams, HistorySyncResult>(ProtocolMethods.HistorySync,
            new(chatId, null, WholeHistoryLimits.MaxPage), cancellationToken).ConfigureAwait(false);
        var pages = new List<IReadOnlyList<ChatMessage>> { newest.Messages };
        var oldest = newest.Messages.Count > 0 ? newest.Messages[0] : null;
        var more = newest.MoreOlder;
        var complete = newest.Complete;
        while (more && oldest is not null)
        {
            var page = await OlderAsync(chatId, oldest, heldOnly: true, cancellationToken).ConfigureAwait(false);
            complete = page.Complete;
            if (page.Messages.Count == 0) break;
            pages.Add(page.Messages);
            oldest = page.Messages[0];
            more = page.More;
        }
        pages.Reverse();
        return new(chatId, Distinct(pages.SelectMany(page => page)), complete || oldest is null && !newest.MoreOlder);
    }

    /// <summary>
    /// Older messages than <paramref name="oldest"/> from the service, read on until it has no more, <see cref="MaxOlderMessages"/> are read, or the read is
    /// stopped; <paramref name="progress"/> gets the count so far. Returns them oldest first and whether the beginning was reached.
    /// </summary>
    public async Task<(IReadOnlyList<ChatMessage> Messages, bool Complete)> ReadOlderAsync(string chatId, ChatMessage oldest, IProgress<int>? progress,
        CancellationToken stop, CancellationToken cancellationToken = default)
    {
        if (!MayReadOlder(chatId)) return ([], false);
        var pages = new List<IReadOnlyList<ChatMessage>>();
        var count = 0;
        var complete = false;
        var from = oldest;
        while (!stop.IsCancellationRequested && count < MaxOlderMessages)
        {
            HistoryOlderResult page;
            try { page = await OlderAsync(chatId, from, heldOnly: false, cancellationToken, Math.Min(WholeHistoryLimits.MaxPage, MaxOlderMessages - count)).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
            if (page.Messages.Count > 0)
            {
                pages.Add(page.Messages);
                count += page.Messages.Count;
                from = page.Messages[0];
                progress?.Report(count);
            }
            if (!page.More) { complete = page.Complete; break; }
            // More said, nothing brought: the service has not loaded that page yet. Stop rather than ask again at once.
            if (page.Messages.Count == 0) break;
            try { await Task.Delay(OlderPause, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        pages.Reverse();
        return (Distinct(pages.SelectMany(page => page)), complete);
    }

    private Task<HistoryOlderResult> OlderAsync(string chatId, ChatMessage before, bool heldOnly, CancellationToken cancellationToken, int limit = WholeHistoryLimits.MaxPage) =>
        client.CallAsync<HistoryOlderParams, HistoryOlderResult>(ProtocolMethods.HistoryOlder,
            new(chatId, before.Timestamp, before.SortKey, before.Id, limit, heldOnly ? true : null), cancellationToken);

    private static ChatMessage[] Distinct(IEnumerable<ChatMessage> messages)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return messages.Where(message => seen.Add(message.Id)).ToArray();
    }
}
