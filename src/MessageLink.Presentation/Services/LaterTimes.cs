using System.Globalization;

namespace MessageLink.Presentation.Services;

public sealed record LaterTime(string Label, DateTimeOffset At);

/// <summary>The quick choices Send later and Remind me offer, worked out from the clock and the local time zone.</summary>
/// <remarks>
/// In an hour; this evening at 8 while there is still an hour before it; and 8 in the morning — later this morning in the
/// small hours, otherwise tomorrow. Anything else is picked with a date and a time.
/// </remarks>
public static class LaterTimes
{
    public static readonly TimeSpan Evening = TimeSpan.FromHours(20), Morning = TimeSpan.FromHours(8);

    public static IReadOnlyList<LaterTime> Presets(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var list = new List<LaterTime> { new("In 1 hour", Minute(now.AddHours(1))) };
        if (local.TimeOfDay < Evening - TimeSpan.FromHours(1))
            list.Add(new("This evening, " + Clock(Evening), At(local.Date + Evening, zone)));
        if (local.TimeOfDay < Morning - TimeSpan.FromHours(1))
            list.Add(new("This morning, " + Clock(Morning), At(local.Date + Morning, zone)));
        else list.Add(new("Tomorrow morning, " + Clock(Morning), At(local.Date.AddDays(1) + Morning, zone)));
        return list;
    }

    /// <summary>A date and a time picked in the local zone, as a moment. Null when it is not in the future.</summary>
    public static DateTimeOffset? Picked(DateTime date, TimeSpan time, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var at = At(date.Date + time, zone ?? TimeZoneInfo.Local);
        return at > now ? at : null;
    }

    // A local wall-clock time as a moment. One skipped by a daylight-saving change is taken an hour later; one that
    // happens twice is taken the first time.
    private static DateTimeOffset At(DateTime wall, TimeZoneInfo zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
        var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Max() : zone.GetUtcOffset(wall);
        return new DateTimeOffset(wall, offset);
    }

    private static DateTimeOffset Minute(DateTimeOffset at) => new(at.Ticks - at.Ticks % TimeSpan.TicksPerMinute, at.Offset);
    private static string Clock(TimeSpan time) => DateTime.Today.Add(time).ToString("t", CultureInfo.CurrentCulture);
}
