namespace MessageLink.Presentation.Services;

// The durations the conversation menu offers, with the moment each one lands on spelled out: "this evening" is
// only obvious until it is evening. Worded as when the reminder comes ("In 1 hour", "At 6:00 PM"), not as how long
// the conversation stays quiet: that reading ("For 1 hour", "Until …") belongs to Mute and Pause, not Remind me.
public static class SnoozeOptions
{
    public static IEnumerable<(string Text, DateTimeOffset Until)> For(DateTimeOffset now)
    {
        yield return ("In 30 minutes", now.AddMinutes(30));
        yield return ("In 1 hour", now.AddHours(1));
        yield return ("In 3 hours", now.AddHours(3));
        // Whichever of this evening or tomorrow morning is still far enough ahead to be worth waiting for.
        var evening = Local(now.LocalDateTime.Date.AddHours(18));
        var moment = evening - now > TimeSpan.FromMinutes(30) ? evening : Local(now.LocalDateTime.Date.AddDays(1).AddHours(8));
        // In the short time Windows is set to, as every other time in the app: a fixed 12-hour pattern wrote "6:00" with no
        // marker in a culture without AM and PM, and a 12-hour clock to someone who chose a 24-hour one.
        var clock = moment.LocalDateTime.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        yield return ($"At {clock}" + (moment.LocalDateTime.Date == now.LocalDateTime.Date ? "" : " tomorrow"), moment);
        // The offset is read for each moment rather than carried over from now, so a choice that crosses a daylight
        // saving change still lands on the clock time it named.
        static DateTimeOffset Local(DateTime moment) => new(moment, TimeZoneInfo.Local.GetUtcOffset(moment));
    }
}
