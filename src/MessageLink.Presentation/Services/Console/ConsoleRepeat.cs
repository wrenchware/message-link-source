using System.Globalization;
using System.Text.RegularExpressions;
using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

/// <summary>
/// What the words after /repeat ask for: the list (nothing, or "list"), cancel N (a number from that list), or a new repeat: the days, a time and
/// the prompt, as in "weekdays 7:00 Morning brief". Pure: no lookups, no state. The time is a wall-clock time in the server's own time zone,
/// which is where repeats fire (RepeatRule).
/// </summary>
public abstract partial record RepeatRequest
{
    public sealed record List : RepeatRequest;
    public sealed record Cancel(int Number) : RepeatRequest;
    public sealed record Create(int Days, int Minute, string Text) : RepeatRequest;
    public sealed record Invalid(string Message) : RepeatRequest;

    /// <summary>The server's own limits (HostRepeats): a prompt up to this long, and this many repeats.</summary>
    public const int MaxText = 2000, MaxRepeats = 20;

    public const string Usage = "Use /repeat weekdays 7:00 Morning brief: the days (daily, weekdays, weekends or mon,wed,fri), a time, then what to ask.";

    public static RepeatRequest Parse(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0 || value.Equals("list", StringComparison.OrdinalIgnoreCase)) return new List();
        var (first, after) = Split(value);
        if (first.Equals("cancel", StringComparison.OrdinalIgnoreCase))
            return after.Length is > 0 and <= 2 && after.All(char.IsAsciiDigit) && int.Parse(after, CultureInfo.InvariantCulture) is var n and > 0
                ? new Cancel(n) : new Invalid("Say which repeat: /repeat cancel 2. /repeat lists them.");
        int? days;
        if (first.Equals("every", StringComparison.OrdinalIgnoreCase) && Split(after) is { First: var day } next && day.Equals("day", StringComparison.OrdinalIgnoreCase))
        {
            days = RepeatRule.Everyday;
            after = next.After;
        }
        else days = DaysOf(first);
        if (days is null) return new Invalid(Usage);
        var (time, prompt) = Split(after);
        if (time.Length == 0) return new Invalid(Usage);
        // "7 am" as two words is the same time as "7am".
        if (Split(prompt) is { First: var half } rest && half.ToLowerInvariant() is "am" or "pm" && time.All(char.IsAsciiDigit))
        {
            time += half;
            prompt = rest.After;
        }
        if (MinuteOf(time) is not { } minute) return new Invalid($"“{time}” isn't a time. Use 7:00 or 19:30, or 7am or 7:30pm.");
        if (prompt.Length == 0) return new Invalid("Type what to ask after the time: /repeat weekdays 7:00 Morning brief.");
        if (prompt.Length > MaxText) return new Invalid($"That's longer than a repeat can ask ({MaxText.ToString("N0", CultureInfo.CurrentCulture)} characters).");
        return new Create(days.Value, minute, prompt);
    }

    /// <summary>The days a word names: every day (everyday, daily), weekdays, weekends, or days with commas between, such as mon,wed,fri. Null for
    /// anything else.</summary>
    public static int? DaysOf(string word)
    {
        switch (word.Trim().ToLowerInvariant())
        {
            case "everyday" or "daily": return RepeatRule.Everyday;
            case "weekdays": return RepeatRule.Weekdays;
            case "weekends": return RepeatRule.Weekends;
        }
        var mask = 0;
        foreach (var part in word.Split(',', StringSplitOptions.TrimEntries))
        {
            if (Day(part) is not { } day) return null;
            mask |= 1 << (int)day;
        }
        return mask == 0 ? null : mask;
    }

    private static DayOfWeek? Day(string word) => word.ToLowerInvariant() switch
    {
        "sun" or "sunday" => DayOfWeek.Sunday,
        "mon" or "monday" => DayOfWeek.Monday,
        "tue" or "tues" or "tuesday" => DayOfWeek.Tuesday,
        "wed" or "wednesday" => DayOfWeek.Wednesday,
        "thu" or "thur" or "thurs" or "thursday" => DayOfWeek.Thursday,
        "fri" or "friday" => DayOfWeek.Friday,
        "sat" or "saturday" => DayOfWeek.Saturday,
        _ => null,
    };

    [GeneratedRegex(@"^(\d{1,2})(?::(\d{2}))?(am|pm)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClockTime();

    /// <summary>Minutes after midnight for 7:00, 19:30, 7am or 7:30pm. A bare number is not a time: "7" could be anything.</summary>
    public static int? MinuteOf(string word)
    {
        if (ClockTime().Match(word.Trim()) is not { Success: true } clock) return null;
        if (!clock.Groups[2].Success && !clock.Groups[3].Success) return null;
        var hour = int.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = clock.Groups[2].Success ? int.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        if (clock.Groups[3].Success)
        {
            if (hour is < 1 or > 12) return null;
            hour = hour % 12 + (clock.Groups[3].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
        }
        return hour > 23 || minute > 59 ? null : hour * 60 + minute;
    }

    /// <summary>A minute of the day as the culture writes a short time: "7:00 AM" or "07:00".</summary>
    public static string TimeText(int minute) => DateTime.MinValue.AddMinutes(minute).ToString("t", CultureInfo.CurrentCulture);

    /// <summary>"Weekdays at 7:00 AM".</summary>
    public static string When(int days, int minute) => new RepeatRule(days, minute).DaysText() + " at " + TimeText(minute);

    private static (string First, string After) Split(string text)
    {
        var value = text.Trim();
        var end = 0;
        while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
        return (value[..end], value[end..].Trim());
    }
}
