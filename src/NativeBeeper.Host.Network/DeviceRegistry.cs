using System.Security.Cryptography;
using System.Text.Json;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// The devices paired with a network host (SWITCHBOARD.md stage 4). A device's token is 32 random bytes made at pairing and handed over once; the host keeps
/// only its SHA-256, with the device's name, when it paired and when it was last seen, in <c>&lt;data&gt;/devices.json</c>, sealed through the host's
/// <see cref="IStateProtector"/>. A token is checked against every device in fixed time. Revoking a device removes it at once, so its token is refused from the
/// next upgrade on, and raises <see cref="Revoked"/> for the host to close its connections. A file that cannot be read stops the host rather than being
/// replaced: an empty list would print a pairing code and then overwrite every pairing.
/// <para>
/// Every write numbers the list (a generation, sealed inside it) and, when the host has a keys folder apart from its data (the Linux host's <c>/keys</c>),
/// records that number there too (<see cref="GenerationFile"/>). An older list put back over the newer one, such as a backup of the data restored, would
/// bring back the devices revoked since; it is refused at start (<c>server devices rolled_back action=refused</c>) rather than trusted. Deleting the list
/// is still the way to unpair everything: no list means no devices, whatever the number says. A list ahead of the number (a stop between the two writes)
/// is taken. A restored copy of the keys volume rolls the number back with it, so the README also tells the owner to revoke again after a restore.
/// </para>
/// </summary>
internal sealed class DeviceRegistry
{
    public const string FileName = "devices.json";
    /// <summary>The generation of the newest list written, in the keys folder: a decimal number, nothing secret, private all the same.</summary>
    public const string GenerationFile = "devices.generation";
    /// <summary>The most devices a host keeps; a pairing past it is refused.</summary>
    public const int MaxDevices = 32;

    private sealed record Device(string Id, string Name, DateTimeOffset Created, DateTimeOffset? LastSeen, byte[] Hash);
    private sealed record StoredDevice(string Id, string Name, DateTimeOffset Created, DateTimeOffset? LastSeen, string Hash);
    // The list as written since generations: a list from before them was a bare array, read as generation 0.
    private sealed record StoredList(long Generation, List<StoredDevice> Devices);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // A device seen again within this is not written again: a reconnecting client must not turn into a stream of writes.
    private static readonly TimeSpan SeenGranularity = TimeSpan.FromMinutes(1);

    private readonly string path;
    private readonly IStateProtector protector;
    private readonly TimeProvider time;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly string? generationPath;
    private List<Device> devices;
    private readonly Dictionary<string, Device> joining = [];
    private long generation;

    private DeviceRegistry(string path, IStateProtector protector, TimeProvider time, Action<string> log, List<Device> devices, string? generationPath, long generation)
    {
        this.path = path; this.protector = protector; this.time = time; this.log = log; this.devices = devices;
        this.generationPath = generationPath; this.generation = generation;
    }

    /// <summary>The record of the restore this server was made from, when it was (stage 10): an unknown token may then be a device from after the backup.</summary>
    internal NativeBeeper.Host.Backup.RestoredMark? Restored { get; set; }

    /// <summary>
    /// Whether a token no device holds is answered as a device from before a restore (<see cref="NativeBeeper.Host.Backup.RestoredMark"/>): the server was restored from a
    /// backup, recently enough, and the token is not one removed since.
    /// </summary>
    public bool RefusesAsRestored(byte[]? presented) =>
        presented is { Length: ClientToken.Length } && Restored is { } mark && mark.RefusesAsRestored(SHA256.HashData(presented), time.GetUtcNow());

    /// <summary>The generation of the list last read or written.</summary>
    public long Generation => Interlocked.Read(ref generation);

    /// <summary>Raised with a device's id once it has been removed (after the file says so).</summary>
    public event Action<string>? Revoked;

    public string Path => path;

    public int Count
    {
        get
        {
            lock (gate) return devices.Count;
        }
    }

    /// <summary>
    /// Reads the devices kept in <paramref name="folder"/>; none when there is no file. Throws <see cref="InvalidDataException"/> for a file that cannot be
    /// read back, or that is older than the generation recorded in <paramref name="keysFolder"/> (when one is given).
    /// </summary>
    public static async Task<DeviceRegistry> LoadAsync(string folder, IStateProtector protector, TimeProvider time, Action<string> log, string? keysFolder = null)
    {
        var path = System.IO.Path.Combine(folder, FileName);
        var generationPath = keysFolder is null ? null : System.IO.Path.Combine(keysFolder, GenerationFile);
        var recorded = ReadGeneration(generationPath, log);
        var devices = new List<Device>();
        var generation = recorded;
        if (File.Exists(path))
        {
            long written;
            try
            {
                using var document = JsonDocument.Parse(await protector.UnprotectAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false));
                List<StoredDevice> stored;
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    stored = document.RootElement.Deserialize<List<StoredDevice>>(Json) ?? throw new InvalidDataException();
                    written = 0;
                }
                else
                {
                    var list = document.RootElement.Deserialize<StoredList>(Json) ?? throw new InvalidDataException();
                    stored = list.Devices ?? throw new InvalidDataException();
                    written = list.Generation;
                }
                foreach (var device in stored)
                {
                    var hash = Convert.FromBase64String(device.Hash);
                    if (string.IsNullOrEmpty(device.Id) || hash.Length != 32) throw new InvalidDataException();
                    devices.Add(new Device(device.Id, device.Name ?? "", device.Created, device.LastSeen, hash));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log("server devices unreadable action=refused");
                throw new InvalidDataException("The server's list of paired devices cannot be read. Restore it, or delete it and pair every device again.", ex);
            }
            if (written < recorded)
            {
                log("server devices rolled_back action=refused");
                throw new InvalidDataException("The server's list of paired devices is older than the last one it wrote (a backup put back?), so it may hold devices that were revoked. Delete it and pair every device again.");
            }
            generation = written;
        }
        log($"server devices count={devices.Count}");
        return new DeviceRegistry(path, protector, time, log, devices, generationPath, Math.Max(generation, recorded));
    }

    // The recorded generation; 0 when there is none yet (a first start, or a host from before generations). One that cannot be read is not guessed at.
    private static long ReadGeneration(string? path, Action<string> log)
    {
        if (path is null || !File.Exists(path)) return 0;
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (text.Length <= 19 && long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)) return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        log("server devices generation unreadable action=refused");
        throw new InvalidDataException("The record of the paired devices' generation cannot be read.");
    }

    /// <summary>The id of the device whose token <paramref name="presented"/> is, or null. Every device is compared, in fixed time, whatever matched first.</summary>
    public string? Authenticate(byte[]? presented)
    {
        if (presented is not { Length: ClientToken.Length }) return null;
        var hash = SHA256.HashData(presented);
        Device[] all;
        lock (gate) all = [.. devices];
        string? found = null;
        foreach (var device in all)
            if (CryptographicOperations.FixedTimeEquals(device.Hash, hash)) found = device.Id;
        return found;
    }

    public bool Has(string id)
    {
        lock (gate) return devices.Any(device => device.Id == id);
    }

    public IReadOnlyList<PairedDevice> List(string? current)
    {
        lock (gate) return [.. devices.Select(device => new PairedDevice(device.Id, device.Name, device.Created, device.LastSeen, device.Id == current))];
    }

    /// <summary>A new device and its token, kept before this returns; null when <see cref="MaxDevices"/> are paired already. The token is in nothing this keeps.</summary>
    public async Task<(string Id, byte[] Token)?> AddAsync(string name)
    {
        var token = RandomNumberGenerator.GetBytes(ClientToken.Length);
        var device = new Device(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), CleanName(name), time.GetUtcNow(), null, SHA256.HashData(token));
        lock (gate)
        {
            if (devices.Count + joining.Count >= MaxDevices) return null;
            devices = [.. devices, device];
        }
        try { await SaveAsync().ConfigureAwait(false); }
        catch (Exception)
        {
            // Not kept, so not paired: the device never learns a token that the next start would not know.
            lock (gate) devices = [.. devices.Where(other => other.Id != device.Id)];
            throw;
        }
        log($"server device paired id={device.Id}");
        return (device.Id, token);
    }

    // A token offered on the join socket is not yet a pairing. In particular, another device's Seen/save must not
    // persist it before its acknowledgement. A lost socket drops this reservation without needing a disk write.
    public (string Id, byte[] Token)? BeginJoin(string name)
    {
        var token = RandomNumberGenerator.GetBytes(ClientToken.Length);
        var device = new Device(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)), CleanName(name), time.GetUtcNow(), null, SHA256.HashData(token));
        lock (gate)
        {
            if (devices.Count + joining.Count >= MaxDevices) return null;
            joining.Add(device.Id, device);
        }
        return (device.Id, token);
    }

    public void DropJoin(string id) { lock (gate) joining.Remove(id); }

    public async Task<bool> AcknowledgeJoinAsync(string id)
    {
        lock (gate)
        {
            if (!joining.Remove(id, out var device)) return false;
            devices = [.. devices, device];
        }
        // Once acknowledged, failures are uncertain: a write may have reached disk before a later flush failed.
        await SaveAsync().ConfigureAwait(false);
        log($"server device paired id={id}");
        return true;
    }

    /// <summary>
    /// Removes the device, keeps the list, then raises <see cref="Revoked"/>. False when there was no such device. A list that cannot be written fails the
    /// revoke and puts the device back where it was: a revoke kept only in memory would quietly come undone at the next start, so the owner is told
    /// instead and can try again.
    /// </summary>
    public async Task<bool> RevokeAsync(string id)
    {
        Device removed;
        int index;
        lock (gate)
        {
            index = devices.FindIndex(device => device.Id == id);
            if (index < 0) return false;
            removed = devices[index];
            // Out of the list first: its token is refused while the file is written.
            devices = [.. devices.Where(device => device.Id != id)];
        }
        try { await SaveAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (!devices.Any(device => device.Id == id))
                {
                    var restored = devices.ToList();
                    restored.Insert(Math.Min(index, restored.Count), removed);
                    devices = restored;
                }
            }
            log($"server device revoke failed type={ex.GetType().Name}");
            throw;
        }
        log($"server device revoked id={id}");
        // Told from now on that it was removed, not that the server was restored.
        if (Restored is { } mark) await mark.NoteRevokedAsync(removed.Hash).ConfigureAwait(false);
        Revoked?.Invoke(id);
        return true;
    }

    /// <summary>
    /// Gives the device a new name (cleaned as a name given at pairing is) and keeps the list. Null when there was no such device. A list that cannot be written fails
    /// the rename and puts the old name back, as a revoke does: a name kept only in memory would quietly come undone at the next start.
    /// </summary>
    public async Task<string?> RenameAsync(string id, string name)
    {
        var clean = CleanName(name);
        string before;
        lock (gate)
        {
            var index = devices.FindIndex(device => device.Id == id);
            if (index < 0) return null;
            before = devices[index].Name;
            if (before == clean) return clean;
            var updated = devices.ToList();
            updated[index] = updated[index] with { Name = clean };
            devices = updated;
        }
        try { await SaveAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            lock (gate)
            {
                var index = devices.FindIndex(device => device.Id == id);
                if (index >= 0 && devices[index].Name == clean)
                {
                    var restored = devices.ToList();
                    restored[index] = restored[index] with { Name = before };
                    devices = restored;
                }
            }
            log($"server device rename failed type={ex.GetType().Name}");
            throw;
        }
        log($"server device renamed id={id}");
        return clean;
    }

    /// <summary>Notes that the device connected; written at most once a minute per device, in the background.</summary>
    public void Seen(string id)
    {
        var now = time.GetUtcNow();
        lock (gate)
        {
            var index = devices.FindIndex(device => device.Id == id);
            if (index < 0 || devices[index].LastSeen is { } last && now - last < SeenGranularity) return;
            var updated = devices.ToList();
            updated[index] = updated[index] with { LastSeen = now };
            devices = updated;
        }
        _ = Task.Run(async () =>
        {
            try { await SaveAsync().ConfigureAwait(false); }
            catch (Exception ex) { log($"server devices save failed type={ex.GetType().Name}"); }
        });
    }

    // The whole list, written to a temporary file (the owner's alone off Windows) and moved over the old one. One write at a time, each of the latest list.
    private async Task SaveAsync()
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            List<StoredDevice> snapshot;
            lock (gate) snapshot = [.. devices.Select(device => new StoredDevice(device.Id, device.Name, device.Created, device.LastSeen, Convert.ToBase64String(device.Hash)))];
            var next = Generation + 1;
            var sealedBytes = await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(new StoredList(next, snapshot), Json)).ConfigureAwait(false);
            // The list first, then its number: a stop between them leaves a list ahead of the number, which a start takes; never the other way round.
            await WritePrivateAsync(path, sealedBytes).ConfigureAwait(false);
            Interlocked.Exchange(ref generation, next);
            if (generationPath is not null)
                await WritePrivateAsync(generationPath, System.Text.Encoding.ASCII.GetBytes(next.ToString(System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
        }
        finally { writing.Release(); }
    }

    // Written to a temporary file (the owner's alone off Windows), flushed, and moved over the old one.
    private static async Task WritePrivateAsync(string target, byte[] bytes)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(temporary, options))
        {
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, target, true);
    }

    // What a device calls itself, as a list shows it: trimmed, no control characters, cut to the longest kept, and never empty.
    internal static string CleanName(string? name)
    {
        // Format characters too (a right-to-left override, a zero-width joiner): a name asking to join is anyone's on the home network, and is read at the moment of approving it.
        var clean = new string([.. (name ?? "").Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)]).Trim();
        if (clean.Length > NetworkPairing.MaxDeviceName) clean = clean[..NetworkPairing.MaxDeviceName].TrimEnd();
        if (char.IsHighSurrogate(clean.LastOrDefault())) clean = clean[..^1];
        return clean.Length == 0 ? "Device" : clean;
    }
}
