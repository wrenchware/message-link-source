using System.Globalization;
using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

/// <summary>How a scheduled message is drawn in its conversation: an outgoing row after everything else, marked with its time.</summary>
/// <remarks>
/// Its row has the scheduled message's own id, which is also the id the outbox gives it when it starts sending, so the
/// row on screen stays the same row from scheduled to sent. Scheduled rows are not messages yet: they never take the
/// receipt, the lock or Up's edit from the newest real message, and an export leaves them out.
/// </remarks>
public static class ScheduledSendRows
{
    public const string Scheduled = "LOCAL_SCHEDULED", NotSentPrefix = "LOCAL_NOT_SENT_";
    /// <summary>A message a client has asked its server to keep and has no answer for yet (stage 6 slice 11).</summary>
    public const string Scheduling = "LOCAL_SCHEDULING";

    public static bool Is(ChatMessage message) => Is(message.Delivery.SendState);
    public static bool Is(string? sendState) =>
        sendState is not null && (sendState == Scheduled || sendState == Scheduling || sendState.StartsWith(NotSentPrefix, StringComparison.Ordinal));
    public static bool IsNotSent(string? sendState) => sendState?.StartsWith(NotSentPrefix, StringComparison.Ordinal) == true;

    public static string SendState(ScheduledSend item) => item.State switch
    {
        ScheduledSendState.NotSent => NotSentPrefix + item.Reason.ToString().ToUpperInvariant(),
        // Between being marked as sending and the outbox taking it, which is one save. On a server's schedule it is the whole time the message is on its way.
        ScheduledSendState.Sending => "LOCAL_SENDING",
        ScheduledSendState.Waiting when item.Reason == ScheduledSendReason.Scheduling => Scheduling,
        _ => Scheduled,
    };

    public static ChatMessage Row(ScheduledSend item, string? bubbleFamily = null) =>
        new(item.Id, item.ChatId, "local:self", "You", item.Due, item.Id, item.Text, "text", true, false, false, null, item.ReplyToId,
            [], [], new(SendState(item), null, null, null, new Dictionary<string, bool?>()), bubbleFamily);

    /// <summary>The words beside the row's clock or alert: "Scheduled for 8:00 PM" or "Not sent — Messages was closed at 8:00 PM".</summary>
    public static string Describe(string? sendState, DateTimeOffset due, DateTimeOffset now)
    {
        var at = When(due, now);
        if (sendState == Scheduling) return "Scheduling…";
        if (sendState == Scheduled) return "Scheduled for " + at;
        if (!IsNotSent(sendState)) return "";
        return sendState![NotSentPrefix.Length..] switch
        {
            "CLOSED" => "Not sent — Messages was closed at " + at,
            "OFFLINE" => "Not sent — the service wasn't connected at " + at,
            "REFUSED" => "Not sent — the service couldn't take it at " + at,
            "LATE" => "Not sent — Messages couldn't send it at " + at,
            "ACCOUNT" => "Not sent — a different account is signed in now",
            "MOVED" => "Not sent — scheduled for " + at + " on this PC, before Messages moved to its server",
            "UNCONFIRMED" => "Not sent — the server never confirmed it was scheduled for " + at,
            // ScheduledSendReason.None: held by a restore before its time (ServerRestore.HoldScheduled). The only way to not sent without a reason.
            "NONE" => "Not sent — held after a restore. It was scheduled for " + at,
            _ => "",
        };
    }

    // "8:00 PM" today, "tomorrow 8:00 AM", otherwise the day: "Tue 29 Sep 8:00 AM" in the reader's own format.
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        var days = (local.Date - today).Days;
        return days switch
        {
            0 => time,
            1 => "tomorrow " + time,
            -1 => "yesterday " + time,
            _ => local.ToString("ddd d MMM", CultureInfo.CurrentCulture) + " " + time,
        };
    }
}
