using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend
{
    // Conversations dropped as placeholders (GoogleDirectory.IsLeftPlaceholder): Google still answers for them, so a client
    // that asks after the drop is refused rather than bringing one back. A list that carries one again lets it in.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> placeholders = new(StringComparer.Ordinal);
    private GoogleArrivalHold? arrivalHold;
    private GoogleArrivalHold ArrivalHold => LazyInitializer.EnsureInitialized(ref arrivalHold, () => new(Publish));

    // How long a new conversation's unsettled first message waits for its download to finish or move (GoogleArrivalHold).
    internal TimeSpan ArrivalHoldLimit { get; set; } = TimeSpan.FromSeconds(3);

    private bool IsPlaceholder(string chatId) => placeholders.ContainsKey(GoogleModels.Native(chatId));

    private const string GoneFromListWords = "This conversation isn't in your list any more. Open the one you want from the list.";

    // A conversation left the list, and nothing is sent to it from an old notification or a client that has not heard yet. A dropped
    // placeholder matters most: Google still answers for it, so a reply meant for a group reached the one person who wrote (audit,
    // 2026-11-01). It is never redirected to the group: the person reviews where a message goes. The hidden row is what outlives a
    // restart, as the placeholders above do not. New chat, a list or a real message lets a conversation back in.
    private bool GoneFromList(string chatId)
    {
        var native = GoogleModels.Native(chatId);
        return placeholders.ContainsKey(native) || (chats.TryGetValue(native, out var known) ? known : archive.Chat(native)) is { Hidden: true };
    }

    // Asked again just before the one send: the permission's own lookup, or a file's upload, takes time a move can land in.
    internal void ThrowIfGoneFromList(string chatId)
    {
        if (!GoneFromList(chatId)) return;
        GoogleMessagesLog.Write($"send chat={GoogleMessagesLog.Tag(chatId)} outcome=not_sent reason=not_listed");
        throw new SendException(false, GoneFromListWords);
    }

    // Right after a restart the saved rows may not be here yet, and without them a conversation hidden before the restart looks like any
    // other the phone still answers for. Their read waits at most this long, and a send without them is refused as not sent.
    private static readonly TimeSpan SavedChatsWait = TimeSpan.FromSeconds(5);
    private async Task<bool> SavedChatsReadAsync(CancellationToken cancellationToken)
    {
        if (await NotificationIdentity.ProfileAsync(tokens, cancellationToken) is not { } profile || archive.ChatsRead(profile)) return true;
        _ = archive.LoadAsync(profile, YieldArchiveWarmupAsync);
        try { return await archive.LoadChatsAsync(profile).WaitAsync(SavedChatsWait, cancellationToken); }
        catch (TimeoutException) { return false; }
    }

    // A send already queued in the helper behind a slow upload was checked before this and runs after it, so the helper is told as well.
    private void HoldHelperSends(string chatId, bool held)
    {
        if (helper is not { } connection) return;
        _ = TellAsync();
        async Task TellAsync()
        {
            try { await connection.CallAsync("ui.holdSends", new { chatId, held }, CancellationToken.None); }
            catch (Exception failure) { GoogleMessagesLog.Write($"hold sends failed chat={GoogleMessagesLog.Tag(chatId)} reason={GoogleMessagesLog.Reason(failure)}"); }
        }
    }

    // Lets a dropped placeholder back in: a list carried it, or the person started a chat with exactly this person.
    private bool LiftPlaceholder(string chatId)
    {
        if (!placeholders.TryRemove(chatId, out _)) return false;
        HoldHelperSends(chatId, false);
        return true;
    }

    // Only a finished message proves the conversation is real: the person who wrote in a group also texting the owner alone, in the
    // conversation Google opened for them. One still downloading, or retrying its download, may yet move as the first one did.
    private static bool IsFinished(GoogleMessage message) => message.Status is
        "INCOMING_COMPLETE" or "INCOMING_DELIVERED" or "INCOMING_DISPLAYED" or "OUTGOING_COMPLETE" or "OUTGOING_DELIVERED" or "OUTGOING_DISPLAYED";

    private void ReinstateIfReal(GoogleMessage message)
    {
        if (!IsFinished(message) || !LiftPlaceholder(message.ChatId)) return;
        GoogleMessagesLog.Write($"placeholder reinstated chat={GoogleMessagesLog.Tag(message.ChatId)}");
        if (archive.Chat(message.ChatId) is { Hidden: true } saved) ArchiveChats([saved with { Hidden = false }]);
    }

    private void HoldIfUnsettled(GoogleMessage message)
    {
        if (!GoogleArrivalHold.Unsettled(message) || chats.ContainsKey(message.ChatId) || placeholders.ContainsKey(message.ChatId)) return;
        lock (listedChatIds) if (listedChatIds.Contains(message.ChatId)) return;
        if (ArrivalHold.Begin(message.ChatId) is not { } token) return;
        GoogleMessagesLog.Write($"arrival held chat={GoogleMessagesLog.Tag(message.ChatId)}");
        _ = ExpireHoldAsync(message.ChatId, token);
    }

    private async Task ExpireHoldAsync(string chatId, int token)
    {
        await Task.Delay(ArrivalHoldLimit);
        var events = ArrivalHold.Expire(chatId, token);
        if (events >= 0) GoogleMessagesLog.Write($"arrival released chat={GoogleMessagesLog.Tag(chatId)} reason=limit events={events}");
    }

    private void ReleaseIfSettled(string chatId)
    {
        if (!ArrivalHold.IsHeld(chatId) || !messages.Values.Any(message => message.ChatId == chatId && !GoogleArrivalHold.Unsettled(message))) return;
        var events = ArrivalHold.Release(chatId);
        if (events >= 0) GoogleMessagesLog.Write($"arrival released chat={GoogleMessagesLog.Tag(chatId)} reason=whole events={events}");
    }

    // A message an event moved from one conversation to another. The one it left goes when it was only a placeholder: held,
    // nothing of it was ever published; shown, clients are told it is gone.
    private void MessageMoved(string fromChatId, GoogleMessage message)
    {
        bool everListed;
        lock (listedChatIds) everListed = listedChatIds.Contains(fromChatId);
        var dropped = GoogleDirectory.IsLeftPlaceholder(fromChatId, everListed, messages.Values);
        var discarded = dropped ? ArrivalHold.Discard(fromChatId) : -1;
        GoogleMessagesLog.Write($"event=message state=moved chat={GoogleMessagesLog.Tag(message.ChatId)} from={GoogleMessagesLog.Tag(fromChatId)} message={GoogleMessagesLog.Tag(message.Id)} placeholder={(dropped ? "dropped" : "kept")}{(discarded >= 0 ? $" held_discarded={discarded}" : "")}");
        if (!dropped) { ReleaseIfSettled(fromChatId); return; }
        placeholders[fromChatId] = 0;
        HoldHelperSends(fromChatId, true);
        if (RemoveChat(fromChatId, out var gone)) ArchiveChats([gone with { Hidden = true }]);
        else if (archive.Chat(fromChatId) is { Hidden: false } saved) ArchiveChats([saved with { Hidden = true }]);
        if (discarded < 0) Publish(new(RealtimeKind.ChatDeleted, GoogleModels.Qualify(fromChatId)));
    }
}
