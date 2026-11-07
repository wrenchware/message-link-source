using System.Text.Json;

namespace NativeBeeper.App.Services.Backup;

/// <summary>
/// Whether backups run, where they go, and how the last one went. LastFailure is a short code (see
/// <see cref="BackupSettings.Describe"/>); it is cleared by the next success. The password is kept apart, sealed.
/// </summary>
/// <remarks>
/// Install is the tag this PC's backup names carry (<see cref="BackupFolder"/>). MessengerLeftOut says why the last
/// backup holds no Messenger history although Messenger is used here: not_connected or failed. MessengerKept names the
/// newest backup holding each Messenger account's history, by the helper's one-way account key; pruning never removes
/// those, so days of backups made without that history cannot push out the last one that has it.
/// LastName is the file the last backup was written as, which a check reads back (<see cref="BackupCheck"/>);
/// LastVerifiedAt and LastVerifyResult say when that last happened and what it found. PasswordSetAt is when the
/// password was last set: a backup older than it is sealed under the one before.
/// </remarks>
internal sealed record BackupSettings(bool Enabled = false, string? Folder = null, DateTimeOffset? LastSuccess = null,
    long LastBytes = 0, DateTimeOffset? LastFailureAt = null, string? LastFailure = null, string? Install = null,
    string? MessengerLeftOut = null, Dictionary<string, string>? MessengerKept = null, string? LastName = null,
    DateTimeOffset? LastVerifiedAt = null, string? LastVerifyResult = null, DateTimeOffset? PasswordSetAt = null, string? DestinationId = null)
{
    // LastName may be a usable partial archive. Keep the last complete file separately so partial retries cannot evict it.
    public string? LastCompleteName { get; init; }
    // A usable partial still consumes the day's archive slot; success and stale warnings remain separate.
    public DateTimeOffset? LastWrittenAt { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal DateTimeOffset? LatestWrite => LastWrittenAt ?? LastSuccess;
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? CompleteArchive => LastCompleteName ?? (LastSuccess is not null && MessengerLeftOut is not ("not_connected" or "failed") ? LastName : null);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    // A failure is tried again sooner than a day, but not so soon that a folder that is gone fills the log.
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);
    // The latest backup is first read back a day after it is written, then weekly: backups go daily into a folder the
    // app never reads again, so one that no longer decrypts would otherwise be found only during a restore.
    public static readonly TimeSpan FirstVerify = TimeSpan.FromDays(1), VerifyInterval = TimeSpan.FromDays(7);
    // A check that found the folder busy, or could not read the saved password, records nothing, so it stays due; it is
    // tried again after this long (the schedule's poll) rather than every minute.
    public static readonly TimeSpan VerifyRetry = TimeSpan.FromMinutes(10);

    /// <summary>When the next scheduled backup is due, or null when none will run: off, no password, or no folder.
    /// Never backed up means due now.</summary>
    public DateTimeOffset? NextDue(DateTimeOffset now, bool hasPassword)
    {
        if (!Enabled || !hasPassword || string.IsNullOrWhiteSpace(Folder)) return null;
        // A clock that has moved back must not put the next backup a year away.
        var due = LatestWrite is { } done && done <= now + Interval ? done + Interval : DateTimeOffset.MinValue;
        if (LastFailureAt is { } failed && (LastSuccess is null || failed > LastSuccess) && failed <= now + Interval &&
            failed + RetryAfterFailure > due) due = failed + RetryAfterFailure;
        // The latest backup did not read back: a fresh copy follows within the retry interval rather than tomorrow, until
        // one is written. An attempt that fails meanwhile is tried again the same interval after that failure, never at once.
        if (UnreadableIsLatest && LastVerifiedAt is { } checkedAt && checkedAt <= now + Interval)
        {
            var after = LastFailureAt is { } failedSince && failedSince > checkedAt && failedSince <= now + Interval ? failedSince : checkedAt;
            if (after + RetryAfterFailure < due) due = after + RetryAfterFailure;
        }
        return due;
    }

    /// <summary>When the latest backup is next read back, or null when there is nothing to check: off, or never backed
    /// up. First a day after a backup, then a week after the last check; the backup written to replace one that did not
    /// read back is itself read back a day after it is written.</summary>
    public DateTimeOffset? NextVerifyDue(DateTimeOffset now)
    {
        if (!Enabled || LatestWrite is not { } done) return null;
        if (LastVerifyResult == "unavailable" && LastVerifiedAt is { } unavailable) return unavailable + VerifyRetry;
        // As NextDue: a clock that has moved back must not put the next check a year away.
        if (LastVerifiedAt is { } verified && !(LastVerifyResult == "unreadable" && done > verified))
            return verified <= now + VerifyInterval ? verified + VerifyInterval : DateTimeOffset.MinValue;
        return done <= now + FirstVerify ? done + FirstVerify : DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Whether the schedule reads the latest backup back at <paramref name="now"/>. It does so before that round's
    /// backup: the check a day after a backup falls due when the next backup does, and the backup would otherwise
    /// replace the file to check and put the check a day away again, every day. Checked first, the file read back is
    /// the one that has sat in the folder since it was written. <paramref name="notBefore"/> holds off a check that
    /// last found the folder busy or the password unreadable until the retry interval has passed.
    /// </summary>
    public bool VerifyIsDue(DateTimeOffset now, bool hasPassword, DateTimeOffset? notBefore) =>
        VerifyWake(now, hasPassword, notBefore) is { } at && at <= now;

    /// <summary>When the schedule next has something to do: a backup or a check, whichever is sooner; null for neither.</summary>
    public DateTimeOffset? NextScheduled(DateTimeOffset now, bool hasPassword, DateTimeOffset? verifyNotBefore)
    {
        var backup = NextDue(now, hasPassword);
        var verify = VerifyWake(now, hasPassword, verifyNotBefore);
        return backup is null ? verify : verify is null ? backup : backup < verify ? backup : verify;
    }

    // A check needs the password and the folder, and waits while the last backup failed on the password: it would fail
    // the same way at every poll.
    private DateTimeOffset? VerifyWake(DateTimeOffset now, bool hasPassword, DateTimeOffset? notBefore)
    {
        if (!hasPassword || string.IsNullOrWhiteSpace(Folder) || (LastFailed && LastFailure == "password")) return null;
        if (NextVerifyDue(now) is not { } due) return null;
        return notBefore is { } held && held > due && held <= now + VerifyRetry ? held : due;
    }

    public bool LastFailed => LastFailure is not null && (LastSuccess is null || LastFailureAt > LastSuccess);

    /// <summary>Whether the latest backup was read back and could not be, with no newer backup since.</summary>
    public bool UnreadableIsLatest => LastVerifyResult == "unreadable" && LastVerifiedAt is { } checkedAt && (LatestWrite is null || checkedAt > LatestWrite);

    /// <summary>These settings once a check has finished with <paramref name="result"/>. A folder that was busy is not
    /// a finished check: it is tried again at the next poll.</summary>
    public BackupSettings Verified(DateTimeOffset at, string result) => result is "busy" ? this : this with { LastVerifiedAt = at, LastVerifyResult = result };

    /// <summary>The newest backup of each Messenger account's history once <paramref name="name"/> is written.</summary>
    public Dictionary<string, string>? KeptAfter(BackupManifest manifest, string name) =>
        manifest.Messenger is not { } messenger ? MessengerKept
            : new Dictionary<string, string>(MessengerKept ?? [], StringComparer.Ordinal) { [messenger.Account] = name };

    /// <summary>These settings once a backup has been written as <paramref name="name"/>.</summary>
    public BackupSettings Succeeded(DateTimeOffset at, long bytes, string name, BackupManifest manifest) => this with
    {
        LastSuccess = at, LastWrittenAt = at, LastBytes = bytes, LastFailure = null, LastFailureAt = null, LastName = name,
        // Not signed in to Messenger means there is no history to miss; the other two mean some was left behind.
        MessengerLeftOut = manifest.LeftOut is "not_connected" or "failed" ? manifest.LeftOut : null,
        MessengerKept = KeptAfter(manifest, name),
    };

    /// <summary>The one line Settings shows under Backup.</summary>
    public string Describe(DateTimeOffset now, bool running, bool restoring = false)
    {
        if (restoring) return "Restoring…";
        if (running) return "Backing up…";
        if (LastFailed) return "Last backup failed. " + (LastFailure == "messenger" && MessengerLeftOut == "not_connected" ? "Messenger wasn't connected." : Failure(LastFailure));
        if (LastSuccess is { } done)
            return $"Last backup {When(done, now)} · {Size(LastBytes)}" + (MessengerLeftOut is null ? "" : " · without Messenger history") +
                // Only on failure: a check that found the backup fine says nothing here.
                (UnreadableIsLatest ? ". Latest backup couldn't be read back." : "");
        if (!Enabled) return "Off";
        return string.IsNullOrWhiteSpace(Folder) ? "Choose a folder to start." : "On. The first backup runs shortly.";
    }

    /// <summary>A failure code in plain words.</summary>
    public static string Failure(string? code) => code switch
    {
        "folder" => "The backup folder isn't available.",
        "space" => "The backup folder is full.",
        "password" => "The saved password can't be read. Set it again.",
        "messenger" => "Messenger history couldn't be read.",
        "local" => "This PC's saved data couldn't be read.",
        _ => "The backup couldn't be written.",
    };

    public static string When(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        if (local.Date == today) return "today at " + time;
        if (local.Date == today.AddDays(-1)) return "yesterday at " + time;
        // As the chat list writes dates: the year only when it is not this one, so an old backup never looks recent.
        return local.ToString(local.Year == today.Year ? "MMM d" : "MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture) + " at " + time;
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static BackupSettings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<BackupSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, overwrite: true);
    }
}
