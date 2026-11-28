namespace MessageLink.Protocol;

/// <summary>How a proxy answers a member that has no call on the wire.</summary>
public enum SyncAnswer
{
    /// <summary>From the state the server pushes (<see cref="ServerStateCache"/>).</summary>
    Pushed,
    /// <summary>From the pushed state where it is a flag of the scope, and from a prefetched copy where it depends on the conversation.</summary>
    PushedAndPrefetched,
    /// <summary>
    /// From a client-side copy of the server's answer to a method (<see cref="Remembered{TKey, TValue}"/>): the conservative answer until the
    /// server has been asked, then the server's, refreshed in the background. For a member that a window reads on its own thread and so cannot
    /// wait on a round trip for.
    /// </summary>
    Prefetched,
    /// <summary>By a rule in Core, applied on the client to what the caller hands it.</summary>
    Local,
    /// <summary>Sent on the wire without waiting for the answer: an action the caller needs nothing back from.</summary>
    FireAndForget,
}

/// <param name="Answer">Where the answer comes from.</param>
/// <param name="Reason">What the member depends on, and for a prefetched one the method behind it.</param>
public sealed record ProxyMember(SyncAnswer Answer, string Reason);

/// <summary>
/// Every synchronous member of the ports the proxies stand in for, and how each is answered. The members that are not async and not an
/// event have no request to carry them, so each is part of the server's pushed state, a copy of the server's answer to a method, a rule the
/// client applies itself, or an action sent without waiting. None asks an object in the server's process (since 2.6d there is no such
/// answer). The harness walks the ports by reflection and fails when a member is here and not on a port, or on a port and not here: a
/// new synchronous member cannot appear without someone deciding which it is. Keys are <c>Interface.Member</c>.
/// </summary>
public static class ProxyMembers
{
    public static IReadOnlyDictionary<string, ProxyMember> Synchronous { get; } = new Dictionary<string, ProxyMember>(StringComparer.Ordinal)
    {
        // ---- The routers' ports ----
        ["IProviderConnectionState.AnyConnected"] = new(SyncAnswer.Pushed, "Any scope connected: derived from the scopes' flags."),
        ["IProviderConnectionState.IsConnected"] = new(SyncAnswer.Pushed, "The connected flag of the scope the conversation id names."),
        ["IProviderConnectionState.IsScopeConnected"] = new(SyncAnswer.Pushed, "A flag per scope."),
        ["IProviderConnectionState.IsConfigured"] = new(SyncAnswer.Pushed, "A flag per scope: a session is saved."),
        ["IProviderConnectionState.NeedsSignInOnly"] = new(SyncAnswer.Pushed, "A flag of the Google scope."),
        ["IMessagePeek.HoldsPartialCopies"] = new(SyncAnswer.Pushed, "A flag per scope (Instagram keeps some messages in part)."),
        ["IMessageActions.HasLocalActionOptions"] = new(SyncAnswer.Pushed, "A constant of the host."),
        ["IFirstMessageStarter.MaxFirstMessageLength"] = new(SyncAnswer.Pushed, "A constant of the host."),
        ["IGroupManagement.GroupNameMaximum"] = new(SyncAnswer.Pushed, "A constant of the host."),
        ["IGroupManagement.AddMaximum"] = new(SyncAnswer.Pushed, "A constant of the host."),
        ["IAttachmentBackend.CanSave"] = new(SyncAnswer.Pushed, "A flag per scope, found by the scope the attachment id names; a draft made on the client is its own."),
        ["IAttachmentBatchBackend.DrawsPhotosAtOnce"] = new(SyncAnswer.Pushed, "A flag of the Google scope, and the shape of the files (the host's batch limit is a constant)."),
        ["IHistoryWindowReader.CanReadWindow"] = new(SyncAnswer.PushedAndPrefetched, "Messenger always, Instagram and Voice never; Google's depends on which conversations its archive has crawled: a copy of history.canReadWindow, asked for when the conversation is opened."),
        ["IAttachmentBatchBackend.CanSendBatch"] = new(SyncAnswer.Prefetched, "Needs the conversation's own send permission (read-only, encrypted and not registered): a copy of attachments.canSendBatch for the same conversation and files, kept when CanSendBatchAsync (what the window asks) gets its answer; no until the host has been asked."),
        ["IDirectChatLookup.FindDirectChat"] = new(SyncAnswer.Prefetched, "A search of the chats the host has loaded for this person: a copy of contacts.findDirect, asked for the people a listing returns before the listing is handed on. No existing conversation until the host has answered."),
        ["IGroupManagement.IsMember"] = new(SyncAnswer.Local, "A pure function of the chat's participants and a person: Core's MessengerGroupMembership, applied on the client."),
        ["ISearchScopeInfo.SearchScopeDescription"] = new(SyncAnswer.Prefetched, "Text that follows the indexing job (it would be a new state event every few seconds): a copy of search.scopeDescription, refreshed in the background when it is read."),

        // ---- The typing port (TypingProxy) ----
        ["ITypingService.CanReceive"] = new(SyncAnswer.Pushed, "Carried in the state (Typing): any provider can receive typing."),
        ["ITypingService.CanSend"] = new(SyncAnswer.Pushed, "Carried in the state (Typing): any provider can send typing."),

        // ---- The account ports ----
        ["IMessengerAccount.State"] = new(SyncAnswer.Pushed, "The snapshot, as the host sees it."),
        ["IInstagramAccount.State"] = new(SyncAnswer.Pushed, "The snapshot, as the host sees it."),
        ["IGoogleMessagesAccount.State"] = new(SyncAnswer.Pushed, "The snapshot, as the host sees it."),
        ["IGoogleVoiceAccounts.Accounts"] = new(SyncAnswer.Pushed, "The accounts' snapshots."),
        ["IGoogleVoiceAccounts.KnownAccountKeys"] = new(SyncAnswer.Pushed, "The keys of listed and saved accounts."),
        ["IInstagramAccount.AnswersFromMemory"] = new(SyncAnswer.Prefetched, "Per message, and about what the host holds in memory: a copy of accounts.instagram.answersFromMemory. Not in memory until the host has answered, so Up never reads a thread on a key press."),
        ["IGoogleMessagesAccount.SimFor"] = new(SyncAnswer.Prefetched, "A SIM per conversation: a copy of accounts.google.simFor, no SIM until the host has answered; a different answer raises SimsChanged."),
        ["IGoogleMessagesAccount.WarmArchive"] = new(SyncAnswer.FireAndForget, "Best-effort action (accounts.google.warmArchive) with nothing to wait for."),
        ["IGoogleMessagesAccount.KnownConversations"] = new(SyncAnswer.Prefetched, "Conversations, not flags: a copy of accounts.google.knownConversations, refreshed in the background when it is read after its lifetime."),

        // ---- Kept history (KeptHistoryProxy) ----
        ["IKeptHistorySearch.Covers"] = new(SyncAnswer.Local, "A fixed fact of the kept source (search.query asks for Messenger's own store); it names the service in the scope line."),
        ["IKeptHistorySearch.Warm"] = new(SyncAnswer.FireAndForget, "Best-effort action (search.warmKept) with nothing to wait for."),
    };
}
