namespace MessageLink.Core.Messaging;

/// <summary>
/// When a repeating scheduled send fires: on the chosen weekdays at one wall-clock time in the server's own time zone, so
/// "7:00" stays 7:00 across daylight saving. Days is a mask with bit 0 for Sunday through bit 6 for Saturday
/// (<see cref="DayOfWeek"/>); Minute is minutes after local midnight. Only Hermes threads take repeats (the owner's
/// decision, 2026-11-14): Hermes cannot deliver its own cron jobs into an API session, so the server asks for it.
/// </summary>
public sealed record RepeatRule(int Days, int Minute)
{
    public const int Everyday = 0b111_1111, Weekdays = 0b011_1110, Weekends = 0b100_0001;
    /// <summary>How late a missed time may still be sent, after the server was down or asleep. Later than this, it is skipped and the
    /// next time waits: a morning brief at noon is noise, and a backlog of them worse.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);

    public bool Valid => Days is > 0 and <= Everyday && Minute is >= 0 and < 24 * 60;
    public bool On(DayOfWeek day) => (Days & (1 << (int)day)) != 0;

    /// <summary>The first time strictly after <paramref name="after"/> in <paramref name="zone"/>. A time the clocks skip (spring
    /// forward) fires when they land; a time that happens twice (fall back) fires once, at the first.</summary>
    public DateTimeOffset Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        if (!Valid) throw new InvalidOperationException("Invalid repeat.");
        var start = TimeZoneInfo.ConvertTime(after, zone).Date;
        for (var day = -1; day <= 8; day++)
        {
            var date = start.AddDays(day);
            if (!On(date.DayOfWeek)) continue;
            var at = Instant(DateTime.SpecifyKind(date.AddMinutes(Minute), DateTimeKind.Unspecified), zone);
            if (at > after) return at;
        }
        throw new InvalidOperationException("No repeat time found.");
    }

    /// <summary>
    /// What a repeat due at <paramref name="due"/> does when its timer runs at <paramref name="now"/>: send it (on time or a little
    /// late) or skip it, and when it is next due. One missed time at most is ever sent, never a backlog.
    /// </summary>
    public (bool Send, DateTimeOffset Next) Settle(DateTimeOffset due, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (now < due) return (false, due);
        // Only the latest time that has passed counts: after days down, this morning's may still be a few minutes late, the days before never are.
        var latest = Latest(now, zone) is { } last && last >= due ? last : due;
        return (now - latest <= Grace, Next(now, zone));
    }

    /// <summary>The last time at or before <paramref name="at"/>, or null within the past week when there is none.</summary>
    public DateTimeOffset? Latest(DateTimeOffset at, TimeZoneInfo zone)
    {
        var start = TimeZoneInfo.ConvertTime(at, zone).Date;
        for (var day = 1; day >= -8; day--)
        {
            var date = start.AddDays(day);
            if (!On(date.DayOfWeek)) continue;
            var time = Instant(DateTime.SpecifyKind(date.AddMinutes(Minute), DateTimeKind.Unspecified), zone);
            if (time <= at) return time;
        }
        return null;
    }

    private static DateTimeOffset Instant(DateTime local, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(local))
        {
            // Spring forward: the wall clock jumps over this time; fire at the first minute that exists after it.
            var probe = local;
            for (var i = 0; i < 24 * 60 && zone.IsInvalidTime(probe); i++) probe = probe.AddMinutes(1);
            local = probe;
        }
        if (zone.IsAmbiguousTime(local))
        {
            // Fall back: the earlier of the two is the larger offset.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(local, offset);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>The days as people say them: "Every day", "Weekdays", "Weekends", or the short names in week order.</summary>
    public string DaysText() => Days switch
    {
        Everyday => "Every day",
        Weekdays => "Weekdays",
        Weekends => "Weekends",
        _ => string.Join(", ", Enumerable.Range(0, 7).Select(i => (DayOfWeek)((i + 1) % 7)).Where(On)
            .Select(day => System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day))),
    };
}

/// <summary>
/// A repeating scheduled send, as the server keeps it (one <c>repeat:</c> row each, written by the host alone). NextDue is the next time it fires;
/// LastAt and LastOutcome (<c>sent</c>, <c>skipped</c>, <c>failed</c>) say what the last time did.
/// </summary>
public sealed record RepeatingSend(string Id, string ChatId, string Text, int Days, int Minute, DateTimeOffset CreatedAt, DateTimeOffset NextDue,
    DateTimeOffset? LastAt = null, string? LastOutcome = null)
{
    public RepeatRule Rule => new(Days, Minute);
}
