using MessageLink.App.Services.GoogleMessages;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.Messenger;

// One chat list from every network, and every call about one conversation passed to the service that owns its id
// (ConversationOwners), which answers it or refuses it in its own words. An id no service owns is refused there.
internal sealed partial class MessagingRouter(ConversationOwners owners, MessageLink.Presentation.Services.PrivateReading? reading = null)
    : IChatReader, ILinkPreviewLoader, IMessageSender, IReadStateWriter, IMessageActions, IMessageMutations, IChatSettingsBackend, IChatDeletion, IProviderConnectionState, IMessagePeek,
      IHistoryWindowReader, IHistoryFacts
{
    internal ProviderAccounts? Accounts { get => owners.Accounts; set => owners.Accounts = value; }
    private IConversationService Owner(string id) => owners.Of(id);

    // A window around a moment is read where history is kept on this PC: Messenger's helper, and Google Messages' archive
    // for a conversation it has crawled to the beginning. Instagram and Voice search only what this session loaded, which
    // paging back reaches.
    public bool CanReadWindow(string chatId) => Owner(chatId).CanReadWindow(chatId);

    public Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default)
        => Owner(chatId).ReadWindowAsync(chatId, at, older, newer, cancellationToken);

    // Google's archive and Instagram's newest pages answer this. Messenger's and Voice's openings wait for their read.
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default)
        => Owner(chatId).ReadHeldAsync(chatId, after, limit, cancellationToken);

    // Google Messages that was never paired on this PC is not set up: it has no conversations to add, nothing to connect
    // and nothing to report, whether or not any other network is signed in. It was expected whenever nothing else was,
    // and a first run then tried to connect every three seconds, said "A service needs authorization" and "Google Messages
    // could not refresh", and offered Load more over an empty list (2026-10-02 clean-PC run). Pairing restarts
    // synchronization, which asks this again. A paired connection that is failing retains its cached rows and retry cursor.
    internal static bool GoogleMessagesExpected => GoogleSessionProfile.HasSavedSession;

    public bool AnyConnected => owners.Services.Any(service => service.AnyConnected);
    public bool IsConnected(string chatId) => Owner(chatId).IsConnected(chatId);
    public bool IsScopeConnected(string scope) => AccountNamespace.KeyOf(scope) is not null ? IsConnected(scope) : Owner(scope).IsScopeConnected(scope);
    public bool IsConfigured(string scope) => Owner(scope).IsConfigured(scope);
    public bool NeedsSignInOnly(string scope) => Owner(scope).NeedsSignInOnly(scope);
    private const string RetryGoogleFirst = "retry:first";

    private async Task<Page<ChatSummary>> LegacyChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        var (google, voice, instagram, messenger, hermes) = (owners.Google, owners.Voice, owners.Instagram, owners.Messenger, owners.Hermes);
        var expected = GoogleMessagesExpected;
        var empty = new Page<ChatSummary>([], false, null, null);
        var first = olderThan is null;
        var combined = first ? null : CombinedChatCursor.IsCombined(olderThan)
            ? CombinedChatCursor.Decode(olderThan) ?? throw new MessageLink.Core.Backend.BackendException(MessageLink.Core.Backend.BackendFailure.InvalidResponse, "That list position is no longer valid. Refresh chats.")
            : new CombinedChatCursor(olderThan, new Dictionary<string, string>());
        var googleCursor = combined?.Google;
        var readGoogle = expected && (first || googleCursor is not null);
        // Each provider owns a separate helper. Begin independent reads before awaiting any of them. A sign-in being renewed
        // is quietly unavailable: "could not refresh" appeared once launch's notices were released and went again when the
        // renewal's reconnect resynced the list a few seconds later. If the renewal fails, the connection notice says so.
        var googleTask = readGoogle ? MessageLink.Presentation.Services.PartialChatPages.ReadAsync(
            () => google.CanReceive ? google.GetChatsAsync(googleCursor == RetryGoogleFirst ? null : googleCursor, cancellationToken)
                : Task.FromException<Page<ChatSummary>>(google.SignInSettling ? GoogleReadFailure.Renewing() : new InvalidOperationException("not_connected")),
            ServiceTags.GoogleMessages.Prefix, ServiceTags.GoogleMessages.Name, googleCursor ?? RetryGoogleFirst, cancellationToken) : Task.FromResult(empty);
        var voiceTask = voice.GetChatPageAsync(first ? null : combined!.Voice, cancellationToken);
        var instagramTask = first ? MessageLink.Presentation.Services.PartialChatPages.ReadAsync(
            () => instagram.GetChatsAsync(null, cancellationToken), ServiceTags.Instagram.Prefix, ServiceTags.Instagram.Name, null, cancellationToken) : Task.FromResult(empty);
        var messengerTask = first ? MessageLink.Presentation.Services.PartialChatPages.ReadAsync(
            () => messenger.GetChatsAsync(null, cancellationToken), ServiceTags.Messenger.Prefix, ServiceTags.Messenger.Name, null, cancellationToken) : Task.FromResult(empty);
        var hermesTask = first && hermes is not null ? MessageLink.Presentation.Services.PartialChatPages.ReadAsync(
            () => hermes.GetChatsAsync(null, cancellationToken), ServiceTags.Hermes.Prefix, ServiceTags.Hermes.Name, null, cancellationToken) : Task.FromResult(empty);
        await Task.WhenAll(googleTask, voiceTask, instagramTask, messengerTask, hermesTask);
        var chats = await googleTask;
        var (voiceChats, voiceNext, voiceFailed, voiceUnavailable) = await voiceTask;
        var insta = await instagramTask; var meta = await messengerTask; var agent = await hermesTask;
        var merged = chats.Items.Concat(voiceChats).Concat(insta.Items).Concat(meta.Items).Concat(agent.Items)
            .OrderByDescending(chat => chat.LastActivity ?? DateTimeOffset.MinValue).ThenBy(chat => chat.Id, StringComparer.Ordinal).ToArray();
        foreach (var chat in merged) Drift.Observe(chat);
        var googleMore = chats.HasMore && chats.OldestCursor is not null;
        var retry = voiceFailed > 0 || chats.UnavailableScopes?.Count > 0 ? Math.Min(1_000_000, (combined?.Retry ?? 0) + 1) : 0;
        var warnings = new[] { chats.Warning, insta.Warning, meta.Warning, agent.Warning,
            voiceFailed > 0 ? "Some Google Voice conversations could not refresh. Their saved conversations remain; choose Load more to retry." : null };
        var unavailable = (chats.UnavailableScopes ?? []).Concat(voiceUnavailable)
            .Concat(insta.UnavailableScopes ?? []).Concat(meta.UnavailableScopes ?? []).Concat(agent.UnavailableScopes ?? []).Distinct().ToArray();
        return new(merged, voiceNext.Count > 0 || chats.HasMore,
            CombinedChatCursor.Next(chats.OldestCursor, googleMore, voiceNext, retry), chats.NewestCursor,
            Warning: string.Join(" ", warnings.Where(value => !string.IsNullOrEmpty(value))), UnavailableScopes: unavailable);
    }

    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
        => Observed(await Owner(chatId).GetChatAsync(chatId, cancellationToken).ConfigureAwait(false));

    public Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
        => Owner(chatId).GetMessagesAsync(chatId, olderThan, cancellationToken);

    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
        => Owner(chatId).LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken);

    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default)
        => Owner(chatId).EditAsync(chatId, messageId, originalText, text, cancellationToken);

    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default)
        => Owner(chatId).DeleteAsync(chatId, messageId, forEveryone, cancellationToken);

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default)
        => Owner(chatId).GetSendPermissionAsync(chatId, cancellationToken);

    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default)
        => Owner(chatId).SendTextAsync(chatId, text, cancellationToken);

    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
        => Owner(chatId).ResolveMessageAsync(chatId, messageId, cancellationToken);

    // Routed like ResolveMessageAsync, but only ever into memory each backend already holds: no provider request, no
    // page, no conversation read. A merged id belongs to no service and has no messages of its own. A copy filed under
    // another conversation is not this one's.
    public ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
        => PeekAsync(chatId, messageId, whole: false, cancellationToken);

    // The same memory, whole copies only (IMessagePeek.PeekWholeAsync). Instagram leaves out the list's words for a photo
    // or a share; Google's, Voice's and Messenger's memory holds each message as their own resolve gives it, so theirs
    // answer as above.
    public bool HoldsPartialCopies(string chatId) => Owner(chatId).HoldsPartialCopies;

    public HistoryFacts FactsOf(string chatId) => Owner(chatId).HistoryFacts;
    public bool TryTakeUnaskedRead(string chatId) => Owner(chatId).TryTakeUnaskedRead(chatId);
    public ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
        => PeekAsync(chatId, messageId, whole: true, cancellationToken);

    private async ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, bool whole, CancellationToken cancellationToken)
    {
        if (MessageLink.Presentation.Services.MergedConversations.IsMerged(chatId)) return null;
        ChatMessage? found;
        try
        {
            var owner = Owner(chatId);
            found = whole ? await owner.PeekWholeAsync(chatId, messageId, cancellationToken) : await owner.PeekAsync(chatId, messageId, cancellationToken);
        }
        catch (Exception) { return null; }
        return found?.ChatId == chatId && found.Id == messageId ? found : null;
    }

    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default)
        => Owner(chatId).CanMarkReadAsync(chatId, cancellationToken);

    // Every read marker reaches a service through here, and every one the app means to send comes through
    // PrivateReadingGate first. One for a private-reading service that did not is refused and logged, not sent.
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default) => TellReadAsync(chatId, messageId, cancellationToken);

    // Whether the service was told (false: private reading kept it). A read it took is remembered for the drift check.
    private async Task<bool> TellReadAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        var owner = Owner(chatId);
        // Hermes keeps its read state here alone and tells no service, so private reading has nothing to hold back.
        if (!owner.TellsReads) { await owner.MarkReadAsync(chatId, messageId, cancellationToken).ConfigureAwait(false); return true; }
        if (MessageLink.Presentation.Services.PrivateReadingGate.Refuses(reading, chatId, "read")) return false;
        await owner.MarkReadAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
        Drift.Accepted(chatId, messageId);
        return true;
    }

    // Reads a service took and then did not keep (ReadDrift): seen in the conversations passing back through here, told once more.
    private ReadDrift? drift;
    internal ReadDrift Drift => LazyInitializer.EnsureInitialized(ref drift, () => new ReadDrift(TellReadAsync, GetChatAsync, AppLog.Write));

    private ChatSummary Observed(ChatSummary chat) { Drift.Observe(chat); return chat; }

    public bool HasLocalActionOptions => ((IMessageActions)owners.Google).HasLocalActionOptions;

    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
        => Owner(chatId).GetOptionsAsync(chatId, messageId, cancellationToken);

    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default)
        => Owner(chatId).ReplyAsync(chatId, messageId, text, cancellationToken);

    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default)
        => Owner(chatId).ReactAsync(chatId, messageId, reaction, remove, cancellationToken);

    public Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken = default)
        => Owner(chatId).GetSettingsAsync(chatId, cancellationToken);

    public Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default)
        => Owner(chatId).SetAsync(chatId, setting, value, cancellationToken);

    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken = default)
        => Owner(chatId).DeleteChatAsync(chatId, cancellationToken);
}
