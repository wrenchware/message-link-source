using MessageLink.Core.Backend;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services.GoogleMessages;

// Avatars persist in the encrypted media cache: photos for 30 days and "no photo" for 3 days, so a
// restart shows them without asking the phone. A background check refreshes each chat at most once a
// day, and failures are remembered for 30 minutes (AvatarCachePolicy). Disk entries per chat:
// avatar-picture: (photo), avatar-absent: (no photo), avatar-checked: (last successful check), avatar-failed:. A photo
// was avatar: until 0.1.284 and avatar-photo: until 0.1.286, which kept Google's letter tiles as photos; each is fetched
// once more so the helper can leave the tiles out (letter_tile.go). "No photo" was
// avatar-none: until 0.1.282, whose answers came from a helper that never asked for a contact's own picture. A "no photo"
// is believed only when it carries the current NoPhotoMarker, so answers from a helper that looked in fewer places (0.1.282
// skipped direct conversations that list their person twice) are asked again instead of shown as initials for days.
// An empty picture means the phone said there is none. Anything not known throws, so a picture kept elsewhere
// (ProviderAvatarReader) is dropped only on a real answer. A picture asked for before Google connected throws the waiting
// failure itself, and is neither a failure nor an answer.
internal sealed partial class GoogleMediaBackend
{
    private static readonly TimeSpan AvatarFailureBackoff = TimeSpan.FromMinutes(2),
        AvatarRefreshDelay = TimeSpan.FromSeconds(20);
    private static readonly byte[] AvatarMarker = [1];
    internal const string NoPhotoKey = "avatar-absent:", PhotoKey = "avatar-picture:";
    private static readonly byte[] NoPhotoMarker = [2];
    // The helper's account of how it looked for each picture: counts, lengths and field numbers only. The first of a run
    // are enough to say which request found the photos, and a list scrolled for an hour does not fill the log with them.
    private const int ProbeLimit = 80;
    private int probesWritten;
    private static readonly System.Text.RegularExpressions.Regex ProbeText = new("^[a-z0-9_=.,: ]{1,1500}$");
    private readonly SemaphoreSlim avatarGate = new(1);
    // Null bytes: a recent failure, remembered so the phone is not asked again at once. It is not an answer.
    private readonly Dictionary<string, (DateTimeOffset Until, byte[]? Bytes)> avatars = [];
    private readonly Dictionary<string, DateTimeOffset> avatarBackoff = [];
    private readonly HashSet<string> avatarRefreshing = [];
    private DateTimeOffset avatarPauseUntil;
    private int avatarFailureStreak;

    private bool TryMemoryAvatar(string key, out byte[]? bytes)
    {
        lock (avatars)
        {
            if (avatars.TryGetValue(key, out var cached) && cached.Until > DateTimeOffset.UtcNow) { bytes = cached.Bytes; return true; }
            bytes = null;
            return false;
        }
    }

    private void RememberAvatar(string key, byte[]? bytes, TimeSpan lifetime)
    {
        lock (avatars)
        {
            if (avatars.Count >= 256 || avatars.Values.Sum(v => (long)(v.Bytes?.Length ?? 0)) + (bytes?.Length ?? 0) > 16 * 1024 * 1024) avatars.Clear();
            avatars[key] = (DateTimeOffset.UtcNow + lifetime, bytes);
        }
    }

    private static BackendException AvatarUnknown() => new(BackendFailure.Unavailable, "The phone has not said whether there is a picture.");

    // After a reset of this PC's Google data: every avatar remembered in memory was filed under a conversation number.
    internal async Task ForgetAvatarsAsync()
    {
        lock (avatars) avatars.Clear();
        await avatarGate.WaitAsync();
        try { avatarBackoff.Clear(); avatarPauseUntil = default; avatarFailureStreak = 0; }
        finally { avatarGate.Release(); }
    }

    private async Task<byte[]> ReadAvatarAsync(string nativeChat, CancellationToken token)
    {
        var profile = await NotificationIdentity.ProfileAsync(tokens, token);
        if (profile is null || string.IsNullOrWhiteSpace(nativeChat)) throw AvatarUnknown();
        var key = profile + "\n" + nativeChat;
        if (TryMemoryAvatar(key, out var remembered)) return remembered ?? throw AvatarUnknown();
        // Disk is consulted once per chat per session; the outcome is then remembered in memory. Each read opens the cache
        // under its single gate, so the failure marker is read only when Decide would look at it: after a check within the
        // day, a saved photo or "no photo" is shown whatever it says.
        var photo = await ReadAvatarDiskAsync(profile, PhotoKey + nativeChat, token);
        var noPhoto = photo is null && await ReadAvatarDiskAsync(profile, NoPhotoKey + nativeChat, token) is [2];
        var checkedRecently = await ReadAvatarDiskAsync(profile, "avatar-checked:" + nativeChat, token) is not null;
        var failedRecently = AvatarCachePolicy.FailureMatters(photo is not null, noPhoto, checkedRecently) &&
            await ReadAvatarDiskAsync(profile, "avatar-failed:" + nativeChat, token) is not null;
        await VerifyProfileAsync(profile, token);
        var (action, refresh) = AvatarCachePolicy.Decide(photo is not null, noPhoto, checkedRecently, failedRecently);
        switch (action)
        {
            case AvatarCacheAction.UsePhoto:
                Interlocked.Increment(ref avatarDiskHits);
                RememberAvatar(key, photo!, TimeSpan.FromHours(12));
                if (refresh)
                {
                    // Photos saved under the old 1-day lifetime are renewed while they are shown.
                    await SaveCacheAsync(profile, PhotoKey + nativeChat, photo!, AvatarCachePolicy.PhotoLifetime, token);
                    QueueAvatarRefresh(profile, nativeChat, hasPhoto: true);
                }
                return photo!;
            case AvatarCacheAction.UseNoPhoto:
                Interlocked.Increment(ref avatarDiskHits);
                RememberAvatar(key, [], TimeSpan.FromHours(12));
                if (refresh) QueueAvatarRefresh(profile, nativeChat, hasPhoto: false);
                return [];
            case AvatarCacheAction.WaitAfterFailure:
                Interlocked.Increment(ref avatarSkipped);
                RememberAvatar(key, null, TimeSpan.FromMinutes(10));
                throw AvatarUnknown();
            default:
                return await FetchAvatarAsync(profile, nativeChat, hasPhoto: false, background: false, token) ?? throw AvatarUnknown();
        }
    }

    private Task<byte[]?> ReadAvatarDiskAsync(string profile, string id, CancellationToken token)
    {
        Interlocked.Increment(ref avatarDiskReads);
        return disk.ReadAsync(profile, id, token);
    }

    private void QueueAvatarRefresh(string profile, string nativeChat, bool hasPhoto)
    {
        var key = profile + "\n" + nativeChat;
        lock (avatarRefreshing) if (!avatarRefreshing.Add(key)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AvatarRefreshDelay); // Startup work (chat list, visible images) goes first.
                await FetchAvatarAsync(profile, nativeChat, hasPhoto, background: true, CancellationToken.None);
            }
            catch { /* The saved avatar stays; a later check tries again. */ }
            finally { lock (avatarRefreshing) avatarRefreshing.Remove(key); }
        });
    }

    // Returns null when skipped or failed. A background check of a saved photo also returns null when
    // Google reports no photo, because the saved photo is kept rather than hidden on a possibly brief gap.
    private async Task<byte[]?> FetchAvatarAsync(string profile, string nativeChat, bool hasPhoto, bool background, CancellationToken token)
    {
        var key = profile + "\n" + nativeChat;
        await avatarGate.WaitAsync(token);
        try
        {
            if (!background && TryMemoryAvatar(key, out var remembered)) return remembered;
            // A failure is not proof there is no photo. Back off per chat and pause after repeated failures.
            var now = DateTimeOffset.UtcNow;
            if (now < avatarPauseUntil || avatarBackoff.TryGetValue(key, out var until) && now < until) { avatarSkipped++; return null; }
            byte[]? bytes;
            try
            {
                // Scrolling cancellation affects only this avatar request.
                var response = await backend.AvatarCallAsync(nativeChat, token);
                bytes = response.GetProperty("data").GetBytesFromBase64();
                if (response.TryGetProperty("probe", out var probe) && probe.ValueKind == System.Text.Json.JsonValueKind.String &&
                    Interlocked.Increment(ref probesWritten) <= ProbeLimit)
                    GoogleMessagesLog.Write("avatar probe " + (ProbeText.IsMatch(probe.GetString()!) ? probe.GetString() : "unreadable"));
                if (bytes.Length > 2 * 1024 * 1024) bytes = [];
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            // Not connected yet, or a sign-in being renewed: the phone was never asked, so nothing is remembered, marked or
            // counted against the pause, and the caller hears it is waiting rather than that the picture is unknown. It
            // asks again when Google connects (ChatAvatar). Counted as a failure, a launch's early asks kept initials for
            // ten minutes and, three at once, paused every avatar for two.
            catch (BackendException ex) when (ex.Waits) { Interlocked.Increment(ref avatarNotReady); throw; }
            catch (BackendException ex) { bytes = null; lastAvatarFailure = ex.Failure.ToString(); }
            catch (Exception ex) { bytes = null; lastAvatarFailure = ex.GetType().Name; }
            if (profile != await NotificationIdentity.ProfileAsync(tokens, token)) return null;
            if (bytes is null)
            {
                avatarFailures++;
                foreach (var expired in avatarBackoff.Where(entry => entry.Value <= DateTimeOffset.UtcNow).Select(entry => entry.Key).ToArray()) avatarBackoff.Remove(expired);
                avatarBackoff[key] = DateTimeOffset.UtcNow + AvatarFailureBackoff;
                if (++avatarFailureStreak >= 3) { avatarPauseUntil = DateTimeOffset.UtcNow + AvatarFailureBackoff; avatarFailureStreak = 0; }
                if (!background) RememberAvatar(key, null, TimeSpan.FromMinutes(10));
                await SaveCacheAsync(profile, "avatar-failed:" + nativeChat, AvatarMarker, AvatarCachePolicy.RetryAfterFailure, token);
                return null;
            }
            avatarFailureStreak = 0;
            avatarBackoff.Remove(key);
            if (bytes.Length > 0)
            {
                avatarPhotos++;
                RememberAvatar(key, bytes, TimeSpan.FromHours(12));
                await SaveCacheAsync(profile, PhotoKey + nativeChat, bytes, AvatarCachePolicy.PhotoLifetime, token);
                await SaveCacheAsync(profile, NoPhotoKey + nativeChat, AvatarMarker, TimeSpan.FromMilliseconds(1), token); // Clears an old "no photo".
            }
            else
            {
                avatarEmpty++;
                if (!hasPhoto)
                {
                    RememberAvatar(key, [], TimeSpan.FromHours(12));
                    await SaveCacheAsync(profile, NoPhotoKey + nativeChat, NoPhotoMarker, AvatarCachePolicy.NoPhotoLifetime, token);
                }
            }
            await SaveCacheAsync(profile, "avatar-checked:" + nativeChat, AvatarMarker, AvatarCachePolicy.CheckInterval, token);
            return bytes.Length > 0 || !hasPhoto ? bytes : null;
        }
        finally { avatarGate.Release(); }
    }
}
