using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Messenger;

// One chat list from every network. Anything with a Messenger id goes to the Messenger backend, anything with a Google
// Voice id to the Voice accounts, and everything else stays with Google Messages. Messages, actions, deletion and provider
// archive state all follow the owning conversation.
internal sealed partial class MessagingRouter(GoogleMessagesBackend google, MessengerBackend messenger, GoogleVoiceBackend voice,
    NativeBeeper.App.Services.Instagram.InstagramBackend instagram, NativeBeeper.Presentation.Services.PrivateReading? reading = null)
    : IChatReader, ILinkPreviewLoader, IMessageSender, IReadStateWriter, IMessageActions, IMessageMutations, IChatSettingsBackend, IChatDeletion, IProviderConnectionState, IMessagePeek,
      IHistoryWindowReader
{
    internal ProviderAccounts? Accounts { get; set; }
    private GoogleMessagesBackend Google(string id) => Accounts?.Google(id) ?? google;
    private MessengerBackend Messenger(string id) => Accounts?.Messenger(id) ?? messenger;
    private NativeBeeper.App.Services.Instagram.InstagramBackend InstagramAccount(string id) => Accounts?.Instagram(id) ?? instagram;
    // A window around a moment is read where history is kept on this PC: Messenger's helper, and Google Messages' archive
    // for a conversation it has crawled to the beginning. Instagram and Voice search only what this session loaded, which
    // paging back reaches.
    public bool CanReadWindow(string chatId) => Mine(chatId) || !Insta(chatId) && !Voice(chatId) && Google(chatId).CanReadWindow(chatId);

    public Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default)
        => Mine(chatId) ? Messenger(chatId).ReadWindowAsync(chatId, at, older, newer, cancellationToken)
            : !Insta(chatId) && !Voice(chatId) && Google(chatId).CanReadWindow(chatId) ? Google(chatId).ReadWindowAsync(chatId, at, older, newer, cancellationToken)
            : Task.FromException<HistoryWindow>(new NotSupportedException("This conversation can't open at that message."));

    // Google's archive and Instagram's newest pages answer this. Messenger's and Voice's openings wait for their read as
    // before: Messenger's comes from its helper's memory within milliseconds, and the messages it delivers as it connects
    // reach a merge's saved copy through their events.
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).ReadHeldAsync(chatId, after, limit, cancellationToken)
            : !Mine(chatId) && !Voice(chatId) ? Google(chatId).ReadHeldAsync(chatId, after, limit, cancellationToken)
            : Task.FromResult<HeldHistory?>(null);

    private static bool Mine(string id) => MessengerModels.Owns(id);
    private static bool Voice(string id) => GoogleVoiceModels.Owns(id);
    private static bool Insta(string id) => NativeBeeper.App.Services.Instagram.InstagramIdentity.OwnsAny(id);

    // Instagram sends, replies, reacts, edits, takes back and marks read; changing a conversation's own settings and
    // deleting one are not implemented. Each is named separately rather than left to fall through to Google Messages,
    // which is what an unrecognized id does here.
    private static Task Read(string what) =>
        Task.FromException(NativeBeeper.App.Services.Instagram.InstagramBackend.Refused($"Instagram in Message Link cannot {what}."));
    private static Task<T> Read<T>(string what) =>
        Task.FromException<T>(NativeBeeper.App.Services.Instagram.InstagramBackend.Refused($"Instagram in Message Link cannot {what}."));

    // Google Messages that was never paired on this PC is not set up: it has no conversations to add, nothing to connect
    // and nothing to report, whether or not any other network is signed in. It was expected whenever nothing else was,
    // and a first run then tried to connect every three seconds, said "A service needs authorization" and "Google Messages
    // could not refresh", and offered Load more over an empty list (2026-10-02 clean-PC run). Pairing restarts
    // synchronization, which asks this again. A paired connection that is failing retains its cached rows and retry cursor.
    internal static bool GoogleMessagesExpected => GoogleSessionProfile.HasSavedSession;

    public bool AnyConnected => (Accounts?.Googles.Any(g => g.CanReceive) ?? google.CanReceive) || (Accounts?.Messengers.Any(m => m.IsConnected) ?? messenger.IsConnected) || (Accounts?.Instagrams.Any(i => i.IsConnected) ?? instagram.IsConnected) || voice.AnyConnected;
    public bool IsConnected(string chatId) => Insta(chatId) ? InstagramAccount(chatId).IsConnected
        : Voice(chatId) ? voice.IsConnected(chatId) : Mine(chatId) ? Messenger(chatId).IsConnected : Google(chatId).CanReceive;
    public bool IsScopeConnected(string scope) => AccountNamespace.KeyOf(scope) is not null ? IsConnected(scope) : scope switch
    {
        "google-direct:" => google.CanReceive,
        "messenger:" => messenger.IsConnected,
        "instagram:" => instagram.IsConnected,
        _ => voice.IsScopeConnected(scope),
    };
    public bool IsConfigured(string scope) => scope.StartsWith("gvoice:", StringComparison.Ordinal) ? voice.IsConfigured(scope)
        : scope.StartsWith("messenger:", StringComparison.Ordinal) ? Messenger(scope).SessionSaved
        : scope.StartsWith("instagram:", StringComparison.Ordinal) ? InstagramAccount(scope).HasSavedSession : Google(scope).Files.SessionSaved;
    public bool NeedsSignInOnly(string scope) => scope.StartsWith("google-direct:", StringComparison.Ordinal) && Google(scope).SignInExpired;
    private const string RetryGoogleFirst = "retry:first";

    private async Task<Page<ChatSummary>> LegacyChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default)
    {
        var expected = GoogleMessagesExpected;
        var empty = new Page<ChatSummary>([], false, null, null);
        var first = olderThan is null;
        var combined = first ? null : CombinedChatCursor.IsCombined(olderThan)
            ? CombinedChatCursor.Decode(olderThan) ?? throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.InvalidResponse, "That list position is no longer valid. Refresh chats.")
            : new CombinedChatCursor(olderThan, new Dictionary<string, string>());
        var googleCursor = combined?.Google;
        var readGoogle = expected && (first || googleCursor is not null);
        // Each provider owns a separate helper. Begin independent reads before awaiting any of them. A sign-in being renewed
        // is quietly unavailable: "could not refresh" appeared once launch's notices were released and went again when the
        // renewal's reconnect resynced the list a few seconds later. If the renewal fails, the connection notice says so.
        var googleTask = readGoogle ? NativeBeeper.Presentation.Services.PartialChatPages.ReadAsync(
            () => google.CanReceive ? google.GetChatsAsync(googleCursor == RetryGoogleFirst ? null : googleCursor, cancellationToken)
                : Task.FromException<Page<ChatSummary>>(google.SignInSettling ? GoogleReadFailure.Renewing() : new InvalidOperationException("not_connected")),
            "google-direct:", "Google Messages", googleCursor ?? RetryGoogleFirst, cancellationToken) : Task.FromResult(empty);
        var voiceTask = voice.GetChatPageAsync(first ? null : combined!.Voice, cancellationToken);
        var instagramTask = first ? NativeBeeper.Presentation.Services.PartialChatPages.ReadAsync(
            () => instagram.GetChatsAsync(null, cancellationToken), "instagram:", "Instagram", null, cancellationToken) : Task.FromResult(empty);
        var messengerTask = first ? NativeBeeper.Presentation.Services.PartialChatPages.ReadAsync(
            () => messenger.GetChatsAsync(null, cancellationToken), "messenger:", "Messenger", null, cancellationToken) : Task.FromResult(empty);
        await Task.WhenAll(googleTask, voiceTask, instagramTask, messengerTask);
        var chats = await googleTask;
        var (voiceChats, voiceNext, voiceFailed, voiceUnavailable) = await voiceTask;
        var insta = await instagramTask; var meta = await messengerTask;
        var merged = chats.Items.Concat(voiceChats).Concat(insta.Items).Concat(meta.Items)
            .OrderByDescending(chat => chat.LastActivity ?? DateTimeOffset.MinValue).ThenBy(chat => chat.Id, StringComparer.Ordinal).ToArray();
        foreach (var chat in merged) Drift.Observe(chat);
        var googleMore = chats.HasMore && chats.OldestCursor is not null;
        var retry = voiceFailed > 0 || chats.UnavailableScopes?.Count > 0 ? Math.Min(1_000_000, (combined?.Retry ?? 0) + 1) : 0;
        var warnings = new[] { chats.Warning, insta.Warning, meta.Warning,
            voiceFailed > 0 ? "Some Google Voice conversations could not refresh. Their saved conversations remain; choose Load more to retry." : null };
        var unavailable = (chats.UnavailableScopes ?? []).Concat(voiceUnavailable)
            .Concat(insta.UnavailableScopes ?? []).Concat(meta.UnavailableScopes ?? []).Distinct().ToArray();
        return new(merged, voiceNext.Count > 0 || chats.HasMore,
            CombinedChatCursor.Next(chats.OldestCursor, googleMore, voiceNext, retry), chats.NewestCursor,
            Warning: string.Join(" ", warnings.Where(value => !string.IsNullOrEmpty(value))), UnavailableScopes: unavailable);
    }

    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default)
        => Observed(await (Insta(chatId) ? InstagramAccount(chatId).GetChatAsync(chatId, cancellationToken)
            : Voice(chatId) ? voice.GetChatAsync(chatId, cancellationToken)
            : Mine(chatId) ? Messenger(chatId).GetChatAsync(chatId, cancellationToken) : Google(chatId).GetChatAsync(chatId, cancellationToken)).ConfigureAwait(false));

    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        var page = await (Insta(chatId) ? InstagramAccount(chatId).GetMessagesAsync(chatId, olderThan, cancellationToken)
            : Voice(chatId) ? voice.GetMessagesAsync(chatId, olderThan, cancellationToken)
            : Mine(chatId) ? Messenger(chatId).GetMessagesAsync(chatId, olderThan, cancellationToken) : Google(chatId).GetMessagesAsync(chatId, olderThan, cancellationToken));
        // A page Google's or Instagram's helper has just read hands its messages out for this session, so their pictures
        // can now be fetched (MediaNews): one waiting for exactly that asks again, and one refused after it is gone.
        // Not Messenger's: its "unavailable" can be a Facebook address that expired and is being fetched again behind
        // the answer, which a page read proves nothing about; its events and catch-up already say what it handed out.
        if (Insta(chatId) || !Voice(chatId) && !Mine(chatId))
            foreach (var read in page.Items.GroupBy(message => message.ChatId))
                NativeBeeper.Presentation.Services.MediaNews.Read(read.Key, read.Select(message => message.Id));
        return page;
    }

    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default)
        => Insta(chatId) ? Task.CompletedTask
            : Voice(chatId) ? Task.CompletedTask
            : Mine(chatId) ? Messenger(chatId).LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken)
            : Google(chatId) is ILinkPreviewLoader previews ? previews.LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken) : Task.CompletedTask;

    // Editing and taking back a message. Messenger can do both on either of its connections; Google Messages can
    // remove a message but has no way to edit one, so that half stays refused by name. Google Voice can do neither.
    public Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).EditAsync(chatId, messageId, originalText, text, cancellationToken)
            : Voice(chatId) ? Task.FromException(GoogleVoiceBackend.Refused("Google Voice can't edit a sent message."))
            : Mine(chatId) ? Messenger(chatId).EditAsync(chatId, messageId, originalText, text, cancellationToken)
            : Google(chatId).EditAsync(chatId, messageId, originalText, text, cancellationToken);

    public Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).DeleteAsync(chatId, messageId, forEveryone, cancellationToken)
            : Voice(chatId) ? Task.FromException(GoogleVoiceBackend.Refused("Google Voice can't remove a sent message."))
            : Mine(chatId) ? Messenger(chatId).DeleteAsync(chatId, messageId, forEveryone, cancellationToken)
            : Google(chatId).DeleteAsync(chatId, messageId, forEveryone, cancellationToken);

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).GetSendPermissionAsync(chatId, cancellationToken)
            : Voice(chatId) ? voice.GetSendPermissionAsync(chatId)
            : Mine(chatId) ? Messenger(chatId).GetSendPermissionAsync(chatId, cancellationToken) : Google(chatId).GetSendPermissionAsync(chatId, cancellationToken);

    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).SendTextAsync(chatId, text, cancellationToken)
            : Voice(chatId) ? voice.SendTextAsync(chatId, text, cancellationToken)
            : Mine(chatId) ? Messenger(chatId).SendTextAsync(chatId, text, cancellationToken) : Google(chatId).SendTextAsync(chatId, text, cancellationToken);

    public Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).ResolveMessageAsync(chatId, messageId, cancellationToken)
            : Voice(chatId) ? voice.ResolveMessageAsync(chatId, messageId, cancellationToken)
            : Mine(chatId) ? Messenger(chatId).ResolveMessageAsync(chatId, messageId, cancellationToken) : Google(chatId).ResolveMessageAsync(chatId, messageId, cancellationToken);

    // Routed like ResolveMessageAsync, but only ever into memory each backend already holds: no provider request, no
    // page, no conversation read. A merged id belongs to no service and has no messages of its own. A copy filed under
    // another conversation is not this one's.
    public async ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        if (NativeBeeper.Presentation.Services.MergedConversations.IsMerged(chatId)) return null;
        ChatMessage? found;
        try
        {
            found = Insta(chatId) ? InstagramAccount(chatId).PeekMessage(messageId)
                : Voice(chatId) ? voice.PeekMessage(chatId, messageId)
                : Mine(chatId) ? await Messenger(chatId).PeekMessageAsync(chatId, messageId, cancellationToken)
                : Google(chatId).PeekMessage(chatId, messageId);
        }
        catch (Exception) { return null; }
        return found?.ChatId == chatId && found.Id == messageId ? found : null;
    }

    // The same memory, whole copies only (IMessagePeek.PeekWholeAsync). Instagram leaves out the list's words for a photo
    // or a share; Google's, Voice's and Messenger's memory holds each message as their own resolve gives it, so theirs
    // answer as above. Still never a request: Messenger's is its helper's own store, skipped when the pipe is busy.
    // Instagram alone keeps some messages in part: the list's words for a photo or a share.
    public bool HoldsPartialCopies(string chatId) => Insta(chatId);
    public async ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        if (NativeBeeper.Presentation.Services.MergedConversations.IsMerged(chatId)) return null;
        ChatMessage? found;
        try
        {
            found = Insta(chatId) ? InstagramAccount(chatId).PeekWholeMessage(messageId)
                : Voice(chatId) ? voice.PeekMessage(chatId, messageId)
                : Mine(chatId) ? await Messenger(chatId).PeekMessageAsync(chatId, messageId, cancellationToken)
                : Google(chatId).PeekMessage(chatId, messageId);
        }
        catch (Exception) { return null; }
        return found?.ChatId == chatId && found.Id == messageId ? found : null;
    }

    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).CanMarkReadAsync(chatId, cancellationToken)
            : Voice(chatId) ? voice.CanMarkReadAsync(chatId)
            : Mine(chatId) ? Messenger(chatId).CanMarkReadAsync(chatId, cancellationToken) : Google(chatId).CanMarkReadAsync(chatId, cancellationToken);

    // Every read marker reaches a service through here, and every one the app means to send comes through
    // PrivateReadingGate first. One for a private-reading service that did not is refused and logged, not sent.
    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default) => TellReadAsync(chatId, messageId, cancellationToken);

    // Whether the service was told (false: private reading kept it). A read it took is remembered for the drift check.
    private async Task<bool> TellReadAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        if (NativeBeeper.Presentation.Services.PrivateReadingGate.Refuses(reading, chatId, "read")) return false;
        await (Insta(chatId) ? InstagramAccount(chatId).MarkReadAsync(chatId, messageId, cancellationToken)
            : Voice(chatId) ? voice.MarkReadAsync(chatId, cancellationToken)
            : Mine(chatId) ? Messenger(chatId).MarkReadAsync(chatId, messageId, cancellationToken) : Google(chatId).MarkReadAsync(chatId, messageId, cancellationToken)).ConfigureAwait(false);
        Drift.Accepted(chatId, messageId);
        return true;
    }

    // Reads a service took and then did not keep (ReadDrift): seen in the conversations passing back through here, told once more.
    private ReadDrift? drift;
    internal ReadDrift Drift => LazyInitializer.EnsureInitialized(ref drift, () => new ReadDrift(TellReadAsync, GetChatAsync, AppLog.Write));

    private ChatSummary Observed(ChatSummary chat) { Drift.Observe(chat); return chat; }

    public bool HasLocalActionOptions => ((IMessageActions)google).HasLocalActionOptions;

    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).GetOptionsAsync(chatId, messageId, cancellationToken)
            : Voice(chatId) ? Task.FromResult(new MessageActionOptions(false, [], []))
            : Mine(chatId) ? Messenger(chatId).GetOptionsAsync(chatId, messageId, cancellationToken) : Google(chatId).GetOptionsAsync(chatId, messageId, cancellationToken);

    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).ReplyAsync(chatId, messageId, text, cancellationToken)
            : Voice(chatId) ? Task.FromException<SendReceipt>(GoogleVoiceBackend.Refused("Google Voice doesn't support replies."))
            : Mine(chatId) ? Messenger(chatId).ReplyAsync(chatId, messageId, text, cancellationToken) : Google(chatId).ReplyAsync(chatId, messageId, text, cancellationToken);

    public Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default)
        => Insta(chatId) ? InstagramAccount(chatId).ReactAsync(chatId, messageId, reaction, remove, cancellationToken)
            : Voice(chatId) ? Task.FromException(GoogleVoiceBackend.Refused("Google Voice doesn't support reactions."))
            : Mine(chatId) ? Messenger(chatId).ReactAsync(chatId, messageId, reaction, remove, cancellationToken) : Google(chatId).ReactAsync(chatId, messageId, reaction, remove, cancellationToken);

    public async Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken = default)
        // Instagram's own archive is not an operation this build performs, so the conversation is described without
        // offering to change it.
        => Insta(chatId) ? new ChatSettings(await InstagramAccount(chatId).GetChatAsync(chatId, cancellationToken), false)
            : Voice(chatId) ? new ChatSettings(await voice.GetChatAsync(chatId, cancellationToken), true)
            : Mine(chatId) ? await Messenger(chatId).GetSettingsAsync(chatId, cancellationToken) : await Google(chatId).GetSettingsAsync(chatId, cancellationToken);

    public Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default)
        => Insta(chatId) ? Read<ChatSummary>("change what a conversation is")
            : Voice(chatId) ? setting switch
            {
                ChatSetting.Archived => voice.SetArchivedAsync(chatId, value, cancellationToken),
                ChatSetting.Spam when !value => voice.RemoveSpamAsync(chatId, cancellationToken),
                ChatSetting.Spam => Task.FromException<ChatSummary>(GoogleVoiceBackend.Refused("Message Link can remove a Google Voice conversation from spam, but cannot mark one as spam.")),
                _ => Task.FromException<ChatSummary>(GoogleVoiceBackend.Refused("That setting is kept on this PC only, not on Google Voice.")),
            }
            : Mine(chatId) ? Messenger(chatId).SetAsync(chatId, setting, value, cancellationToken) : Google(chatId).SetAsync(chatId, setting, value, cancellationToken);

    public Task DeleteChatAsync(string chatId, CancellationToken cancellationToken = default)
        => Insta(chatId) ? Read("delete a conversation")
            : Voice(chatId) ? voice.DeleteChatAsync(chatId, cancellationToken)
            : Mine(chatId)
            ? Messenger(chatId).DeleteChatAsync(chatId, cancellationToken)
            : Google(chatId).DeleteChatAsync(chatId, cancellationToken);
}
