using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// Client-side stand-ins for the Core ports. Each implements its port over a ProtocolClient, and needs nothing else of the host's
// process: every member crosses as a method (ProtocolMethods.Catalog) or is answered from the server's pushed state (ServerStateCache). A
// mutation crosses with a new ClientOpId made here, once per call, and is never retried (ProtocolClient.MutateAsync). A proxy implements
// every optional interface its router does (a cast like `sender is IProviderConnectionState`, `lookup as IMessagePeek` or
// `backend as IAttachmentBatchBackend` decides what the window offers), and the harness fails when a router gains one a proxy lacks.
// Members with a default body are implemented explicitly: the default would answer for an object that is not the one that knows.
// None takes the real object (`direct`) any more: that was how the members not yet on the wire were served, and
// none is left.
//
// The synchronous members. A client on another machine cannot ask the real object, so a proxy answers them from the
// state the server pushes, a copy of the server's answer to a method, or a rule in Core. ProxyMembers.Synchronous lists every one
// and the harness fails if a new one is not classified. What each depends on:
//   a flag per connection scope  -> pushed: IProviderConnectionState.* (connected, configured, sign-in only), IMessagePeek.HoldsPartialCopies,
//                                   IAttachmentBackend.CanSave, IAttachmentBatchBackend.DrawsPhotosAtOnce, IHistoryWindowReader.CanReadWindow
//                                   (Messenger always, Instagram and Voice never); the scope comes from the id (ProviderScopes)
//   a constant of the server        -> pushed: IMessageActions.HasLocalActionOptions, IFirstMessageStarter.MaxFirstMessageLength,
//                                   IGroupManagement.GroupNameMaximum and AddMaximum
//   per-conversation data         -> prefetched: IHistoryWindowReader.CanReadWindow for Google (its archive has crawled
//                                   some conversations and not others; history.canReadWindow), IDirectChatLookup.FindDirectChat
//                                   (contacts.findDirect, asked for the people a listing returns before the listing is handed on). A
//                                   synchronous member on a window's thread cannot wait for a round trip, so it reads a copy of the
//                                   server's answer (Remembered) and says the conservative thing until the server has been asked.
//                                   IAttachmentBatchBackend.CanSendBatch (the chat's own send permission) has an async form that
//                                   crosses (attachments.canSendBatch) and is what the window asks; the synchronous one reads a copy
//                                   of that answer for the same files and says no until the server has been asked.
//   a pure function of arguments  -> local: IGroupManagement.IsMember is Core's MessengerGroupMembership rule, applied on the client.
//   text that follows a job       -> prefetched: ISearchScopeInfo.SearchScopeDescription (indexing progress; it would flood the state feed;
//                                   search.scopeDescription, refreshed in the background).
// Before the first state the cache answers conservatively (ServerStateCache); a client that has a state in hand primes it.

/// <summary>What the messaging router is to the window: the chat list, messages, sending, read state, actions, settings, connection state, peeks and history windows.</summary>
public sealed class MessagingProxy(ProtocolClient client, ServerStateCache state, TimeSpan? readableLifetime = null, Action<string>? log = null)
    : IChatReader, ILinkPreviewLoader, IMessageSender, IAdmittedReadStateWriter, IMessageActions, IMessageMutations, IChatSettingsBackend, IChatDeletion,
      IProviderConnectionState, IMessagePeek, IHistoryWindowReader
{
    // A server that keeps the whole history (protocol 24) is followed in a copy of each conversation read here, which answers the reads below without a call
    // while it is known current (HistoryCopies); otherwise each goes to the server as before.
    private readonly HistoryCopies copies = new(client, log);

    /// <summary>The copies behind the reads (for the checks).</summary>
    public HistoryCopies Copies => copies;
    // Which Google conversations the archive can read around a moment: asked for the server's answer when a conversation is opened or its
    // messages are read, so that by the time the window asks (CanReadWindow, synchronous) the answer is here.
    private readonly Remembered<string, bool> readable = new(chatId => client.CallAsync<ChatIdParams, bool>(ProtocolMethods.HistoryCanReadWindow, new(chatId)),
        false, readableLifetime ?? TimeSpan.FromSeconds(30));

    private void WarmReadable(string chatId)
    {
        if (state.WindowReadsFor(chatId) == WindowReads.PerChat) readable.Warm(chatId);
    }

    // ---- IChatReader ----
    public Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default) =>
        client.CallAsync<ChatsListParams, Page<ChatSummary>>(ProtocolMethods.ChatsList, new(olderThan), cancellationToken);
    public Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        WarmReadable(chatId);
        return client.CallAsync<ChatIdParams, ChatSummary>(ProtocolMethods.ChatsGet, new(chatId), cancellationToken);
    }
    public Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        WarmReadable(chatId);
        return copies.Enabled ? FromCopyAsync() : client.CallAsync<MessagesPageParams, Page<ChatMessage>>(ProtocolMethods.MessagesPage, new(chatId, olderThan), cancellationToken);

        async Task<Page<ChatMessage>> FromCopyAsync() =>
            (olderThan is null ? await copies.NewestAsync(chatId, cancellationToken).ConfigureAwait(false)
                : await copies.OlderAsync(chatId, olderThan, cancellationToken).ConfigureAwait(false))
            ?? await client.CallAsync<MessagesPageParams, Page<ChatMessage>>(ProtocolMethods.MessagesPage, new(chatId, olderThan), cancellationToken).ConfigureAwait(false);
    }

    // ---- ILinkPreviewLoader ----
    // Fills the host's cache and sends nothing to anyone (the helper fetches the linked page anonymously and reports the preview as a change
    // to the message). It fails the way the router does when the service is not ready, which is how the conversation knows to ask again.
    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default) =>
        client.CallAsync<LinkPreviewsParams, Done>(ProtocolMethods.MessagesLoadLinkPreviews, new(chatId, messageIds), cancellationToken);

    // ---- IMessageSender ----
    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default) =>
        client.CallAsync<ChatIdParams, SendPermission>(ProtocolMethods.ChatsSendPermission, new(chatId), cancellationToken);
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default) =>
        client.MutateAsync<SendTextParams, SendReceipt>(ProtocolMethods.MessagesSend, new(ProtocolClient.SendOperationId(), chatId, text), cancellationToken);
    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        copies.Find(chatId, messageId) is { } held ? Task.FromResult(held)
            : client.CallAsync<MessageIdParams, ChatMessage>(ProtocolMethods.MessagesResolve, new(chatId, messageId), cancellationToken);

    // ---- IReadStateWriter ----
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default) =>
        client.CallAsync<ChatIdParams, bool>(ProtocolMethods.ChatsCanMarkRead, new(chatId), cancellationToken);
    // PrivateReadingGate admits a read with an AsyncLocal that cannot cross the dispatcher, so the request carries it: the gate states it (it
    // is the only caller that passes true), and the server's end applies it before its router decides (PrivateReadingServerReadState). The
    // plain call is not admitted, so a path that reaches this proxy without the gate is refused for a conversation read privately, exactly
    // as it is by the router in one process. Not journaled: a read marker is a watermark and sending it again changes nothing.
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        MarkReadAsync(chatId, messageId, false, cancellationToken);
    public Task MarkReadAsync(string chatId, string messageId, bool admitted, CancellationToken cancellationToken = default) =>
        MarkReadAsync(chatId, messageId, admitted, false, cancellationToken);
    // `explicit` is stated only by the gate's explicit writer (a read the person asked for by name). Left out it is false: an automatic read.
    public Task MarkReadAsync(string chatId, string messageId, bool admitted, bool explicitly, CancellationToken cancellationToken = default) =>
        client.CallAsync<MarkReadParams, Done>(ProtocolMethods.ChatsMarkRead, new(chatId, messageId, admitted, explicitly), cancellationToken);

    // ---- IMessageActions ----
    public bool HasLocalActionOptions => state.Limits.HasLocalActionOptions;
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        client.CallAsync<MessageIdParams, MessageActionOptions>(ProtocolMethods.MessagesActionOptions, new(chatId, messageId), cancellationToken);
    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default) =>
        client.MutateAsync<ReplyParams, SendReceipt>(ProtocolMethods.MessagesReply, new(ProtocolClient.SendOperationId(), chatId, messageId, text), cancellationToken);
    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default) =>
        client.MutateAsync(ProtocolMethods.MessagesReact, new ReactParams(ProtocolClient.NewOperationId(), chatId, messageId, reaction, remove), cancellationToken);

    // ---- IMessageMutations ----
    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default) =>
        client.MutateAsync(ProtocolMethods.MessagesEdit, new EditParams(ProtocolClient.NewOperationId(), chatId, messageId, originalText, text), cancellationToken);
    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default) =>
        client.MutateAsync(ProtocolMethods.MessagesDelete, new DeleteParams(ProtocolClient.NewOperationId(), chatId, messageId, forEveryone), cancellationToken);

    // ---- IChatSettingsBackend, IChatDeletion ----
    public Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken = default) =>
        client.CallAsync<ChatIdParams, ChatSettings>(ProtocolMethods.ChatsSettings, new(chatId), cancellationToken);
    public Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default) =>
        client.MutateAsync<ChatSetParams, ChatSummary>(ProtocolMethods.ChatsSet, new(ProtocolClient.NewOperationId(), chatId, setting, value), cancellationToken);
    /// <summary>
    /// The start of the operation id of an incoming unarchive, a client's (<see cref="UnarchiveAsync"/>) and a server's own alike, so that asks for one arrival
    /// from either are one operation in the server's journal.
    /// </summary>
    public const string UnarchiveOperationPrefix = "unarchive:";

    /// <summary>
    /// A client of a server's incoming unarchive: <c>chats.set</c> under an id made from the arriving event (<paramref name="attempt"/>)
    /// rather than a new one, so every reader of the same arrival, another client or the server, asks with the same id and the server's journal makes them one.
    /// </summary>
    public Task<ChatSummary> UnarchiveAsync(string attempt, string chatId, CancellationToken cancellationToken = default) =>
        client.MutateAsync<ChatSetParams, ChatSummary>(ProtocolMethods.ChatsSet, new(UnarchiveOperationPrefix + attempt, chatId, ChatSetting.Archived, false), cancellationToken);
    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken = default) =>
        client.MutateAsync(ProtocolMethods.ChatsDelete, new ChatMutationParams(ProtocolClient.NewOperationId(), chatId), cancellationToken);

    // ---- IProviderConnectionState: a flag per scope, pushed by the server (ServerStateCache) ----
    public bool AnyConnected => state.AnyConnected;
    public bool IsConnected(string chatId) => state.IsConnected(chatId);
    public bool IsScopeConnected(string scope) => state.IsScopeConnected(scope);
    public bool IsConfigured(string scope) => state.IsConfigured(scope);
    public bool NeedsSignInOnly(string scope) => state.NeedsSignInOnly(scope);

    // ---- IMessagePeek: display-only and never throws in one process, so a connection that fails is a miss, not an error ----
    public async ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        if (copies.Find(chatId, messageId) is { } held) return held;
        try { return await client.CallNullableAsync<MessageIdParams, ChatMessage>(ProtocolMethods.MessagesPeek, new(chatId, messageId), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
    public async ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        // The copy holds messages as the server's copy holds them: whole.
        if (copies.Find(chatId, messageId) is { } held) return held;
        try { return await client.CallNullableAsync<MessageIdParams, ChatMessage>(ProtocolMethods.MessagesPeekWhole, new(chatId, messageId), cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
    public bool HoldsPartialCopies(string chatId) => state.HoldsPartialCopies(chatId);

    // ---- IHistoryWindowReader ----
    // Messenger always and Instagram and Voice never are flags of the scope. Google's depends on the conversation (its archive has crawled
    // some and not others), which the state cannot carry: that one reads a copy of the server's answer (history.canReadWindow), asked for
    // when the conversation was opened, and says "not readable" until the server has answered.
    public bool CanReadWindow(string chatId) => state.WindowReadsFor(chatId) switch
    {
        WindowReads.Always => true,
        WindowReads.PerChat => readable.Get(chatId),
        _ => false,
    };

    // The window comes back drawn. Its fresher copies (HistoryWindow.Refreshed, a Task) are a second call that waits for them, started
    // here the moment the window is: the server holds a token for it, so the wait is the call's own, ends with the connection and can be
    // cancelled. Nothing waits on a window nobody asked a refresh for.
    public async Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default)
    {
        var reply = await client.CallAsync<HistoryWindowParams, HistoryWindowReply>(ProtocolMethods.HistoryWindow, new(chatId, at, older, newer), cancellationToken).ConfigureAwait(false);
        var window = new HistoryWindow(reply.Messages, reply.MoreOlder, reply.MoreNewer);
        return reply.Refresh is { } token ? window with { Refreshed = RefreshedAsync(token, cancellationToken) } : window;
    }

    private async Task<IReadOnlyList<ChatMessage>> RefreshedAsync(string token, CancellationToken cancellationToken)
    {
        var fresher = await client.CallAsync<WindowRefreshParams, WindowRefreshed>(ProtocolMethods.HistoryWindowRefresh, new(token), cancellationToken).ConfigureAwait(false);
        return fresher.Messages;
    }
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default) =>
        copies.Held(chatId, after, limit) is { } held ? Task.FromResult<HeldHistory?>(held)
            : client.CallNullableAsync<HistoryHeldParams, HeldHistory>(ProtocolMethods.HistoryHeld, new(chatId, after, limit), cancellationToken);
}

/// <summary>What the directory router is to the window: contacts, new chats, groups and the members of one.</summary>
public sealed class DirectoryProxy(ProtocolClient client, ServerStateCache state, TimeSpan? lookupLifetime = null)
    : IContactBackend, IGroupBackend, IDirectChatLookup, IFirstMessageStarter, IFrequentContacts, IGroupManagement
{
    // The existing conversation with a person, as the server last said. The people a listing returns are looked up in one call before the
    // listing is handed on (Learn), so a person on screen has an answer here when the window asks FindDirectChat, which cannot wait.
    private readonly Remembered<ContactPerson, ChatSummary?> directs = new(async contact =>
    {
        var found = await client.CallAsync<FindDirectParams, FindDirectResult>(ProtocolMethods.ContactsFindDirect, new([contact])).ConfigureAwait(false);
        return found.Chats.Count == 1 ? found.Chats[0] : null;
    }, null, lookupLifetime ?? TimeSpan.FromSeconds(30));

    // Failing to look up is not failing to list: the window then sees no existing conversation, which is what a miss looks like.
    private async Task Learn(IReadOnlyList<ContactPerson>? people, CancellationToken cancellationToken)
    {
        if (people is null || people.Count == 0) return;
        var batch = people.Take(ProtocolDispatcher.MaxLookups).ToArray();
        try
        {
            var found = await client.CallAsync<FindDirectParams, FindDirectResult>(ProtocolMethods.ContactsFindDirect, new(batch), cancellationToken).ConfigureAwait(false);
            if (found.Chats.Count == batch.Length)
                for (var i = 0; i < batch.Length; i++) directs.Put(batch[i], found.Chats[i]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    // ---- IContactBackend ----
    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken = default) =>
        client.CallAsync<AccountIdParams, ContactOptions>(ProtocolMethods.ContactsOptions, new(accountId), cancellationToken);
    public async Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken = default)
    {
        var people = await client.CallAsync<ContactsFindParams, IReadOnlyList<ContactPerson>>(ProtocolMethods.ContactsFind, new(accountId, query), cancellationToken).ConfigureAwait(false);
        await Learn(people, cancellationToken).ConfigureAwait(false);
        return people;
    }
    public async Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken = default)
    {
        var started = await client.MutateAsync<StartDirectParams, ChatSummary>(ProtocolMethods.ContactsStartDirect, new(ProtocolClient.NewOperationId(), contact, allowInvite), cancellationToken).ConfigureAwait(false);
        // Now there is one, and it is this.
        directs.Put(contact, started);
        return started;
    }

    // ---- IFrequentContacts ----
    public async Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var people = await client.CallNullableAsync<AccountIdParams, IReadOnlyList<ContactPerson>>(ProtocolMethods.ContactsFrequent, new(accountId), cancellationToken).ConfigureAwait(false);
        await Learn(people, cancellationToken).ConfigureAwait(false);
        return people;
    }

    // ---- IGroupBackend ----
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken = default) =>
        client.CallAsync<AccountIdParams, GroupOptions>(ProtocolMethods.GroupsOptions, new(accountId), cancellationToken);
    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken = default) =>
        client.MutateAsync<GroupCreateParams, ChatSummary>(ProtocolMethods.GroupsCreate, new(ProtocolClient.NewOperationId(), accountId, people, title), cancellationToken);

    // ---- IDirectChatLookup, IFirstMessageStarter, IGroupManagement ----
    // A search of the chats the server has loaded for this person: read from a copy of the server's answer (see Learn), no existing
    // conversation until the server has been asked.
    public ChatSummary? FindDirectChat(ContactPerson contact) => directs.Get(contact);
    public Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken = default) =>
        client.MutateAsync<StartWithMessageParams, ChatSummary>(ProtocolMethods.ContactsStartWithMessage, new(ProtocolClient.NewOperationId(), contact, text), cancellationToken);
    public int MaxFirstMessageLength => state.Limits.MaxFirstMessageLength;
    public int GroupNameMaximum => state.Limits.GroupNameMaximum;
    public int AddMaximum => state.Limits.AddMaximum;
    // A pure function of its arguments (the chat's participants against a person): Core's rule, applied here.
    public bool IsMember(ChatSummary chat, ContactPerson person) => MessengerGroupMembership.IsMember(chat, person);
    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken = default) =>
        client.MutateAsync<GroupRenameParams, ChatSummary>(ProtocolMethods.GroupsRename, new(ProtocolClient.NewOperationId(), chatId, title), cancellationToken);
    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken = default) =>
        client.MutateAsync<GroupMembersParams, ChatSummary>(ProtocolMethods.GroupsAddMembers, new(ProtocolClient.NewOperationId(), chatId, people), cancellationToken);
    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken = default) =>
        client.MutateAsync<GroupMemberParams, ChatSummary>(ProtocolMethods.GroupsRemoveMember, new(ProtocolClient.NewOperationId(), chatId, personId), cancellationToken);
}

/// <summary>
/// What the media router is to the window. Pictures and saved files come down in chunks by reference (<c>media.read</c>); a file
/// to send goes up first under a handle and the send names it (<c>media.upload*</c>, then the journaled <c>attachments.send</c>).
/// A description of a file (its permission, its caption) crosses without its bytes.
/// </summary>
/// <remarks>
/// A send has two phases and the proxy keeps them apart. While the upload runs nothing has been asked of any provider, so a failure
/// is a plain <see cref="SendException"/> (not sent) and a cancel is an <see cref="OperationCanceledException"/>, as a direct
/// backend's cancel would be. Once the send request is handed to the connection the rules of
/// <see cref="ProtocolClient.MutateAsync{TParams, TResult}"/> apply, and nothing is retried.
/// The synchronous members: <c>CanSave</c> and <c>DrawsPhotosAtOnce</c> are answered from the server's pushed state when a cache is given
/// (<c>CanSave</c> by the scope the attachment's id names, <c>DrawsPhotosAtOnce</c> by the conversation's scope and the shape of the
/// files). <c>CanSendBatch</c> needs the conversation's own send permission (read-only, or encrypted and not registered), which is
/// per-conversation data: the window asks <c>CanSendBatchAsync</c>, which crosses (attachments.canSendBatch) and keeps its answer, and the
/// synchronous form reads that copy for the same conversation and files (no until the server has been asked). A picture the window itself drafted
/// (<c>DraftMediaStore</c>, process-wide) is the client's own, so <paramref name="savedLocally"/> answers for it before the server is asked; a
/// router that is handed the draft's attachment still finds it through the attachment's source while server and client share a process, but
/// a host on another machine will need such drafts to travel as uploads.
/// </remarks>
/// <param name="stills">
/// A picture this client makes itself for an inline read, in place of asking the server for it: given the attachment and how to read the whole file
/// from the host as saving it does, the answer, or null for the host to answer. A video's still made from the clip (2026-10-12): a Linux server
/// cannot decode video, so the client does, as the PC did in process.
/// </param>
public sealed class MediaProxy(ProtocolClient client, ServerStateCache state, Func<MessageAttachment, bool>? savedLocally = null, TimeSpan? batchLifetime = null,
    Func<MessageAttachment, byte[]?>? draftBytes = null,
    Func<MessageAttachment, Func<CancellationToken, Task<byte[]>>, CancellationToken, Task<byte[]>?>? stills = null)
    : IMediaReader, IAttachmentBackend, IAttachmentBatchBackend
{
    // Whether the server would send these files to this conversation as one batch: a copy of its answer to attachments.canSendBatch, keyed
    // by the conversation and the shape of the files (what the answer depends on), and no until the server has said.
    private readonly Remembered<BatchKey, bool> batches = new(key => client.CallAsync<CanSendBatchParams, bool>(ProtocolMethods.AttachmentsCanSendBatch, new(key.ChatId, key.Files)),
        false, batchLifetime ?? TimeSpan.FromSeconds(30));

    // The files are carried to ask with; two keys are the same question when the conversation and what describes the files are.
    private sealed class BatchKey(string chatId, AttachmentMeta[] files) : IEquatable<BatchKey>
    {
        private readonly string shape = string.Join('\n', files.Select(file => $"{file.MimeType}|{file.Length}|{file.IsVoiceNote}|{file.DurationSeconds}"));
        public string ChatId { get; } = chatId;
        public AttachmentMeta[] Files { get; } = files;
        public bool Equals(BatchKey? other) => other is not null && ChatId == other.ChatId && shape == other.shape;
        public override bool Equals(object? obj) => Equals(obj as BatchKey);
        public override int GetHashCode() => HashCode.Combine(ChatId, shape);
    }

    // A picture the window drafted for a pending send lives in this process only (DraftMediaStore); the server cannot see it, and asking
    // it logged "media failed reason=Unavailable" for every pending photo (2026-10-12). So it is answered here, as MediaRouter does in process.
    public Task<byte[]> ReadImageAsync(MessageAttachment attachment, CancellationToken cancellationToken = default, bool inline = false) =>
        draftBytes?.Invoke(attachment) is { } draft ? Task.FromResult(draft)
            : inline && stills?.Invoke(attachment, token => ReadWholeAsync(attachment, token), cancellationToken) is { } still ? still
            : MediaTransfer.ReadAsync(client, attachment, inline, cancellationToken);

    // The whole file the way playing it reads it (media.read with save), so the host fetches and keeps it once, and play finds it there.
    private async Task<byte[]> ReadWholeAsync(MessageAttachment attachment, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await MediaTransfer.CopyAsync(client, attachment, memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    public Task<SendPermission> GetPermissionAsync(string chatId, CancellationToken cancellationToken = default) =>
        client.CallAsync<ChatIdParams, SendPermission>(ProtocolMethods.ChatsAttachmentPermission, new(chatId), cancellationToken);
    // No backend reads a file's bytes to answer these (length, type and the conversation decide), so only the description crosses.
    public Task<SendPermission> GetFilePermissionAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default) =>
        client.CallAsync<AttachmentFileParams, SendPermission>(ProtocolMethods.AttachmentsFilePermission, new(chatId, Describe(file)), cancellationToken);
    public Task<AttachmentCaptionSupport> GetCaptionSupportAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default) =>
        client.CallAsync<AttachmentFileParams, AttachmentCaptionSupport>(ProtocolMethods.AttachmentsCaptionSupport, new(chatId, Describe(file)), cancellationToken);

    public async Task<SendReceipt> SendAsync(string chatId, AttachmentFile file, CancellationToken cancellationToken = default, string? caption = null)
    {
        var upload = await UploadForSendAsync(file, cancellationToken).ConfigureAwait(false);
        try
        {
            return await client.MutateAsync<AttachmentSendParams, SendReceipt>(ProtocolMethods.AttachmentsSend,
                new(ProtocolClient.SendOperationId(), chatId, upload, caption), cancellationToken).ConfigureAwait(false);
        }
        // What the server still holds of a send that did not happen goes now; one whose outcome is unknown is left to the server.
        catch (Exception ex) when (ex is not SendException { OutcomeUnknown: true })
        {
            await MediaTransfer.AbortAsync(client, [upload.UploadId]).ConfigureAwait(false);
            throw;
        }
    }

    public bool CanSave(MessageAttachment attachment) => savedLocally?.Invoke(attachment) == true || state.CanSave(attachment);
    public Task CopyToAsync(MessageAttachment attachment, Stream destination, CancellationToken cancellationToken = default) =>
        draftBytes?.Invoke(attachment) is { } draft ? WriteDraftAsync(draft, destination, cancellationToken) : MediaTransfer.CopyAsync(client, attachment, destination, cancellationToken);

    private static async Task WriteDraftAsync(byte[] draft, Stream destination, CancellationToken cancellationToken) =>
        await destination.WriteAsync(draft, cancellationToken).ConfigureAwait(false);

    // The conversation's own permission decides (a read-only chat refuses). The window asks the async form, which crosses as only what
    // describes the files and keeps the answer; the synchronous one reads that copy, and says no until the server has been asked.
    public bool CanSendBatch(string chatId, IReadOnlyList<AttachmentFile> files) => batches.Get(new BatchKey(chatId, [.. files.Select(Describe)]));
    public async Task<bool> CanSendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken = default)
    {
        var metas = files.Select(Describe).ToArray();
        var answer = await client.CallAsync<CanSendBatchParams, bool>(ProtocolMethods.AttachmentsCanSendBatch, new(chatId, metas), cancellationToken).ConfigureAwait(false);
        batches.Put(new BatchKey(chatId, metas), answer);
        return answer;
    }
    public async Task<SendReceipt> SendBatchAsync(string chatId, IReadOnlyList<AttachmentFile> files, CancellationToken cancellationToken = default)
    {
        var uploads = new List<UploadRef>(files.Count);
        try { foreach (var file in files) uploads.Add(await UploadForSendAsync(file, cancellationToken).ConfigureAwait(false)); }
        catch
        {
            await MediaTransfer.AbortAsync(client, uploads.Select(upload => upload.UploadId)).ConfigureAwait(false);
            throw;
        }
        try
        {
            return await client.MutateAsync<AttachmentBatchParams, SendReceipt>(ProtocolMethods.AttachmentsSendBatch,
                new(ProtocolClient.SendOperationId(), chatId, uploads), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SendException { OutcomeUnknown: true })
        {
            await MediaTransfer.AbortAsync(client, uploads.Select(upload => upload.UploadId)).ConfigureAwait(false);
            throw;
        }
    }
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files) => state.DrawsPhotosAtOnce(chatId, files);

    private static AttachmentMeta Describe(AttachmentFile file) => new(file.Name, file.MimeType, file.Length, file.IsVoiceNote, file.DurationSeconds);

    // The upload phase of a send: nothing has been sent yet, whatever goes wrong here.
    private async Task<UploadRef> UploadForSendAsync(AttachmentFile file, CancellationToken cancellationToken)
    {
        try { return await MediaTransfer.UploadAsync(client, file, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        // The server's own words (it is full, the connection is gone) are safe to show; anything else is the fixed sentence.
        catch (BackendException backend) when (backend.Failure != BackendFailure.InvalidResponse) { throw new SendException(false, backend.Message + " The file wasn't sent."); }
        catch (Exception) { throw new SendException(false, MediaTransfer.UploadFailedSentence); }
    }
}

/// <summary>The search a service answers itself (Google's archive): the search crosses, and so do the words that describe its scope.</summary>
public sealed class SearchProxy(ProtocolClient client, TimeSpan? descriptionLifetime = null) : ISearchBackend, ISearchScopeInfo
{
    // The words MergedSearchBackend uses for a source that does not describe itself.
    private const string Undescribed = "Searches each service's messages.";

    private readonly Remembered<int, string> description = new(_ => client.CallAsync<string>(ProtocolMethods.SearchScopeDescription), Undescribed,
        descriptionLifetime ?? TimeSpan.FromSeconds(3));

    // Text that follows the archive's indexing job (how many messages are saved, how far the crawl has got): it changes as the job runs, and in
    // the state it would be a new state event every few seconds. Given a cache it reads a copy of the server's words (search.scopeDescription),
    // refreshed in the background whenever it is read after its lifetime, so what Settings shows is at most one read behind.
    public string SearchScopeDescription => description.Get(0);

    public Task<Page<SearchHit>> SearchAsync(SearchRequest request, string? cursor = null, CancellationToken cancellationToken = default) =>
        client.CallAsync<SearchQueryParams, Page<SearchHit>>(ProtocolMethods.SearchQuery, new(request, cursor), cancellationToken);
}

/// <summary>The backend's identity: its name and the accounts it serves.</summary>
public sealed class ChatBackendProxy(ProtocolClient client) : IChatBackend
{
    public Task<BackendInfo> DiscoverAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<BackendInfo>(ProtocolMethods.BackendDiscover, cancellationToken);
    public Task<IReadOnlyList<ChatAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<IReadOnlyList<ChatAccount>>(ProtocolMethods.AccountsList, cancellationToken);
}

/// <summary>Snoozes, over the wire.</summary>
public sealed class SnoozeProxy(ProtocolClient client) : IChatSnoozes
{
    public Task<DateTimeOffset?> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default) =>
        client.CallNullableAsync<SnoozeGetParams, DateTimeOffset?>(ProtocolMethods.SnoozesGet, new(profile, chatId), cancellationToken);
    public Task SetAsync(string profile, string chatId, DateTimeOffset? until, CancellationToken cancellationToken = default) =>
        client.MutateAsync(ProtocolMethods.SnoozesSet, new SnoozeSetParams(ProtocolClient.NewOperationId(), profile, chatId, until), cancellationToken);
    public Task<IReadOnlyList<ChatSnooze>> ListAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<IReadOnlyList<ChatSnooze>>(ProtocolMethods.SnoozesList, cancellationToken);
}

/// <summary>
/// A move's import, over the wire (<c>state.import</c>): a journaled mutation under the caller's own operation id, never retried by the client
/// (<see cref="ProtocolClient.MutateAsync{TParams, TResult}"/>). One whose connection ended before its answer is an unknown-outcome <see cref="SendException"/>
/// carrying that id; since the import adds and never replaces, the caller settles it by asking the shared state what it holds, or by moving again under a new id.
/// </summary>
public sealed class StateImportProxy(ProtocolClient client) : IStateImport
{
    public Task<StateImportResult> ImportAsync(StateImportParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<StateImportParams, StateImportResult>(ProtocolMethods.StateImport, request, cancellationToken);
}

/// <summary>
/// The server's scheduled sends, over the wire (<c>scheduled.*</c>). Each is a journaled mutation under the caller's own operation id, never
/// retried (<see cref="ProtocolClient.MutateAsync{TParams, TResult}"/>): one whose connection ended before its answer is an unknown-outcome
/// <see cref="SendException"/> carrying that id, which the caller settles with <see cref="ProtocolClient.OperationStatusAsync"/> and then its replica, and
/// never by asking again. With no connection a command fails at once as a send does (unavailable), so none queues.
/// </summary>
public sealed class ScheduledProxy(ProtocolClient client) : IScheduledCommands
{
    public Task<ScheduledAnswer> CreateAsync(ScheduledCreateParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<ScheduledCreateParams, ScheduledAnswer>(ProtocolMethods.ScheduledCreate, request, cancellationToken);
    public Task<ScheduledAnswer> EditAsync(ScheduledCommandParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<ScheduledCommandParams, ScheduledAnswer>(ProtocolMethods.ScheduledEdit, request, cancellationToken);
    public Task<ScheduledAnswer> CancelAsync(ScheduledCommandParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<ScheduledCommandParams, ScheduledAnswer>(ProtocolMethods.ScheduledCancel, request, cancellationToken);
    public Task<ScheduledAnswer> SendNowAsync(ScheduledCommandParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<ScheduledCommandParams, ScheduledAnswer>(ProtocolMethods.ScheduledSendNow, request, cancellationToken);
    public Task<Done> DismissAsync(RefusedDismissParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<RefusedDismissParams, Done>(ProtocolMethods.RefusedDismiss, request, cancellationToken);
}
