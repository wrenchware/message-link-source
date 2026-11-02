using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// A scheduled message held as not sent says so on the PC, as a push does on the phone (PushNotifier.ScheduledHeld): it waited in its conversation, and nobody knew until the moment
/// had gone (audit, 2026-11-01). Each once, whichever device's list shows it. A restore's are told here too, as it holds them without the schedule running. Old ones aren't news.
/// </summary>
public sealed class ScheduledHeldNotices
{
    private static readonly TimeSpan TooOld = TimeSpan.FromDays(1);
    private readonly ScheduledSends sends;
    private readonly NotificationObserver observer;
    private readonly IChatReader chats;
    private readonly TimeProvider clock;
    private readonly HashSet<string> told = [];

    public ScheduledHeldNotices(ScheduledSends sends, NotificationObserver observer, IChatReader chats, TimeProvider? clock = null)
    {
        this.sends = sends; this.observer = observer; this.chats = chats; this.clock = clock ?? TimeProvider.System;
        sends.Changed += (_, _) => _ = TellAsync();
    }

    // Moved and Unconfirmed have words of their own in the conversation and came from something the person did here.
    public static bool Tellable(ScheduledSend item, DateTimeOffset now) =>
        item is { State: ScheduledSendState.NotSent, Reason: ScheduledSendReason.None or ScheduledSendReason.Closed or ScheduledSendReason.Offline or ScheduledSendReason.Refused or ScheduledSendReason.Late or ScheduledSendReason.Account }
        && now - item.Due < TooOld;

    private async Task TellAsync()
    {
        try
        {
            var now = clock.GetUtcNow();
            foreach (var item in sends.All.Where(item => Tellable(item, now) && !told.Contains(item.Id)).ToArray())
            {
                var chat = await chats.GetChatAsync(item.ChatId);
                if (await observer.ScheduledHeldAsync(chat, item.Id)) told.Add(item.Id);
            }
        }
        catch (Exception) { /* A conversation that can't be read yet: the next change of the schedule asks again. */ }
    }
}
