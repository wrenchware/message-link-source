using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleVoice;

// Google Voice conversations in the shared inbox, from every signed-in Voice account. In development: conversations,
// history, photos, voicemail, live changes, read state, archive, deleting conversations and sending texts and photos; what
// is not built is refused by name.
internal sealed partial class GoogleVoiceBackend : IRealtimeFeed
{
    internal const string RetryFirstPage = "retry:first";
    private const int PageSize = 50;
    private const int Chunk = 512 * 1024;
    private const int Limit = 50 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly GoogleVoiceAccounts accounts;
    private readonly IPhotoReencoder photos;
    // Bounded and dropping the oldest; a drop reaches the reader as a resync.
    private readonly RealtimeBuffer realtime = new(512, GoogleVoiceLog.Write);
    // The last summary of each conversation and recent messages, so a single conversation or message can be answered
    // without listing everything again.
    private readonly ConcurrentDictionary<string, ChatSummary> chats = new(StringComparer.Ordinal);
    private readonly GoogleVoiceChatReads reads;
    private readonly MessageLink.Presentation.Services.MessageLookupCache messages = new();
    private readonly SemaphoreSlim accountReads = new(4, 4);
    public bool IsConfigured(string scope) => accounts.KnownAccountKeys.Any(key => GoogleVoiceModels.Qualify(key, "") == scope);
    public bool AnyConnected => accounts.Accounts.Any(account => account.IsConnected);
    public bool IsScopeConnected(string scope) => accounts.Accounts.Any(account =>
        account.IsConnected && GoogleVoiceModels.Qualify(account.Key, "") == scope);
    public bool IsConnected(string chatId) => GoogleVoiceModels.Parse(chatId) is { } id && accounts.Accounts.Any(account => account.Key == id.Key && account.IsConnected);

    public GoogleVoiceBackend(GoogleVoiceAccounts accounts, IPhotoReencoder photos)
    {
        this.accounts = accounts;
        this.photos = photos;
        reads = new(chats);
        accounts.ConversationsChanged += OnConversationsChanged;
        accounts.Changed += OnAccountsChanged;
    }


    private GoogleVoiceAccount AccountFor(string key) =>
        accounts.Accounts.FirstOrDefault(account => account.Key == key && account.IsConnected)
        ?? throw new BackendException(accounts.Accounts.Any(account => account.Key == key) ? BackendFailure.NotReady : BackendFailure.Unavailable,
            "This Google Voice account isn't connected. Check Google Voice in Settings.");

    // One account's first page, read once for every caller that missed the cache meanwhile (GoogleVoiceChatReads). The
    // helper finishes a list read even when its caller gives up (ui.chats is not a cancellable read), so the shared read
    // does the same upstream work a lone caller would.
    private Task<ChatSummary[]> ReadChatsAsync(GoogleVoiceAccount account, CancellationToken cancellationToken) =>
        reads.Share(account.Key, async read =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (stale, outcome) = (0, "ok");
            try
            {
                await accountReads.WaitAsync();
                try
                {
                    var result = await account.CallAsync("ui.chats", null, CancellationToken.None);
                    var found = (result.Deserialize<VoiceChat[]>(Json) ?? []).Select(chat => GoogleVoiceModels.Map(account.Key, chat, account.Number)).ToArray();
                    if (accounts.Accounts.Contains(account)) { stale = reads.Remember(found, read.Since); account.NoteConversationCount(found.Length); }
                    return found;
                }
                finally { accountReads.Release(); }
            }
            catch (Exception failure) { outcome = GoogleVoiceLog.Reason(failure); return []; }
            finally
            {
                GoogleVoiceLog.Write($"chats read account={GoogleVoiceLog.Tag(account.Key)} reason=miss ms={watch.ElapsedMilliseconds} " +
                    $"joined={reads.End(account.Key, read)} stale_discarded={stale} outcome={outcome}");
            }
        }).WaitAsync(cancellationToken);

    public async Task<(IReadOnlyList<ChatSummary> Chats, Dictionary<string, string> Next, int Failed, string[] Unavailable)> GetChatPageAsync(
        IReadOnlyDictionary<string, string>? cursors, CancellationToken cancellationToken)
    {
        var snapshot = accounts.Accounts;
        var keys = accounts.KnownAccountKeys;
        var pages = await Task.WhenAll(keys.Where(key => cursors is null || cursors.ContainsKey(key)).Select(async key =>
        {
            var account = snapshot.FirstOrDefault(account => account.Key == key);
            var cursor = cursors?.GetValueOrDefault(key) ?? "";
            if (cursor == RetryFirstPage) cursor = "";
            await accountReads.WaitAsync(cancellationToken);
            try
            {
                if (account is null || !account.IsConnected) throw new InvalidOperationException("not_connected");
                var (watch, since) = (System.Diagnostics.Stopwatch.StartNew(), reads.Begin());
                var result = await account.CallAsync("ui.chatsPage", new { cursor }, cancellationToken);
                var page = result.Deserialize<VoiceChatPage>(Json) ?? new VoiceChatPage([], null);
                var found = (page.Chats ?? []).Select(chat => GoogleVoiceModels.Map(account.Key, chat, account.Number)).ToArray();
                var stale = 0;
                if (accounts.Accounts.Contains(account))
                {
                    stale = reads.Remember(found, since);
                    // The first page is the same list ui.chats reads, so it is also the count Settings shows.
                    if (cursor.Length == 0) account.NoteConversationCount(found.Length);
                }
                GoogleVoiceLog.Write($"chats read account={GoogleVoiceLog.Tag(key)} reason=page first={cursor.Length == 0} ms={watch.ElapsedMilliseconds} stale_discarded={stale}");
                return (Key: key, Chats: found, Next: !string.IsNullOrEmpty(page.Next) && page.Next != cursor ? page.Next : null, Failed: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                return (Key: key, Chats: Array.Empty<ChatSummary>(), Next: cursor.Length == 0 ? RetryFirstPage : cursor, Failed: true);
            }
            finally { accountReads.Release(); }
        }));
        return (pages.SelectMany(page => page.Chats).ToArray(),
            pages.Where(page => page.Next is not null).ToDictionary(page => page.Key, page => page.Next!, StringComparer.Ordinal),
            pages.Count(page => page.Failed), pages.Where(page => page.Failed).Select(page => GoogleVoiceModels.Qualify(page.Key, "")).ToArray());
    }

    private readonly Dictionary<string, bool> connectionStates = new(StringComparer.Ordinal);
    private void OnAccountsChanged(object? sender, EventArgs args)
    {
        var snapshot = accounts.Accounts;
        var keys = snapshot.Select(account => account.Key).ToHashSet(StringComparer.Ordinal);
        lock (connectionStates)
        {
            var removed = connectionStates.Keys.Where(key => !keys.Contains(key)).ToHashSet(StringComparer.Ordinal);
            if (removed.Count > 0)
            {
                messages.RemoveWhere(message => GoogleVoiceModels.Parse(message.ChatId) is { } id && removed.Contains(id.Key));
                foreach (var chat in chats.Keys)
                    if (GoogleVoiceModels.Parse(chat) is { } id && removed.Contains(id.Key)) chats.TryRemove(chat, out _);
            }
            foreach (var key in connectionStates.Keys.Concat(keys).Distinct().ToArray())
            {
                var connected = snapshot.Any(account => account.Key == key && account.IsConnected);
                if (!connectionStates.TryGetValue(key, out var before) || before != connected)
                    realtime.Writer.TryWrite(new(connected ? RealtimeKind.Connected : RealtimeKind.Disconnected, Scope: GoogleVoiceModels.Qualify(key, "")));
                if (keys.Contains(key)) connectionStates[key] = connected; else connectionStates.Remove(key);
            }
        }
    }

    public async Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken)
    {
        if (chats.TryGetValue(chatId, out var chat)) return chat;
        // Only the conversation's own account is read: another account's summaries are dropped only by its own changes.
        // The answer comes from the read itself, because a change landing meanwhile keeps this summary out of the cache
        // while the conversation plainly exists; that change brings its own read.
        if (GoogleVoiceModels.Parse(chatId) is { } id && accounts.Accounts.FirstOrDefault(account => account.Key == id.Key && account.IsConnected) is { } owner &&
            (await ReadChatsAsync(owner, cancellationToken)).FirstOrDefault(found => found.Id == chatId) is { } read)
            return read;
        if (chats.TryGetValue(chatId, out chat)) return chat;
        // The text conversation a call routes to may not exist yet (the client works that route out from the call row
        // itself); it is the one New chat would open, and Voice creates it on the first send.
        if (chats.Values.Select(GoogleVoiceModels.TextRouteForCall).FirstOrDefault(text => text?.Id == chatId) is { } route)
            return chats.GetOrAdd(chatId, route);
        // Older than every listing since launch (a missed call from weeks back, still in the saved list): its own thread.
        if (GoogleVoiceModels.Parse(chatId) is { } older && accounts.Accounts.FirstOrDefault(account => account.Key == older.Key && account.IsConnected) is { } holder)
        {
            var result = await holder.CallAsync("ui.chat", new { chatId = older.Id }, cancellationToken);
            if (result.Deserialize<VoiceChat>(Json) is { } found && GoogleVoiceModels.Map(holder.Key, found, holder.Number) is { } mapped && mapped.Id == chatId)
                return chats.GetOrAdd(chatId, mapped);
        }
        throw new BackendException(BackendFailure.Unavailable, "That Google Voice conversation isn't among the recent ones yet.");
    }

    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan, CancellationToken cancellationToken)
    {
        var (key, native) = GoogleVoiceModels.Parse(chatId) ?? throw new BackendException(BackendFailure.InvalidResponse, "Not a Google Voice conversation.");
        var account = AccountFor(key);
        var result = await account.CallAsync("ui.messages", new { chatId = native, count = PageSize, before = olderThan ?? "" }, cancellationToken);
        var page = result.Deserialize<VoicePage>(Json) ?? new VoicePage([], null);
        var names = chats.TryGetValue(chatId, out var chat) ? chat.Participants.ToDictionary(person => person.Id, person => person.DisplayName) : null;
        var mapped = (page.Messages ?? []).Select(message => GoogleVoiceModels.Map(key, message, names)).ToArray();
        if (accounts.Accounts.Contains(account)) foreach (var message in mapped) messages.Put(message);
        var more = !string.IsNullOrEmpty(page.Next);
        return new(mapped, more, more ? page.Next : null, null);
    }

    public async Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        var accountId = GoogleVoiceModels.Parse(chatId) ?? throw new BackendException(BackendFailure.InvalidResponse, "Not a Google Voice conversation.");
        _ = AccountFor(accountId.Key);
        if (messages.TryGetValue(messageId, out var known) && known.ChatId == chatId) return known;
        var page = await GetMessagesAsync(chatId, null, cancellationToken);
        // Concurrent history reads can churn the cache; this authoritative page still resolves the lookup.
        return page.Items.FirstOrDefault(message => message.Id == messageId && message.ChatId == chatId) is { } found ? found
            : throw new BackendException(BackendFailure.Unavailable, "That Google Voice message isn't in the recent history.");
    }

    // The cache half of ResolveMessageAsync alone: a miss reads no history.
    internal ChatMessage? PeekMessage(string chatId, string messageId) =>
        messages.TryGetValue(messageId, out var known) && known.ChatId == chatId ? known : null;

    // Reading a conversation here marks it read in that Voice account, as Voice's website does. Voice keeps one read state
    // per conversation, not per message, and tells the other people nothing.
    // Any conversation of a connected account, listed since launch or not: an older one (Mom's missed call from weeks back)
    // was refused here without a word, and opening it never read it (2026-10-07).
    public Task<bool> CanMarkReadAsync(string chatId) =>
        Task.FromResult(GoogleVoiceModels.Parse(chatId) is { } id && accounts.Accounts.Any(account => account.Key == id.Key && account.IsConnected));

    public async Task MarkReadAsync(string chatId, CancellationToken cancellationToken)
    {
        var (key, native) = GoogleVoiceModels.Parse(chatId) ?? throw new BackendException(BackendFailure.InvalidResponse, "Not a Google Voice conversation.");
        var account = AccountFor(key);
        try
        {
            var result = await account.CallAsync("ui.markRead", new { chatId = native }, cancellationToken);
            if (!result.TryGetProperty("outcome", out var outcome) || outcome.GetString() != "accepted")
                throw new InvalidOperationException("mark_read_failed");
            GoogleVoiceLog.Write($"mark read account={GoogleVoiceLog.Tag(key)} accepted listed={chats.ContainsKey(chatId)}");
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Quiet in the view, but never recorded as read: the next viewing tries again.
            GoogleVoiceLog.Write($"mark read account={GoogleVoiceLog.Tag(key)} refused reason={GoogleVoiceLog.Reason(failure)}");
            throw;
        }
        // Only over the summary it read: one a change dropped meanwhile stays dropped. Then announced, because nothing else
        // will: the helper records this read itself, so its next listing sees no change to report, and the list's own copy
        // stayed unread under the app's local read until the next launch (the console showed it, 2026-10-07). One not listed
        // since launch is announced too: the list reads it back from its own thread, so it shows what Voice now says.
        if (!chats.TryGetValue(chatId, out var chat) ||
            chat.UnreadCount != 0 && chats.TryUpdate(chatId, chat with { UnreadCount = 0 }, chat))
            realtime.Writer.TryWrite(new(RealtimeKind.ChatChanged, null, [chatId], null, DateTimeOffset.UtcNow));
    }

    // Texts go to a conversation's own Voice account while it is connected. A conversation of an account that is still
    // reconnecting waits rather than refusing.
    // Voice keeps calls and voicemail apart from texts: a one-to-one text conversation is "t." and the number, and a call
    // or voicemail item has an id of its own. Texting into call history is refused; New chat opens the text conversation.
    private const string CallHistoryReason = "This is call and voicemail history. To text this person, start a conversation from New chat.";
    private bool CallHistoryOnly(string chatId) => chats.TryGetValue(chatId, out var chat) && GoogleVoiceModels.CallHistoryOnly(chat);

    public Task<SendPermission> GetSendPermissionAsync(string chatId)
    {
        if (GoogleVoiceModels.Parse(chatId) is not { } id) return Task.FromResult(new SendPermission(false, "Not a Google Voice conversation."));
        var account = accounts.Accounts.FirstOrDefault(candidate => candidate.Key == id.Key);
        return Task.FromResult(account switch
        {
            null => new SendPermission(false, "Sign in to this Google Voice account in Settings to send messages."),
            _ when CallHistoryOnly(chatId) => new SendPermission(false, CallHistoryReason),
            { IsConnected: false } => new SendPermission(false, "Connecting to Google Voice…", Pending: true),
            _ => new SendPermission(true, ""),
        });
    }

    // Sent once; an answer that leaves the outcome open is reported as unknown and never sent again (the helper stops
    // libgv's own retries for sends). The text is never logged.
    public async Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken)
    {
        var (key, native) = GoogleVoiceModels.Parse(chatId) ?? throw new SendException(false, "Not a Google Voice conversation.");
        if (CallHistoryOnly(chatId)) throw new SendException(false, CallHistoryReason);
        var account = accounts.Accounts.FirstOrDefault(candidate => candidate.Key == key && candidate.IsConnected)
            ?? throw new SendException(false, "Google Voice isn't connected. Check Google Voice in Settings.");
        GoogleVoiceLog.Write($"send account={GoogleVoiceLog.Tag(key)} length={text.Length}");
        JsonElement result;
        try { result = await account.CallAsync("ui.sendText", new { chatId = native, text }, cancellationToken); }
        catch (OperationCanceledException) { GoogleVoiceLog.Write("send cancelled"); throw; }
        catch (InvalidOperationException failure)
        {
            GoogleVoiceLog.Write($"send refused reason={GoogleVoiceLog.Reason(failure)}");
            throw new SendException(SendOutcomeUnknown(failure.Message), ExplainSend(failure.Message));
        }
        catch (Exception) { GoogleVoiceLog.Write("send failed in transport"); throw new SendException(true, ExplainSend("")); }
        var id = result.TryGetProperty("id", out var sent) ? sent.GetString() : null;
        if (string.IsNullOrEmpty(id))
        {
            GoogleVoiceLog.Write("send returned no message id");
            throw new SendException(true, ExplainSend(""));
        }
        GoogleVoiceLog.Write($"send accepted account={GoogleVoiceLog.Tag(key)}");
        return new(chatId, GoogleVoiceModels.Qualify(key, id));
    }

    public async Task<SendPermission> GetPhotoPermissionAsync(string chatId, AttachmentFile file) =>
        GoogleVoicePhotos.Refusal(file) is { } reason ? new SendPermission(false, reason) : await GetSendPermissionAsync(chatId);

    // A photo, with its caption as the message's text. Until every byte is staged with the helper nothing has been sent,
    // and a failure says so; after that the outcome is Google's to report, and an uncertain one is never sent again.
    private readonly SemaphoreSlim uploads = new(1, 1);
    public async Task<SendReceipt> SendPhotoAsync(string chatId, AttachmentFile file, string? caption, CancellationToken cancellationToken)
    {
        var (key, native) = GoogleVoiceModels.Parse(chatId) ?? throw new SendException(false, "Not a Google Voice conversation.");
        var permission = await GetPhotoPermissionAsync(chatId, file);
        if (!permission.Allowed) throw new SendException(false, permission.Reason);
        var account = accounts.Accounts.FirstOrDefault(candidate => candidate.Key == key && candidate.IsConnected)
            ?? throw new SendException(false, "Google Voice isn't connected. Check Google Voice in Settings.");
        byte[] bytes;
        string mime;
        try { (bytes, mime) = await GoogleVoicePhotos.PrepareAsync(file, photos, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException refusal) { throw new SendException(false, refusal.Message); }
        catch (Exception) { throw new SendException(false, "That photo couldn't be prepared for Google Voice. It wasn't sent."); }
        await uploads.WaitAsync(cancellationToken);
        var submitted = false;
        try
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(Chunk, bytes.Length - offset);
                var ack = await account.CallAsync("ui.mediaStage", new { offset, data = Convert.ToBase64String(bytes, offset, count) }, cancellationToken);
                offset += count;
                if (ack.GetProperty("next").GetInt32() != offset) throw new IOException("Upload offset mismatch.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            GoogleVoiceLog.Write($"send photo account={GoogleVoiceLog.Tag(key)} bytes={bytes.Length} caption={caption?.Length ?? 0}");
            var result = await account.CallAsync("ui.sendMedia", new { chatId = native, mime, text = caption ?? "" }, cancellationToken);
            var id = result.TryGetProperty("id", out var sent) ? sent.GetString() : null;
            if (string.IsNullOrEmpty(id)) throw new SendException(true, ExplainSend(""));
            GoogleVoiceLog.Write($"send photo accepted account={GoogleVoiceLog.Tag(key)}");
            return new(chatId, GoogleVoiceModels.Qualify(key, id));
        }
        catch (SendException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure)
        {
            GoogleVoiceLog.Write($"send photo refused reason={GoogleVoiceLog.Reason(failure)} submitted={submitted}");
            throw new SendException(submitted && SendOutcomeUnknown(failure.Message), ExplainSend(failure.Message));
        }
        catch (Exception)
        {
            throw new SendException(submitted, submitted ? ExplainSend("") : "That photo couldn't be handed to Google Voice. It wasn't sent.");
        }
        finally { uploads.Release(); }
    }

    // Only these answers mean the text certainly did not leave; anything else may have reached Google.
    internal static bool SendOutcomeUnknown(string code) => code is not ("send_rejected" or "auth_required" or "invalid_input" or "login_required");

    internal static string ExplainSend(string code) => code switch
    {
        "send_rejected" => "Google Voice didn't accept this message.",
        "auth_required" => "Google Voice needs you to sign in again in Settings. The message wasn't sent.",
        "invalid_input" => "This message can't be sent with Google Voice.",
        "login_required" => "Google Voice isn't connected. The message wasn't sent.",
        _ => "Google Voice didn't confirm the message. Check the conversation before sending it again.",
    };

    // Archiving changes only this account, with the thread-attributes request that marks a conversation read.
    public async Task<ChatSummary> SetArchivedAsync(string chatId, bool archived, CancellationToken cancellationToken)
    {
        var (key, native) = GoogleVoiceModels.Parse(chatId) ?? throw new SendException(false, "Not a Google Voice conversation.");
        var account = accounts.Accounts.FirstOrDefault(candidate => candidate.Key == key && candidate.IsConnected)
            ?? throw new SendException(false, "Google Voice isn't connected. Check Google Voice in Settings.");
        try { await account.CallAsync("ui.updateChat", new { chatId = native, archived }, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure) when (failure.Message is "update_failed" or "auth_required" or "invalid_input" or "login_required")
        {
            GoogleVoiceLog.Write($"archive refused reason={failure.Message}");
            throw new SendException(false, failure.Message == "auth_required"
                ? "Google Voice needs you to sign in again in Settings." : "Google Voice would not change that conversation.");
        }
        catch (Exception failure)
        {
            GoogleVoiceLog.Write($"archive unknown reason={GoogleVoiceLog.Reason(failure)}");
            throw new SendException(true, "The archive result is unknown. Refresh chats before trying again.");
        }
        GoogleVoiceLog.Write($"archive account={GoogleVoiceLog.Tag(key)} archived={archived}");
        var chat = await GetChatAsync(chatId, cancellationToken);
        chats[chatId] = chat = chat with { IsArchived = archived };
        return chat;
    }

    // Removing spam changes only this account. The app deliberately does not expose the inverse operation.
    public async Task<ChatSummary> RemoveSpamAsync(string chatId, CancellationToken cancellationToken)
    {
        var (key, native) = GoogleVoiceModels.Parse(chatId) ?? throw new SendException(false, "Not a Google Voice conversation.");
        var account = accounts.Accounts.FirstOrDefault(candidate => candidate.Key == key && candidate.IsConnected)
            ?? throw new SendException(false, "Google Voice isn't connected. Check Google Voice in Settings.");
        try { await account.CallAsync("ui.updateChat", new { chatId = native, spam = false }, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure) when (failure.Message is "update_failed" or "auth_required" or "invalid_input" or "login_required")
        {
            GoogleVoiceLog.Write($"not spam refused reason={failure.Message}");
            throw new SendException(false, failure.Message == "auth_required"
                ? "Google Voice needs you to sign in again in Settings." : "Google Voice would not remove that conversation from spam.");
        }
        catch (Exception failure)
        {
            GoogleVoiceLog.Write($"not spam unknown reason={GoogleVoiceLog.Reason(failure)}");
            throw new SendException(true, "The Not spam result is unknown. Refresh chats before trying again.");
        }
        GoogleVoiceLog.Write($"not spam account={GoogleVoiceLog.Tag(key)} accepted");
        var chat = await GetChatAsync(chatId, cancellationToken);
        chats[chatId] = chat = chat with { IsSpam = false };
        return chat;
    }

    // Deletes a conversation from this account only, asked from the conversation menu after its confirmation (2026-10-04).
    // The helper asks Google once and stops libgv's own repeat (delete.go); an answer that leaves the outcome open is
    // reported as unknown and never asked again. On success the conversation leaves this backend's memory before the
    // deletion is announced, so nothing reading in reaction to it finds the old summary.
    public async Task DeleteChatAsync(string chatId, CancellationToken cancellationToken)
    {
        var (key, native) = GoogleVoiceModels.Parse(chatId) ?? throw new SendException(false, "Not a Google Voice conversation.");
        var account = accounts.Accounts.FirstOrDefault(candidate => candidate.Key == key && candidate.IsConnected)
            ?? throw new SendException(false, GoogleVoiceModels.ExplainDelete("not_connected"));
        GoogleVoiceLog.Write($"delete account={GoogleVoiceLog.Tag(key)}");
        try { await account.CallAsync("ui.deleteChat", new { chatId = native }, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            GoogleVoiceLog.Write("delete cancelled");
            throw new SendException(true, GoogleVoiceModels.ExplainDelete(""));
        }
        catch (InvalidOperationException failure)
        {
            var reason = GoogleVoiceLog.Reason(failure);
            var unknown = GoogleVoiceModels.DeleteOutcomeUnknown(failure.Message);
            GoogleVoiceLog.Write($"delete {(unknown ? "unknown" : "refused")} reason={reason}");
            throw new SendException(unknown, GoogleVoiceModels.ExplainDelete(unknown ? "" : failure.Message));
        }
        catch (Exception failure)
        {
            GoogleVoiceLog.Write($"delete unknown reason={GoogleVoiceLog.Reason(failure)}");
            throw new SendException(true, GoogleVoiceModels.ExplainDelete(""));
        }
        GoogleVoiceLog.Write($"delete accepted account={GoogleVoiceLog.Tag(key)}");
        GoogleVoiceChanges.Deleted(key, chatId, reads, messages, realtime.Writer, DateTimeOffset.UtcNow);
    }

    // What Google Voice itself cannot do, and what is not built here yet, refused by name.
    public static Exception Refused(string reason) => new SendException(false, reason);

    public bool CanSave(MessageAttachment attachment) =>
        GoogleVoiceModels.ParseMedia(attachment.Id) is { } media && accounts.Accounts.Any(account => account.Key == media.Key && account.IsConnected);

    public async Task<byte[]> ReadImageAsync(MessageAttachment attachment, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await CopyToAsync(attachment, memory, cancellationToken);
        return memory.ToArray();
    }

    public async Task CopyToAsync(MessageAttachment attachment, Stream destination, CancellationToken cancellationToken)
    {
        var (key, id) = GoogleVoiceModels.ParseMedia(attachment.Id) ?? throw new BackendException(BackendFailure.InvalidResponse, "Not a Google Voice attachment.");
        var account = AccountFor(key);
        var offset = 0;
        var size = -1;
        while (true)
        {
            var response = await account.CallAsync("ui.mediaRead", new { id, offset }, cancellationToken);
            var total = response.GetProperty("total").GetInt32();
            var next = response.GetProperty("next").GetInt32();
            var bytes = response.GetProperty("data").GetBytesFromBase64();
            // The size must not change between chunks, or the picture would be stitched from two different downloads.
            if (size < 0) size = total;
            if (total <= 0 || total > Limit || total != size || bytes.Length > Chunk || next != offset + bytes.Length || next > total)
                throw new IOException("Invalid media chunk.");
            await destination.WriteAsync(bytes, cancellationToken);
            offset = next;
            if (offset >= total) return;
        }
    }

    private void OnConversationsChanged(GoogleVoiceAccount account, JsonElement changes) =>
        GoogleVoiceChanges.Publish(account.Key, changes, reads, realtime.Writer, DateTimeOffset.UtcNow);

    public async Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        foreach (var account in accounts.Accounts.Where(account => account.IsConnected))
            await receive(new(RealtimeKind.Connected, Scope: GoogleVoiceModels.Qualify(account.Key, "")), cancellationToken);
        await realtime.DrainAsync(receive, cancellationToken);
    }
}
