using System.Globalization;

namespace MessageLink.Presentation.Services;

// "Pause notifications" for a while: one shared state that the tray, Settings and later the console and the palette all
// change. A pause suppresses nothing. It rides the same path as being away from the PC (NotificationPreferences.
// AwayReason), so every notification still lands in Notification Center, only without a banner, and unread repeats stay
// quiet while it lasts. Whether it is paused is worked out from the clock whenever it is asked, so a pause that has run
// out is never reported, whatever happened to the timer meant to announce its end.
public sealed class NotificationPause(TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private DateTimeOffset? until;

    public event EventHandler? Changed;

    // When the pause ends, or null when there is none (or it has already ended).
    public DateTimeOffset? Until
    {
        get { lock (gate) return until is { } end && end > clock.GetUtcNow() ? end : null; }
    }

    public bool IsPaused => Until is not null;

    public void PauseFor(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        Set(clock.GetUtcNow() + duration);
    }

    public void PauseUntilMorning() => Set(MorningAfter(clock.GetUtcNow(), clock.LocalTimeZone));

    public void Resume()
    {
        bool had;
        lock (gate) { had = until is not null; until = null; }
        if (had) Changed?.Invoke(this, EventArgs.Empty);
    }

    // A pause saved by an earlier run. One that has already ended is ignored, and nothing is announced for it.
    public void Restore(DateTimeOffset? saved)
    {
        if (saved is not { } end || end <= clock.GetUtcNow()) return;
        Set(end);
    }

    // What the observer writes beside a silent delivery: "paused until 3:04 PM". Null while not paused.
    public string? Reason() => Until is { } end ? "paused until " + Local(end).ToString("t", CultureInfo.CurrentCulture) : null;

    // The one line Settings and the tray show: "Paused until 3:04 PM" or "Paused until tomorrow, 8:00 AM".
    public string Describe()
    {
        if (Until is not { } end) return "";
        var now = Local(clock.GetUtcNow());
        var local = Local(end);
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        var days = (local.Date - now.Date).Days;
        return days switch
        {
            <= 0 => $"Paused until {time}",
            1 => $"Paused until tomorrow, {time}",
            _ => $"Paused until {local.ToString("ddd", CultureInfo.CurrentCulture)}, {time}"
        };
    }

    // The next moment Describe's words change while paused: the end of the pause, or the coming local midnight when that
    // is sooner, after which "tomorrow, 8:00 AM" has become plain "8:00 AM". Null while not paused.
    public DateTimeOffset? NextChange()
    {
        if (Until is not { } end) return null;
        var zone = clock.LocalTimeZone;
        var midnight = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime.Date.AddDays(1);
        // A zone that moves its clocks at midnight has no 0:00 that day; the words change at the first time it does have.
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(30);
        var next = new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
        return next < end ? next : end;
    }

    // Tomorrow at 8:00 local time, or today at 8:00 when it is still the small hours (before 4:00), when "until the
    // morning" plainly means this one.
    public static DateTimeOffset MorningAfter(DateTimeOffset nowLocal) => MorningAfter(nowLocal, TimeZoneInfo.Local);

    // The wall-clock moment is built first and its offset read for that moment, so a night that crosses a daylight
    // saving change still ends at 8:00 on the clock rather than an hour either side of it.
    public static DateTimeOffset MorningAfter(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var morning = (local.Hour < 4 ? local.Date : local.Date.AddDays(1)).AddHours(8);
        // No zone moves its clocks at 8:00 today; should one ever, an hour that does not exist is skipped forward.
        while (zone.IsInvalidTime(morning)) morning = morning.AddMinutes(30);
        return new DateTimeOffset(morning, zone.GetUtcOffset(morning));
    }

    // The menu wording for that choice: "Until 8:00 AM tomorrow", or "Until 8:00 AM" before 4:00.
    public static string MorningLabel(DateTimeOffset nowLocal) => MorningLabel(nowLocal, TimeZoneInfo.Local);

    public static string MorningLabel(DateTimeOffset now, TimeZoneInfo zone)
    {
        var morning = TimeZoneInfo.ConvertTime(MorningAfter(now, zone), zone);
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var time = morning.ToString("t", CultureInfo.CurrentCulture);
        return morning.Date == today ? $"Until {time}" : $"Until {time} tomorrow";
    }

    // The pause's reason, falling back to the one already in place (being away from the PC).
    public static Func<string?> Compose(NotificationPause pause, Func<string?> away) => () => pause.Reason() ?? away();

    private void Set(DateTimeOffset end)
    {
        lock (gate) until = end;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DateTimeOffset Local(DateTimeOffset moment) => TimeZoneInfo.ConvertTime(moment, clock.LocalTimeZone);
}

// Where a pause lives between runs, and how it is joined to the preferences the notification observer and the repeat
// service read. Kept here rather than in the App's NotificationSettings so the harness runs the very code that makes a
// pause silence anything. The file, beside notifications-enabled.txt, holds the end as a UTC round-trip ("O") time.
public static class NotificationPauseFile
{
    // Joins the pause to the preferences (the pause first, then being away) and brings back a pause saved by an earlier
    // run. True when that pause is still in force; otherwise a file that is past, unreadable or garbled is removed.
    public static bool Attach(NotificationPreferences preferences, NotificationPause pause, string path)
    {
        preferences.AwayReason = NotificationPause.Compose(pause, preferences.AwayReason);
        preferences.PausedUntil = () => pause.Until;
        pause.Restore(Read(path));
        if (pause.IsPaused) return true;
        Delete(path);
        return false;
    }

    public static DateTimeOffset? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return DateTimeOffset.TryParseExact(File.ReadAllText(path).Trim(), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var until) ? until : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // Writes the pause's end, or removes the file when there is no pause. Null on success, else the error's type name.
    public static string? Save(NotificationPause pause, string path)
    {
        if (pause.Until is not { } until) { Delete(path); return null; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, until.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.GetType().Name; }
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
