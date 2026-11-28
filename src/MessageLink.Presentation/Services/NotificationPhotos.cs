using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

// The picture this PC already keeps for a conversation (or, in a group, for the person who wrote), as a small PNG. Only
// what is already here: a photo chosen on this PC, or a provider's picture kept from an earlier look. It never asks a
// service for one, and never asks a service whether a kept one is still current.
public interface INotificationPhotoSource
{
    Task<byte[]?> ReadAsync(ChatSummary chat, string? senderId, CancellationToken token);
}

/// <summary>"Show the sender's photo": the picture a notification shows as its round logo, as a file for as long as it lives.</summary>
/// <remarks>
/// <para>Windows takes that picture only as a file, so a face that is otherwise kept encrypted sits on disk, decrypted, while
/// its notification can still be seen. That is why the option is off unless turned on, and why the file is kept as briefly
/// as the notification allows: it goes when the notification is clicked or acted on (<see cref="Release"/>), when a sweep
/// finds Windows no longer has the notification (dismissed or expired), after <see cref="Lifetime"/> whatever happened,
/// and all of them go when the option is turned off, details are hidden, or an account signs out (<see cref="Clear"/>).</para>
/// <para>One folder under the app's own data, whose permissions it inherits: this Windows user (with SYSTEM and
/// administrators), as everything else there. A file is named by a one-way tag of the notification's record id, so the
/// folder names no conversation or person, and it holds a small PNG and nothing else.</para>
/// </remarks>
public sealed class NotificationPhotos(string directory, INotificationPhotoSource source, TimeProvider? clock = null, Action<string>? log = null)
{
    /// <summary>A notification is kept by Windows for a day (its expiration), and so is its picture at most.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);
    // A notification being shown now may not be in Windows' list yet when a sweep reads it.
    private static readonly TimeSpan Settling = TimeSpan.FromMinutes(2);
    private const int Largest = 512 * 1024;
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public string Directory => directory;

    public static string FileName(string recordId) => NotificationIdentity.Hash("notification-photo\n" + recordId)[..32].ToLowerInvariant() + ".png";

    /// <summary>Writes the picture for the notification <paramref name="recordId"/>; its path, or null when there is none.</summary>
    public async Task<string?> PrepareAsync(string recordId, ChatSummary chat, string? senderId, CancellationToken token = default)
    {
        var bytes = await source.ReadAsync(chat, senderId, token);
        if (bytes is not { Length: > 0 and <= Largest }) return null;
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName(recordId));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, path, true);
            // Its age is counted from this clock, so the day it may live is the same whatever the file system says.
            File.SetLastWriteTimeUtc(path, clock.GetUtcNow().UtcDateTime);
        }
        finally { TryDelete(temporary); }
        return path;
    }

    /// <summary>The notification was clicked, acted on or removed: its picture goes.</summary>
    public void Release(string recordId)
    {
        if (!NotificationIdentity.IsValidId(recordId)) return;
        TryDelete(Path.Combine(directory, FileName(recordId)));
    }

    /// <summary>
    /// Removes every picture older than <see cref="Lifetime"/> and, when <paramref name="liveIds"/> (the record ids of the
    /// notifications Windows still has) is known, every one whose notification is gone. Returns how many went.
    /// </summary>
    public int Sweep(IReadOnlyCollection<string>? liveIds)
    {
        if (!System.IO.Directory.Exists(directory)) return 0;
        var now = clock.GetUtcNow().UtcDateTime;
        var live = liveIds?.Where(NotificationIdentity.IsValidId).Select(FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var path in System.IO.Directory.EnumerateFiles(directory).ToArray())
        {
            try
            {
                var age = now - File.GetLastWriteTimeUtc(path);
                var stray = !path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                var gone = live is not null && !live.Contains(Path.GetFileName(path)) && age > Settling;
                if (!stray && !gone && age <= Lifetime) continue;
                if (stray && age <= Settling) continue;
                File.Delete(path);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* In use; the next sweep tries again. */ }
        }
        if (removed > 0) log?.Invoke($"notification photos swept removed={removed}");
        return removed;
    }

    /// <summary>Every picture, at once: the option was turned off, details hidden, or an account signed out.</summary>
    /// <remarks>
    /// Never throws. It runs after the change that asked for it has already happened — a sign-out that worked, an option
    /// already saved — and a folder that cannot be read must not make that change look as if it failed. What is left is
    /// taken by the hourly sweep, or the next clear.
    /// </remarks>
    public int Clear(string reason)
    {
        var removed = 0;
        try
        {
            if (!System.IO.Directory.Exists(directory)) return 0;
            foreach (var path in System.IO.Directory.EnumerateFiles(directory).ToArray())
                if (TryDelete(path)) removed++;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            log?.Invoke($"notification photos clear failed reason={reason} error={e.GetType().Name}");
        }
        if (removed > 0) log?.Invoke($"notification photos cleared removed={removed} reason={reason}");
        return removed;
    }

    /// <summary>A sweep now and then every hour, while the app runs.</summary>
    public async Task RunAsync(Func<Task<IReadOnlyCollection<string>?>> liveIds, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try { Sweep(await liveIds()); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception e) { log?.Invoke($"notification photos sweep failed error={e.GetType().Name}"); }
            try { if (!await timer.WaitForNextTickAsync(token)) return; }
            catch (OperationCanceledException) { return; }
        }
        while (!token.IsCancellationRequested);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
