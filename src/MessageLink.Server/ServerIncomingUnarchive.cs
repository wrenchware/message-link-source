using System.Collections.Concurrent;
using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>What a server needs to run the incoming unarchive itself.</summary>
/// <param name="State">The server's own private state (the backends' <c>BackendServices.ServerTokensKey</c> one, <c>state.db</c> at the server's root), where its
/// <c>incoming-unarchive</c> watermarks are kept. Never the shared state: that is no shared category, so no client ever holds or writes these rows.</param>
/// <param name="Merges">The clients' merges, read from the server's shared state: with them the server also takes the one-to-one conversations
/// outside every merge, while every connection speaks <see cref="ProtocolVersion.ServerUnarchivesChats"/> or later. <see cref="ProtocolServer.Create"/> gives
/// them to a server that keeps a shared state and an identity; without them the server takes groups alone, as at protocol 4.</param>
internal sealed record ServerUnarchive(PrivateState State, TimeProvider? Clock = null, SharedMergeMembership? Merges = null);

/// <summary>
/// The incoming unarchive of the client modes for group conversations and, from protocol 5, for the one-to-one conversations outside every merge, run once
/// by the server over the providers' feed rather than once by every client (with two clients each would ask). It is the window's own
/// <see cref="IncomingMessageUnarchiver"/>, taking what <see cref="IncomingUnarchiveSplit.ServerTakes"/> gives the server, in the same queue the window runs it in
/// (<see cref="NotificationRealtimeFeed"/>, without notifications). A client connected at <see cref="ProtocolVersion.ServerUnarchives"/> or later takes the
/// rest (its merges always); the in-process app and the loopback take all.
/// </summary>
/// <remarks>
/// <para><b>At most once.</b> The watermark is written before anything is asked (as in the window), so a replay of the same arrival, in this run or after a
/// restart, stops at it. The ask itself goes through the server's <see cref="OperationJournal"/> under the arrival's id (the event's, the same id a client uses), like a client's
/// mutation: a repeat answers the first outcome without asking again, and one that was running when the server died is <c>outcome_unknown</c> after the
/// restart, never made again. Nothing here retries.</para>
/// <para><b>Older clients.</b> A client below version 4 still returns groups to the inbox itself, and one below 5 its one-to-one conversations. While one is
/// connected the server leaves those to it (groups below 4, one-to-one conversations below 5), so the two never both ask. What is left open: an older client
/// connected but not running its unarchiver at that moment leaves a conversation archived, two older clients both ask (as before protocol 4), and an arrival
/// the server has begun as an older client connects can be asked for by both.</para>
/// <para><b>What the window knew.</b> The provider's archive is the router's to say (<c>chats.settings</c>), as it was for the window. Instagram's "archive
/// on this PC" (<c>chatpref:</c> rows) is never a provider archive and lifts itself when a client reads a newer arrival, so the server reads no preference row.
/// No merge holds a group, so groups need no merges. A one-to-one conversation is the server's only when the shared merges row does not hold it: a
/// merge's archive is that row's, which each client returns itself. A merge made on a client and not yet on the server can meet an arrival as it goes up:
/// the server then returns the conversation on its service and that client its merge, which is one provider ask, not two (the merge is the client's own row).
/// An unmerge not yet on the server is the client's to return (MergedConversations.ServerMayHold), so nobody leaves it archived.</para>
/// <para><b>Never holding the feed.</b> Every event goes on to the log at once. The unarchive's own queue drops what does not fit rather than wait (counted in
/// the log), and while the clients take one-to-one conversations, one seen once is never read again for it.</para>
/// </remarks>
internal static class ServerIncomingUnarchive
{
    /// <summary>
    /// The start of every journal id the server makes for its own unarchive: the clients' own (<see cref="MessagingProxy.UnarchiveOperationPrefix"/>), on purpose.
    /// The rest of the id names the arrival alone, so when a client and the server both take one arrival (a client's unmerge on its way to a server that already
    /// holds it, MergedConversations.ServerMayHold) their two asks are one operation in this journal, and the provider is asked once.
    /// </summary>
    public const string OperationPrefix = MessagingProxy.UnarchiveOperationPrefix;

    // A bound on the one-to-one conversations remembered as not the server's; past it the memory starts again, which costs one read each.
    private const int KnownLimit = 10_000;

    /// <summary>The providers' feed with the server's incoming unarchive behind it: every event passes through at once, and a conversation it unarchived is announced
    /// to every client as changed, in the log's own order.</summary>
    public static IRealtimeFeed Over(IRealtimeFeed providers, IChatSettingsBackend settings, IMessageSender messages, OperationJournal journal,
        ConnectionRoster roster, ServerUnarchive options, Action<string> log)
    {
        var notGroups = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        int deferredLogged = 0, chatsLogged = 0;
        var merges = options.Merges;
        // Whether the server takes one-to-one conversations now: it reads the merges, and no client below 5 is taking them itself.
        bool TakesChats() => merges is not null && !Deferred(roster, ProtocolVersion.ServerUnarchivesChats, "chats ", ref chatsLogged, log);
        var unarchiver = new IncomingMessageUnarchiver(settings, messages, options.State, options.Clock)
        {
            // Before the read: any conversation while an older client does them all itself, or one already known not to be a group while the clients
            // take those.
            Skips = chatId => Deferred(roster, ProtocolVersion.ServerUnarchives, "", ref deferredLogged, log) || notGroups.ContainsKey(chatId) && !TakesChats(),
            Takes = chat =>
            {
                if (IncomingUnarchiveSplit.ServerTakes(chat)) return true;
                // Whether a merge holds it is read next (TakesAsync).
                if (TakesChats()) return true;
                if (notGroups.Count >= KnownLimit) notGroups.Clear();
                notGroups.TryAdd(chat.Id, 0);
                return false;
            },
            TakesAsync = merges is null ? null : async (chat, cancellationToken) =>
            {
                if (IncomingUnarchiveSplit.ServerTakes(chat)) return true;
                // In the inbox on its service, nothing is asked whoever takes it; taken, its arrivals are only noted (a reaction after an archive then
                // cannot undo it). The merges are read only for a conversation there could be something to return.
                if (!chat.IsArchived) return true;
                bool held;
                try { held = await merges.HoldsAsync(chat, cancellationToken).ConfigureAwait(false); }
                catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // Not knowing, the provider is not asked: an archived conversation stays archived, rather than one a merge holds being returned on its
                    // service under the merge.
                    Write(log, $"incoming unarchive via=host merges=unreadable error={failure.GetType().Name}");
                    return false;
                }
                return IncomingUnarchiveSplit.ServerTakes(chat, takesChats: true, inMerge: held);
            },
            Submit = (chatId, attempt, cancellationToken) => SubmitAsync(settings, journal, chatId, attempt, log, cancellationToken),
            Log = line => Write(log, line),
        };
        return new NotificationRealtimeFeed(providers, null, unarchiver) { Skipped = count => Write(log, $"incoming unarchive via=host skipped={count}") };
    }

    // Whether a client older than `version` is connected, logged when the count changes. Called only from the unarchive's one worker.
    private static bool Deferred(ConnectionRoster roster, int version, string what, ref int logged, Action<string> log)
    {
        var older = roster.Below(version);
        if (older != logged) Write(log, $"incoming unarchive via=host {what}older_clients={older}");
        logged = older;
        return older > 0;
    }

    // As chats.set would be served: the same params and fingerprint, through the same journal, so a repeat of the same attempt id is answered, not made.
    private static async Task<ChatSummary> SubmitAsync(IChatSettingsBackend settings, OperationJournal journal, string chatId, string attempt, Action<string> log,
        CancellationToken cancellationToken)
    {
        var id = OperationPrefix + attempt;
        var request = JsonSerializer.SerializeToElement(new ChatSetParams(id, chatId, ChatSetting.Archived, false), ProtocolJson.Options);
        try
        {
            var answer = await journal.ExecuteAsync(id, OperationJournal.Fingerprint(ProtocolMethods.ChatsSet, request), async token =>
                JsonSerializer.SerializeToUtf8Bytes(await settings.SetAsync(chatId, ChatSetting.Archived, false, token).ConfigureAwait(false), ProtocolJson.Options),
                cancellationToken).ConfigureAwait(false);
            var updated = JsonSerializer.Deserialize<ChatSummary>(answer, ProtocolJson.Options) ?? throw new InvalidDataException();
            Write(log, $"incoming unarchive via=host outcome=done archived={(updated.IsArchived ? "true" : "false")}");
            return updated;
        }
        catch (ProtocolException failure)
        {
            // The code is a fixed word of the protocol's; one this build does not know is not repeated.
            Write(log, $"incoming unarchive via=host outcome=failed code={(ProtocolErrors.IsKnown(failure.Error.Code) ? failure.Error.Code : "other")}");
            throw;
        }
    }

    private static void Write(Action<string> log, string line)
    {
        try { log(line); } catch (Exception) { /* a logger that fails must not fail the feed */ }
    }
}
