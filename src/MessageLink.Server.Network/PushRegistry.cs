using System.Text.Json;
using MessageLink.Core.Services;

namespace MessageLink.Server.Network;

/// <summary>
/// Each paired device's push address: Firebase's token for it and the key and secret its pushes are sealed to. Kept in
/// <c>push.json</c> beside <c>devices.json</c>, sealed the same way, and forgotten when the device is removed or Firebase says the token is gone. A token is
/// never logged.
/// </summary>
/// <remarks>
/// Only a device paired now has a row that counts (<c>paired</c>, the device list's own answer): a row can outlive its device (a <c>push.json</c> put back
/// from before a revoke, a <c>devices.json</c> the owner removed, a registration that raced its device's removal, a removal whose write failed), so rows of
/// devices no longer paired are let go at load, a registration from one is refused, and the notifier asks again before each push.
/// </remarks>
internal sealed class PushRegistry
{
    public const string FileName = "push.json";

    public sealed record Registration(string DeviceId, string Token, string Package, byte[] PublicKey, byte[] Auth, DateTimeOffset At, bool BackgroundSync = false);
    // A row with an empty token is a phone's report that the project has no app for its package, kept in the same list.
    private sealed record Stored(string DeviceId, string Token, string Package, string PublicKey, string Auth, DateTimeOffset At, bool BackgroundSync = false);

    /// <summary>A phone's report that the Firebase project has no app for its <paramref name="Package"/>: shown on the page, never a push target.</summary>
    public sealed record MissingApp(string DeviceId, string Package, DateTimeOffset At);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string path;
    private readonly IStateProtector protector;
    private readonly Action<string> log;
    private readonly Func<string, bool> paired;
    private readonly object gate = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private Dictionary<string, Registration> registrations;
    private readonly Dictionary<string, MissingApp> missing;

    private PushRegistry(string path, IStateProtector protector, Action<string> log, Func<string, bool> paired, Dictionary<string, Registration> registrations,
        Dictionary<string, MissingApp> missing)
    {
        this.path = path; this.protector = protector; this.log = log; this.paired = paired; this.registrations = registrations; this.missing = missing;
    }

    /// <summary>
    /// The registrations kept in <paramref name="folder"/> of devices <paramref name="paired"/> says are paired; none when there is no file, or one that cannot
    /// be read (each phone registers again when it connects). Rows of other devices, or with a key no push can be sealed to, are let go, and the file written
    /// without them.
    /// </summary>
    public static async Task<PushRegistry> LoadAsync(string folder, IStateProtector protector, Action<string> log, Func<string, bool> paired)
    {
        var path = Path.Combine(folder, FileName);
        var kept = new Dictionary<string, Registration>(StringComparer.Ordinal);
        var reports = new Dictionary<string, MissingApp>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                var opened = await protector.UnprotectAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false);
                foreach (var stored in JsonSerializer.Deserialize<List<Stored>>(opened, Json) ?? [])
                    if (stored.Token.Length == 0) reports[stored.DeviceId] = new(stored.DeviceId, stored.Package, stored.At);
                    else kept[stored.DeviceId] = new(stored.DeviceId, stored.Token, stored.Package, Convert.FromBase64String(stored.PublicKey), Convert.FromBase64String(stored.Auth), stored.At, stored.BackgroundSync);
            }
            catch (Exception failure) when (failure is IOException or JsonException or FormatException or System.Security.Cryptography.CryptographicException or InvalidDataException)
            {
                log($"push registrations unreadable type={failure.GetType().Name}");
            }
        }
        // And a key no push can be sealed to, kept before registrations were held to one: every push to it would fail.
        var letGo = kept.Values.Where(r => !paired(r.DeviceId) || !WebPushEncryption.Accepts(r.PublicKey)).Select(r => r.DeviceId).ToArray();
        foreach (var id in letGo) kept.Remove(id);
        var reportsGone = reports.Keys.Where(id => !paired(id)).ToArray();
        foreach (var id in reportsGone) reports.Remove(id);
        log($"push registrations count={kept.Count}{(reports.Count > 0 ? $" missing_app={reports.Count}" : "")}");
        var registry = new PushRegistry(path, protector, log, paired, kept, reports);
        if (letGo.Length > 0 || reportsGone.Length > 0)
        {
            log($"push registrations pruned count={letGo.Length + reportsGone.Length}");
            await registry.SaveAsync().ConfigureAwait(false);
        }
        return registry;
    }

    /// <summary>Whether <paramref name="deviceId"/> is paired now: no push goes to any other.</summary>
    public bool Paired(string deviceId) => paired(deviceId);

    /// <summary>Every registration kept; whether its device is still paired is asked as each push goes (<see cref="Paired"/>).</summary>
    public IReadOnlyList<Registration> All()
    {
        lock (gate) return [.. registrations.Values];
    }

    /// <summary>The registration of <paramref name="deviceId"/> as it is now, or null for none.</summary>
    public Registration? Get(string deviceId)
    {
        lock (gate) return registrations.GetValueOrDefault(deviceId);
    }

    /// <summary>Keeps <paramref name="registration"/>, replacing the device's last; false, keeping nothing, for a device that is not paired.</summary>
    public async Task<bool> SetAsync(Registration registration)
    {
        if (!paired(registration.DeviceId))
        {
            log("push registration refused why=device_unpaired");
            return false;
        }
        int moved;
        lock (gate)
        {
            // A phone that joined again without being removed first brings the same token under its new id: the old row would send each message twice.
            var others = registrations.Values.Where(r => r.DeviceId != registration.DeviceId && r.Token == registration.Token).Select(r => r.DeviceId).ToArray();
            foreach (var other in others) registrations.Remove(other);
            moved = others.Length;
            // Signed up: whatever it reported missing before is no longer so.
            var reported = missing.Remove(registration.DeviceId);
            if (!reported && moved == 0 && registrations.TryGetValue(registration.DeviceId, out var was) && was.Token == registration.Token && was.Package == registration.Package &&
                was.PublicKey.AsSpan().SequenceEqual(registration.PublicKey) && was.Auth.AsSpan().SequenceEqual(registration.Auth) && was.BackgroundSync == registration.BackgroundSync) return true;
            registrations[registration.DeviceId] = registration;
        }
        if (moved > 0) log($"push registration removed why=token_moved count={moved}");
        log("push registered");
        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Keeps a phone's report that the project has no app for <paramref name="package"/>, beside any registration the device has and never in its place;
    /// false, keeping nothing, for a device that is not paired.
    /// </summary>
    public async Task<bool> SetMissingAsync(string deviceId, string package, DateTimeOffset at)
    {
        if (!paired(deviceId))
        {
            log("push registration refused why=device_unpaired");
            return false;
        }
        lock (gate)
        {
            if (missing.TryGetValue(deviceId, out var was) && was.Package == package) return true;
            missing[deviceId] = new(deviceId, package, at);
        }
        log("push missing_app reported");
        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>The phones' reports of a package the project lacks, of devices paired now.</summary>
    public IReadOnlyList<MissingApp> Missing()
    {
        lock (gate) return [.. missing.Values.Where(report => paired(report.DeviceId))];
    }

    /// <summary>Forgets every report of a missing app: the files were replaced, and a phone whose app is still missing reports again when it asks for the offer.</summary>
    public Task ClearMissingAsync()
    {
        lock (gate)
        {
            if (missing.Count == 0) return Task.CompletedTask;
            missing.Clear();
        }
        return SaveAsync();
    }

    /// <summary>
    /// Forgets the device's registration; given <paramref name="token"/>, only while the row still holds that token (one registered since stays). Without one (the
    /// device removed), its report of a missing app goes too.
    /// </summary>
    public Task RemoveAsync(string deviceId, string why, string? token = null)
    {
        lock (gate)
        {
            var report = token is null && missing.Remove(deviceId);
            if (!registrations.TryGetValue(deviceId, out var was) || token is not null && was.Token != token) return report ? SaveAsync() : Task.CompletedTask;
            registrations.Remove(deviceId);
        }
        log($"push registration removed why={why}");
        return SaveAsync();
    }

    private async Task SaveAsync()
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            List<Stored> snapshot;
            lock (gate) snapshot = [.. registrations.Values.Select(r => new Stored(r.DeviceId, r.Token, r.Package, Convert.ToBase64String(r.PublicKey), Convert.ToBase64String(r.Auth), r.At, r.BackgroundSync)),
                .. missing.Values.Select(m => new Stored(m.DeviceId, "", m.Package, "", "", m.At))];
            var sealedBytes = await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(snapshot, Json)).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(sealedBytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            log($"push registrations not saved type={failure.GetType().Name}");
        }
        finally { writing.Release(); }
    }
}
