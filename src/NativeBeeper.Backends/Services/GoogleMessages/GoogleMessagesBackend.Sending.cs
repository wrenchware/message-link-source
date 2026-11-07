using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend : INotificationActionHints
{
    public (bool Reply, bool Read) GetNotificationActions(string chatId)
        => connected && chats.TryGetValue(Models.Native(chatId), out var chat)
            ? (!chat.ReadOnly, true) : (false, false);
    private string PendingPrefix => Models.PendingPrefix;
    internal string SendTimingSummary { get; private set; } = "No text sends measured yet this session.";
    public async Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default)
    {
        // A known writable conversation is ready without a phone round trip, which right after launch would wait
        // behind startup reads. The helper still checks the conversation and SIM before every send and rejects without
        // sending when it isn't ready, against the phone's own description of the conversation when that is under a
        // minute old (a lookup, a list or the event the phone sends when it changes) and a fresh lookup otherwise
        // (conversation_cache.go). Trusting it for that minute was chosen on 2026-10-14: a lookup before every
        // send was about 250 ms of a 628 ms median send.
        var native = Models.Native(chatId);
        if (!chats.ContainsKey(native) && !await SavedChatsReadAsync(cancellationToken)) return new(false, "Google Messages is still starting. Try again in a moment.");
        if (GoneFromList(chatId)) return new(false, GoneFromListWords);
        if (connected && (chats.TryGetValue(native, out var known) ? known : archive.Chat(native)) is { ReadOnly: false, OutgoingId.Length: > 0 })
            return WithDefaultSmsCaution(new(true, "", 65536));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await ConnectAsync(cancellationToken);
                if (GoneFromList(chatId)) return new(false, GoneFromListWords);
                var result = await ReadAsync("ui.sendPermission", new { chatId = Models.Native(chatId) }, cancellationToken);
                return WithDefaultSmsCaution(result.Deserialize<SendPermission>(Json) ?? new(false,"Couldn't verify Google Messages send permission."));
            }
            catch (BackendException ex) when (ex.Failure == BackendFailure.Unavailable && attempt < 2)
            { await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken); }
        }
    }
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default)
        => SendTextCoreAsync(chatId,text,null,cancellationToken);
    private async Task<SendReceipt> SendTextCoreAsync(string chatId, string text, string? replyTo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 65536) throw new SendException(false,"Enter a message within the text limit.");
        await ThrowIfRenumberedAsync();
        // All validation and connection work happens before the one and only send attempt.
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        var permission = await GetSendPermissionAsync(chatId,cancellationToken);
        if (!permission.Allowed) throw new SendException(false,permission.Reason);
        cancellationToken.ThrowIfCancellationRequested();
        var checkedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        GoogleMessagesLog.Write($"send chat={GoogleMessagesLog.Tag(chatId)} length={text.Length} reply={replyTo is not null} {NativeBeeper.Presentation.Services.TypingPings.Shared.Field(chatId)}");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(135));
            JsonElement result;
            // The helper answers a send with a code only when it never sent it: the checks before the one SendMessage
            // request, or Google's relay refusing that request (4xx), which it never passes to the phone (send.go). A sign-in Google
            // refused at the conversation lookup was reported as "outcome unknown" for a message that certainly never went.
            // Only the call itself is read this way; anything after it has an answer and keeps the uncertain wording. A helper
            // replaced by a renewal just before the call means the call never happened either.
            ThrowIfGoneFromList(chatId);
            var connection = helper;
            if (connection is null)
            {
                GoogleMessagesLog.Write($"send chat={GoogleMessagesLog.Tag(chatId)} outcome=not_sent reason=no_helper");
                throw new SendException(false, "Not sent. Google Messages is reconnecting; send it again in a moment.");
            }
            try { result = await connection.CallAsync("ui.sendText",new { chatId = Models.Native(chatId), text, replyTo = replyTo is null ? null : Models.Native(replyTo) },timeout.Token); }
            catch (InvalidOperationException refused)
            {
                GoogleMessagesLog.Write($"send chat={GoogleMessagesLog.Tag(chatId)} outcome=not_sent reason={GoogleMessagesLog.Reason(refused)}");
                if (refused.Message == "authentication_required") MarkSignInExpired("send");
                else if (refused.Message == "pairing_lost") MarkPairingLost("send");
                throw new SendException(false, refused.Message switch
                {
                    "authentication_required" when signIn.Held(DateTimeOffset.UtcNow) => "Not sent. Google Messages is renewing its sign-in; send it again in a moment.",
                    "authentication_required" or "pairing_lost" or "pairing_required" or "access_denied" or "rate_limited" => "Not sent. " + GoogleReadFailure.Map(refused.Message).Message,
                    "send_rejected" => "Not sent. Google refused the message before it reached your phone. Send it again in a moment.",
                    // The read wording ("the connection will retry automatically") promised a resend nothing makes.
                    _ => "Not sent. Google Messages couldn't reach your phone. Send it again when your phone is online.",
                });
            }
            // Durations only: the check, then waiting for the helper plus Google accepting the send.
            SendTimingSummary = $"Last text send: {System.Diagnostics.Stopwatch.GetElapsedTime(began, checkedAt).TotalSeconds:0.0} s send check, " +
                $"{System.Diagnostics.Stopwatch.GetElapsedTime(checkedAt).TotalSeconds:0.0} s until Google accepted it.";
            // The helper's reasons are its own fixed messages (read-only conversation, SIM information not ready).
            if (result.GetProperty("outcome").GetString() == "rejected")
            {
                // The phone refusing because Google Messages is not its default SMS app says so, and is remembered.
                var notDefault = NotDefaultSmsRefusal(result);
                GoogleMessagesLog.Write($"send chat={GoogleMessagesLog.Tag(chatId)} outcome=rejected{(notDefault is null ? "" : " reason=" + GoogleDefaultSmsApp.RefusalCode)}{GoogleMessagesLog.Conversation(result)}");
                throw new SendException(false,notDefault ?? (result.TryGetProperty("reason",out var why) && why.GetString() is { Length: > 0 } reason
                    ? reason : "Google Messages rejected the message. Check the conversation before trying again."));
            }
            // An error after the request may or may not have reached the phone: say so rather than wait on a clock for an
            // echo that, for a request the relay dropped, never comes (send.go).
            if (result.GetProperty("outcome").GetString() == "unknown")
            {
                GoogleMessagesLog.Write($"send chat={GoogleMessagesLog.Tag(chatId)} outcome=unknown{SendErrorClass(result)} ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(checkedAt).TotalMilliseconds}{GoogleMessagesLog.Conversation(result)}");
                throw new SendException(true,"The send outcome is unknown. Check Google Messages before sending again; this app will not retry automatically.");
            }
            var pendingId = result.GetProperty("pendingId").GetString();
            if (string.IsNullOrWhiteSpace(pendingId)) throw new IOException("Missing transaction identity.");
            GoogleMessagesLog.Write($"send chat={GoogleMessagesLog.Tag(chatId)} outcome=accepted ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(checkedAt).TotalMilliseconds}{SendErrorClass(result)}{GoogleMessagesLog.Conversation(result)}");
            return new(chatId,PendingPrefix+pendingId);
        }
        catch (SendException) { throw; }
        catch (Exception failure) { GoogleMessagesLog.Write($"send chat={GoogleMessagesLog.Tag(chatId)} outcome=unknown reason={GoogleMessagesLog.Reason(failure)}"); throw new SendException(true,"The send outcome is unknown. Check Google Messages before sending again; this app will not retry automatically."); }
    }
    // The helper's class for an error after the request (phone_unavailable while it stays pending), from the fixed codes only.
    internal static string SendErrorClass(JsonElement result)
        => result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
            ? " reason=" + GoogleMessagesLog.Reason(new InvalidOperationException(error.GetString())) : "";
    private async Task<ChatMessage> ResolvePendingAsync(string chatId,string pendingId,CancellationToken token)
    {
        var nativeChat = Models.Native(chatId);
        await GetChatAsync(chatId,token);
        var txn = pendingId[PendingPrefix.Length..];
        GoogleMessage? Find() => GoogleModels.FindEcho(messages.Values,nativeChat,txn);
        var message = Find();
        // Finding the initial echo is not confirmation that the phone sent it.
        // Re-read while it is pending, including when a status event was missed.
        if (message is null || Models.Map(message, chats[nativeChat]).Delivery.SendState == "PENDING")
        { await FetchMessagePageAsync(chatId,null,StatusPageSize,token); message = Find(); }
        if (message is null) throw new BackendException(BackendFailure.Unavailable,"Waiting for the phone to confirm this message.");
        return Models.Map(message,chats[nativeChat]);
    }
}
