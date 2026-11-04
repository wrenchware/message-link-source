using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

// The read-only members of the Core ports that can cross a wire as they are: async, plain data in and out, no mutation.
// Results are the Core records themselves. Names are noun.verb, lowercase noun, camelCase verb.
//
// Left for 2.2, because they cannot cross as they are:
//   bytes that are not media: IPrivateStateStore, ISavedPhotoStore. Since stage 6 the rows that must look the same everywhere cross as the shared state
//     (ProtocolMethods.SharedState.cs), into a replica the client's IPrivateStateStore reads; the rest of IPrivateStateStore stays on each device.
//   synchronous live state: has no request. Since 2.6b the host pushes it as a `state` event (HostState) and the proxies answer from the
//     client's cache; ProxyMembers lists every synchronous member and how it is answered. IAppPresence, the keeper's UserBusy, private reading and link previews are the client's own, stated to the host by client.state (ProtocolMethods.ClientState.cs).
//   IRealtimeFeed.RunAsync is the events.subscribe stream (EventLog, RealtimeFeedProxy). ITypingService.WatchAsync is the same stream's
//     `typing` frames (TypingHub, TypingProxy), which are not part of its sequence: typing is ephemeral and never replayed.
//   ownership undecided (per profile, may stay on the client): ILocalChatPreferences.GetAsync, ILocalContactNames.GetAsync
// 2.6d (ProtocolMethods.Reading.cs): mark read and typing out, which carry the private-reading admission explicitly; link previews; and a
// history window with the call that waits for its refresh (HistoryWindow.Refreshed is a Task and cannot cross).
// The mutations that are here carry a clientOpId and are served through the OperationJournal (at most once per id).
// Media (2.4c): a file to send goes up first, in chunks, under a handle (media.upload*, UploadStore), and the send names the handle
// (attachments.send, attachments.sendBatch); a file or picture to read comes down in chunks by reference (media.read). The
// synchronous members CanSave and DrawsPhotosAtOnce are answered on the client from the pushed state, and CanSendBatch has an async form
// (attachments.canSendBatch).
// Accounts (2.6c, ProtocolMethods.Accounts.cs): what the four account ports do. The actions that change an account or a session are
// journaled mutations (accounts.<service>.<verb>); a question per call is a read, and the synchronous ones are answered on the client from a
// copy of the host answer (Remembered) or a rule in Core.
public static partial class ProtocolMethods
{
    public const string BackendDiscover = "backend.discover";
    public const string AccountsList = "accounts.list";
    public const string ChatsList = "chats.list";
    public const string ChatsSync = "chats.sync";
    public const string ChatsGet = "chats.get";
    public const string ChatsSettings = "chats.settings";
    public const string ChatsSendPermission = "chats.sendPermission";
    public const string ChatsAttachmentPermission = "chats.attachmentPermission";
    public const string ChatsCanMarkRead = "chats.canMarkRead";
    public const string MessagesPage = "messages.page";
    public const string MessagesResolve = "messages.resolve";
    public const string MessagesPeek = "messages.peek";
    public const string MessagesPeekWhole = "messages.peekWhole";
    public const string MessagesActionOptions = "messages.actionOptions";
    public const string HistoryHeld = "history.held";
    public const string ContactsOptions = "contacts.options";
    public const string ContactsFind = "contacts.find";
    public const string ContactsFrequent = "contacts.frequent";
    public const string GroupsOptions = "groups.options";
    public const string SearchQuery = "search.query";
    public const string SnoozesGet = "snoozes.get";
    public const string SnoozesList = "snoozes.list";

    // Mutations: each takes a MutationParams record.
    public const string MessagesSend = "messages.send";
    public const string MessagesReply = "messages.reply";
    public const string MessagesReact = "messages.react";
    public const string MessagesEdit = "messages.edit";
    public const string MessagesDelete = "messages.delete";
    public const string ChatsSet = "chats.set";
    public const string ChatsDelete = "chats.delete";
    public const string GroupsCreate = "groups.create";
    public const string GroupsRename = "groups.rename";
    public const string GroupsAddMembers = "groups.addMembers";
    public const string GroupsRemoveMember = "groups.removeMember";
    public const string ContactsStartDirect = "contacts.startDirect";
    public const string ContactsStartWithMessage = "contacts.startWithMessage";
    public const string SnoozesSet = "snoozes.set";
    /// <summary>One file, or a photo set, to a conversation. Each names uploads made earlier (<see cref="MediaUploadBegin"/>); the host reads them from its store.</summary>
    public const string AttachmentsSend = "attachments.send";
    public const string AttachmentsSendBatch = "attachments.sendBatch";

    // Reads about a file that is not sent yet: only what describes it (name, type, length) crosses, never its bytes.
    public const string AttachmentsFilePermission = "attachments.filePermission";
    public const string AttachmentsCaptionSupport = "attachments.captionSupport";

    // Media. Not mutations: an upload by itself sends nothing, and a repeated chunk is refused by its offset, so none carries a clientOpId.
    public const string MediaUploadBegin = "media.uploadBegin";
    public const string MediaUploadChunk = "media.uploadChunk";
    public const string MediaUploadEnd = "media.uploadEnd";
    /// <summary>Drops an upload no send has claimed. Best effort: the host expires what is left.</summary>
    public const string MediaUploadAbort = "media.uploadAbort";
    /// <summary>A picture, clip or file a message carries, one chunk of it at a time.</summary>
    public const string MediaRead = "media.read";

    /// <summary>What became of an earlier mutation, from the journal. A lookup: it never runs or repeats anything.</summary>
    public const string OpsStatus = "ops.status";

    /// <summary>Starts this connection's event feed (see <see cref="EventLog"/>): the answer says where it begins, then <c>event</c> frames follow. One feed per connection; asking again replaces it.</summary>
    public const string EventsSubscribe = "events.subscribe";
    /// <summary>Ends this connection's event feed. Safe when there is none.</summary>
    public const string EventsUnsubscribe = "events.unsubscribe";

    /// <summary>
    /// One method: its wire name, the Core member behind it, its params record (null for none) and the type of its result (a
    /// null result is allowed where the port can return null). A <paramref name="Mutation"/> changes something: its params derive
    /// from <see cref="MutationParams"/> and the host runs it at most once per client operation id.
    /// </summary>
    public sealed record Entry(string Name, string Member, Type? Params, Type Result, bool Mutation = false);

    public static IReadOnlyList<Entry> Catalog { get; } =
    [
        new(BackendDiscover, "IBeeperBackend.DiscoverAsync", null, typeof(BackendInfo)),
        new(AccountsList, "IBeeperBackend.GetAccountsAsync", null, typeof(IReadOnlyList<ChatAccount>)),
        new(ChatsList, "IChatReader.GetChatsAsync", typeof(ChatsListParams), typeof(Page<ChatSummary>)),
        new(ChatsSync, "ConversationSync.ReadAsync", typeof(ChatsSyncParams), typeof(ChatsSyncResult)),
        new(ChatsGet, "IChatReader.GetChatAsync", typeof(ChatIdParams), typeof(ChatSummary)),
        new(ChatsSettings, "IChatSettingsBackend.GetSettingsAsync", typeof(ChatIdParams), typeof(ChatSettings)),
        new(ChatsSendPermission, "IMessageSender.GetSendPermissionAsync", typeof(ChatIdParams), typeof(SendPermission)),
        new(ChatsAttachmentPermission, "IAttachmentBackend.GetPermissionAsync", typeof(ChatIdParams), typeof(SendPermission)),
        new(ChatsCanMarkRead, "IReadStateWriter.CanMarkReadAsync", typeof(ChatIdParams), typeof(bool)),
        new(MessagesPage, "IChatReader.GetMessagesAsync", typeof(MessagesPageParams), typeof(Page<ChatMessage>)),
        new(MessagesResolve, "IMessageSender.ResolveMessageAsync", typeof(MessageIdParams), typeof(ChatMessage)),
        new(MessagesPeek, "IMessagePeek.PeekAsync", typeof(MessageIdParams), typeof(ChatMessage)),
        new(MessagesPeekWhole, "IMessagePeek.PeekWholeAsync", typeof(MessageIdParams), typeof(ChatMessage)),
        new(MessagesActionOptions, "IMessageActions.GetOptionsAsync", typeof(MessageIdParams), typeof(MessageActionOptions)),
        new(HistoryHeld, "IHistoryWindowReader.ReadHeldAsync", typeof(HistoryHeldParams), typeof(HeldHistory)),
        new(ContactsOptions, "IContactBackend.GetContactOptionsAsync", typeof(AccountIdParams), typeof(ContactOptions)),
        new(ContactsFind, "IContactBackend.FindContactsAsync", typeof(ContactsFindParams), typeof(IReadOnlyList<ContactPerson>)),
        new(ContactsFrequent, "IFrequentContacts.GetFrequentContactsAsync", typeof(AccountIdParams), typeof(IReadOnlyList<ContactPerson>)),
        new(GroupsOptions, "IGroupBackend.GetGroupOptionsAsync", typeof(AccountIdParams), typeof(GroupOptions)),
        new(SearchQuery, "ISearchBackend.SearchAsync and IKeptHistorySearch.SearchAsync", typeof(SearchQueryParams), typeof(Page<SearchHit>)),
        new(SnoozesGet, "IChatSnoozes.GetAsync", typeof(SnoozeGetParams), typeof(DateTimeOffset?)),
        new(SnoozesList, "IChatSnoozes.ListAsync", null, typeof(IReadOnlyList<ChatSnooze>)),

        new(MessagesSend, "IMessageSender.SendTextAsync", typeof(SendTextParams), typeof(SendReceipt), true),
        new(MessagesReply, "IMessageActions.ReplyAsync", typeof(ReplyParams), typeof(SendReceipt), true),
        new(MessagesReact, "IMessageActions.ReactAsync", typeof(ReactParams), typeof(Done), true),
        new(MessagesEdit, "IMessageMutations.EditAsync", typeof(EditParams), typeof(Done), true),
        new(MessagesDelete, "IMessageMutations.DeleteAsync", typeof(DeleteParams), typeof(Done), true),
        new(ChatsSet, "IChatSettingsBackend.SetAsync", typeof(ChatSetParams), typeof(ChatSummary), true),
        new(ChatsDelete, "IChatDeletion.DeleteChatAsync", typeof(ChatMutationParams), typeof(Done), true),
        new(GroupsCreate, "IGroupBackend.CreateGroupAsync", typeof(GroupCreateParams), typeof(ChatSummary), true),
        new(GroupsRename, "IGroupManagement.RenameGroupAsync", typeof(GroupRenameParams), typeof(ChatSummary), true),
        new(GroupsAddMembers, "IGroupManagement.AddGroupMembersAsync", typeof(GroupMembersParams), typeof(ChatSummary), true),
        new(GroupsRemoveMember, "IGroupManagement.RemoveGroupMemberAsync", typeof(GroupMemberParams), typeof(ChatSummary), true),
        new(ContactsStartDirect, "IContactBackend.StartDirectAsync", typeof(StartDirectParams), typeof(ChatSummary), true),
        new(ContactsStartWithMessage, "IFirstMessageStarter.StartDirectWithMessageAsync", typeof(StartWithMessageParams), typeof(ChatSummary), true),
        new(SnoozesSet, "IChatSnoozes.SetAsync", typeof(SnoozeSetParams), typeof(Done), true),
        new(AttachmentsSend, "IAttachmentBackend.SendAsync", typeof(AttachmentSendParams), typeof(SendReceipt), true),
        new(AttachmentsSendBatch, "IAttachmentBatchBackend.SendBatchAsync", typeof(AttachmentBatchParams), typeof(SendReceipt), true),

        new(AttachmentsFilePermission, "IAttachmentBackend.GetFilePermissionAsync", typeof(AttachmentFileParams), typeof(SendPermission)),
        new(AttachmentsCaptionSupport, "IAttachmentBackend.GetCaptionSupportAsync", typeof(AttachmentFileParams), typeof(AttachmentCaptionSupport)),
        new(MediaUploadBegin, "UploadStore.Begin", typeof(UploadBeginParams), typeof(UploadBegun)),
        new(MediaUploadChunk, "UploadStore.Append", typeof(UploadChunkParams), typeof(UploadReceived)),
        new(MediaUploadEnd, "UploadStore.End", typeof(UploadIdParams), typeof(UploadEnded)),
        new(MediaUploadAbort, "UploadStore.Abort", typeof(UploadIdParams), typeof(Done)),
        new(MediaRead, "IMediaReader.ReadImageAsync and IAttachmentBackend.CopyToAsync", typeof(MediaReadParams), typeof(MediaChunk)),

        new(OpsStatus, "OperationJournal.Lookup", typeof(OpStatusParams), typeof(OpStatus)),
        new(EventsSubscribe, "EventLog.Subscribe", typeof(EventsSubscribeParams), typeof(EventsSubscribed)),
        new(EventsUnsubscribe, "EventLog.Unsubscribe", null, typeof(Done)),
        .. AccountEntries(),
        .. ReadingEntries(),
        .. DiagnosticsEntries(),
        .. KeptHistoryEntries(),
        .. IncomingEntries(),
        .. ClientStateEntries(),
        .. ShutdownEntries(),
        .. DeviceEntries(),
        .. SharedStateEntries(),
        .. ScheduledEntries(),
        .. StateImportEntries(),
        .. AccountMoveEntries(),
        .. HostLogsEntries(),
        .. AdminEntries(),
        .. PushEntries(),
        .. BackupEntries(),
        .. WholeHistoryEntries(),
    ];

    public static IReadOnlyList<string> All { get; } = Catalog.Select(entry => entry.Name).ToArray();
}

// Params records, one JSON object each. A method with no params record takes none and the request may omit "params".
public sealed record ChatsListParams(string? OlderThan = null);
public sealed record ChatIdParams(string ChatId);
public sealed record MessagesPageParams(string ChatId, string? OlderThan = null);
public sealed record MessageIdParams(string ChatId, string MessageId);
public sealed record AccountIdParams(string AccountId);
public sealed record ContactsFindParams(string AccountId, string Query);
/// <summary>A null <paramref name="Source"/> (absent on the wire) searches the provider's archive; <see cref="SearchQueryParams.KeptSource"/> the host's kept history, whole and without a cursor.</summary>
public sealed record SearchQueryParams(SearchRequest Request, string? Cursor = null, string? Source = null)
{
    /// <summary>The <see cref="Source"/> of the host's kept history (Messenger's own store).</summary>
    public const string KeptSource = "kept";
}
public sealed record HistoryHeldParams(string ChatId, DateTimeOffset After, int Limit);
public sealed record SnoozeGetParams(string Profile, string ChatId);

// Mutation params. ClientOpId names one user action (a new one per action, never reused for a different one): it is how a host
// answers a repeat with the first answer instead of doing the thing twice. A required member that is a value type (a flag, an
// enum) must be present on the wire, because a missing one would otherwise read as its default and change what is asked.
public abstract record MutationParams(string ClientOpId);
public sealed record SendTextParams(string ClientOpId, string ChatId, string Text) : MutationParams(ClientOpId);
public sealed record ReplyParams(string ClientOpId, string ChatId, string MessageId, string Text) : MutationParams(ClientOpId);
public sealed record ReactParams(string ClientOpId, string ChatId, string MessageId, string Reaction, bool Remove) : MutationParams(ClientOpId);
public sealed record EditParams(string ClientOpId, string ChatId, string MessageId, string? OriginalText, string Text) : MutationParams(ClientOpId);
public sealed record DeleteParams(string ClientOpId, string ChatId, string MessageId, bool ForEveryone) : MutationParams(ClientOpId);
public sealed record ChatSetParams(string ClientOpId, string ChatId, ChatSetting Setting, bool Value) : MutationParams(ClientOpId);
public sealed record ChatMutationParams(string ClientOpId, string ChatId) : MutationParams(ClientOpId);
public sealed record GroupCreateParams(string ClientOpId, string AccountId, IReadOnlyList<ContactPerson> People, string Title) : MutationParams(ClientOpId);
public sealed record GroupRenameParams(string ClientOpId, string ChatId, string Title) : MutationParams(ClientOpId);
public sealed record GroupMembersParams(string ClientOpId, string ChatId, IReadOnlyList<ContactPerson> People) : MutationParams(ClientOpId);
public sealed record GroupMemberParams(string ClientOpId, string ChatId, string PersonId) : MutationParams(ClientOpId);
public sealed record StartDirectParams(string ClientOpId, ContactPerson Contact, bool AllowInvite) : MutationParams(ClientOpId);
public sealed record StartWithMessageParams(string ClientOpId, ContactPerson Contact, string Text) : MutationParams(ClientOpId);
/// <summary>A null <paramref name="Until"/> (absent on the wire) ends the snooze.</summary>
public sealed record SnoozeSetParams(string ClientOpId, string Profile, string ChatId, DateTimeOffset? Until = null) : MutationParams(ClientOpId);

// Media. An upload is begun with the file's declared length, filled by chunks whose offsets follow one another, and ended; the
// host checks that what arrived is what was declared. A chunk is at most UploadStore.MaxChunkBytes before base64.
public sealed record UploadBeginParams(string MimeType, long Length, string FileName);
public sealed record UploadBegun(string UploadId);
public sealed record UploadChunkParams(string UploadId, long Offset, string Data);
/// <summary>How many bytes of the upload the host holds now.</summary>
public sealed record UploadReceived(long Received);
public sealed record UploadIdParams(string UploadId);
public sealed record UploadEnded(string UploadId, long Length);

/// <summary>What an <c>AttachmentFile</c> says about itself, without its bytes. <paramref name="FileName"/> is private: it is never logged.</summary>
public sealed record AttachmentMeta(string FileName, string MimeType, long Length, bool IsVoiceNote = false, double? DurationSeconds = null);
/// <summary>A finished upload that a send names: the handle and the file's description (the host checks length and type against what it holds).</summary>
public sealed record UploadRef(string UploadId, string FileName, string MimeType, long Length, bool IsVoiceNote = false, double? DurationSeconds = null);
public sealed record AttachmentFileParams(string ChatId, AttachmentMeta File);
public sealed record AttachmentSendParams(string ClientOpId, string ChatId, UploadRef Upload, string? Caption = null) : MutationParams(ClientOpId);
public sealed record AttachmentBatchParams(string ClientOpId, string ChatId, IReadOnlyList<UploadRef> Uploads) : MutationParams(ClientOpId);

/// <summary>
/// One chunk of what a message carries, from <paramref name="Offset"/>. <paramref name="Save"/> asks for the file as
/// <c>IAttachmentBackend.CopyToAsync</c> would write it; without it the bytes are <c>IMediaReader.ReadImageAsync</c>'s
/// (<paramref name="Inline"/>: the smaller copy a conversation draws). A video's read is its still and its copy is the clip,
/// so the two are not interchangeable.
/// </summary>
public sealed record MediaReadParams(MessageAttachment Attachment, long Offset, int Count, bool Inline = false, bool Save = false);
/// <summary>Up to the requested count of bytes (base64), and the size of the whole item in bytes.</summary>
public sealed record MediaChunk(string Data, long Total);

/// <summary>The result of a mutation whose port returns nothing: an empty object, so an answer always has a body to read.</summary>
public sealed record Done;

public sealed record OpStatusParams(string ClientOpId);

/// <summary>Where an operation stands in the journal. <c>unknown</c> means the host has no record of it: never seen, or pruned.</summary>
public enum OpState { Running, Done, Failed, Unknown }

/// <summary>
/// What <see cref="ProtocolMethods.OpsStatus"/> answers. <paramref name="Result"/> is the stored result of a finished operation
/// and <paramref name="Error"/> the stored error of a failed one. A send that was interrupted after it started is
/// <c>failed</c> with code <c>outcome_unknown</c>: the caller reports it as unknown and never sends it again.
/// </summary>
public sealed record OpStatus(OpState State, JsonElement? Result = null, ProtocolError? Error = null);

/// <summary>
/// Where a connection's feed resumes from: the <paramref name="Epoch"/> and the last <paramref name="Since"/> a client received. Both
/// absent is a first subscription. A feed that cannot be resumed (another epoch, or a position the host no longer holds) starts with
/// a resync instead. <paramref name="Typing"/> asks for the typing of the people in the user's conversations as <c>typing</c> frames
/// beside the events (a client that does not ask gets none; an older host ignores it).
/// </summary>
public sealed record EventsSubscribeParams(long? Since = null, string? Epoch = null, bool Typing = false);

/// <summary>
/// The answer to a subscription, sent before any event frame. <paramref name="Head"/> is the host's last sequence number now;
/// <paramref name="Resync"/> says the first event frame is a resync rather than the replay of what the client missed.
/// </summary>
public sealed record EventsSubscribed(string Epoch, long Head, bool Resync);
