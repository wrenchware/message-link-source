using System.Text.Json;
using MessageLink.Core.Backend;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>How a <see cref="SharedSpace.PinAsync"/> ended.</summary>
public enum SpacePin
{
    /// <summary>The space is now filed under the key; the file was written.</summary>
    Pinned,
    /// <summary>The space was already filed under this key; nothing was written.</summary>
    Already,
    /// <summary>Not pinned: another key is pinned, the pinned file cannot be read, or the store holds rows made under another key.</summary>
    Refused,
}

/// <summary>
/// The key a server's shared space is filed under (stage 6 slice 2, docs/research/switchboard-stage6.md finding 2): <c>&lt;root&gt;\Server\space.protected</c>,
/// beside <c>shared.db</c>, sealed by the host's <see cref="IStateProtector"/>. A server serves it as its <c>LocalKey</c> once it exists, whatever its own
/// Google pairing says later, because every shared row's key is a one-way digest of the profile made from it: a server that paired Google itself would
/// otherwise get another key and orphan every row. Only a server's <see cref="SharedStateStore"/> has one, and only once the host attaches it
/// (<see cref="SharedStateStore.AttachSpace"/>), so the app's in-process and loopback hosts never read or write the file.
/// </summary>
/// <remarks>
/// <para><b>Pinned at the first write, not the first key served.</b> A key served while the store holds no row files nothing on the host, so nothing can be
/// orphaned by its changing. Pinning at the first serve would fix an empty space to whatever the server happened to have when a client first connected
/// (often the unpaired name), and migration (slice 13) could then no longer adopt the PC's key into it (D2). The first write's pin is made under the store's
/// gate with the very key the write's keys were judged under, so the pin and the first row cannot disagree, and a row is never kept unless the pin holds.</para>
/// <para><b>Never a new key over rows.</b> A store with fixed rows (one row per profile, such as the quick replies) and no file (one from before this slice)
/// adopts the key the host has now only when one of those rows was made under it; rows keyed by conversation (snoozes, chat preferences, names) carry their
/// profile inside their own digest, cannot be orphaned by a pin, and so neither decide nor block one. Otherwise the key they were made under cannot be known, and the space refuses rather than starting a second one beside them. A
/// file that cannot be read refuses the same way, and is never written over. Refusing makes <c>state.*</c> answer unavailable and the host's identity
/// absent (its clients keep the identity they last knew, so their writes wait in their pending tables instead of being made under another key).</para>
/// <para>Logs carry fixed words and counts only; never the key.</para>
/// </remarks>
public sealed class SharedSpace
{
    public const string FileName = "space.protected";
    private const int MaxBytes = 4096;

    private sealed record Stored(int Version, string LocalKey);

    /// <summary>The key a write is judged under, and the key to pin before its first row is kept (null once a key is pinned).</summary>
    internal sealed record Filing(string? Profile, string? Unpinned);

    private readonly string path;
    private readonly IStateProtector protector;
    private readonly Action<string>? log;
    private readonly Func<string?, Task<(int Rows, int Checkable, int Matching)>> rowsUnder;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Func<CancellationToken, Task<string?>>? live;
    private string? pinned;
    private bool unreadable;
    // The store holds no fixed row, so no key is ruled out (StoreOpenAsync). Volatile: the store clears it from under its own gate after an import.
    private volatile bool open;
    // The live key last seen beside a different pin, and the candidate last judged against rows of an unknown key: in memory only, never logged.
    private string? keptOver, refusedFor;
    private bool keptOverKnown, refusedKnown;

    /// <param name="rowsUnder">How many rows the store holds, how many of them name the profile they were made under, and how many of those name the given one; the store's file is not made by asking.</param>
    internal SharedSpace(string path, IStateProtector protector, Action<string>? log, Func<string?, Task<(int Rows, int Checkable, int Matching)>> rowsUnder)
    {
        this.path = path; this.protector = protector; this.log = log; this.rowsUnder = rowsUnder;
    }

    public string FilePath => path;

    /// <summary>The key the host's own Google pairing (or the unpaired name) gives now: what a first write pins and what is served before anything is.</summary>
    internal void Follow(Func<CancellationToken, Task<string?>> liveKey) => live = liveKey;

    /// <summary>
    /// What the host serves as its <c>LocalKey</c>, given what its own pairing says now: the pinned key once there is one (a different live key is
    /// logged <c>space key pinned kept=yes</c> and otherwise ignored), and the live key until then. Throws <see cref="SpaceUnavailableException"/> when
    /// the pinned file cannot be read or the store holds rows made under a key that is not this one.
    /// </summary>
    public async Task<string?> ServeAsync(string? liveKey, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await LoadAsync().ConfigureAwait(false) is { } pin)
            {
                if (!string.Equals(pin, liveKey, StringComparison.Ordinal))
                {
                    // Once per change of what the pairing says, since the identity is read every few seconds.
                    if (!keptOverKnown || !string.Equals(keptOver, liveKey, StringComparison.Ordinal)) log?.Invoke("space key pinned kept=yes");
                    keptOver = liveKey; keptOverKnown = true;
                }
                else keptOverKnown = false;
                return pin;
            }
            if (liveKey is null) return null;
            await AdoptOrRefuseAsync(liveKey).ConfigureAwait(false);
            return pinned ?? liveKey;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The key the space is filed under now: the pinned one, or the live one while nothing is pinned; null while nothing is signed in on the host and nothing is pinned. What
    /// a restored PC backup's choices wait for (stage 10). Throws <see cref="SpaceUnavailableException"/> as <see cref="ServeAsync"/> does.
    /// </summary>
    internal async Task<string?> CurrentKeyAsync(CancellationToken cancellationToken) =>
        await ServeAsync(live is null ? null : await live(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// What a <c>state.*</c> call files under now, read before the store's gate. Throws <see cref="SpaceUnavailableException"/> while the space
    /// cannot be served, including a store that holds rows while nothing is signed in on the host and nothing is pinned.
    /// </summary>
    internal async Task<Filing> FilingAsync(CancellationToken cancellationToken)
    {
        var liveKey = live is null ? null : await live(cancellationToken).ConfigureAwait(false);
        var key = await ServeAsync(liveKey, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (pinned is null && !await StoreOpenAsync().ConfigureAwait(false))
                {
                    Refuse(liveKey: null, "unknown_rows");
                    throw new SpaceUnavailableException();
                }
            }
            finally { gate.Release(); }
            return new Filing(null, null);
        }
        bool isPinned;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { isPinned = string.Equals(pinned, key, StringComparison.Ordinal); }
        finally { gate.Release(); }
        return new Filing(NotificationIdentity.ProfileOf(key), isPinned ? null : key);
    }

    /// <summary>
    /// Files the space under <paramref name="localKey"/> for good: the first write's key, or (slice 13) the key a migration brings from the PC, which may
    /// be adopted only while nothing else is pinned and the store holds no row made under another key. A second pin of the same key is
    /// <see cref="SpacePin.Already"/>; any other key is refused, as is a pin over a file that cannot be read. <paramref name="via"/> is a fixed word for the log.
    /// </summary>
    public async Task<SpacePin> PinAsync(string localKey, string via, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? pin;
            try { pin = await LoadAsync().ConfigureAwait(false); }
            catch (SpaceUnavailableException)
            {
                log?.Invoke("space key pin refused reason=unreadable");
                return SpacePin.Refused;
            }
            if (pin is not null)
            {
                if (string.Equals(pin, localKey, StringComparison.Ordinal)) return SpacePin.Already;
                log?.Invoke($"space key pin refused reason=mismatch via={via}");
                return SpacePin.Refused;
            }
            // Refused only over fixed rows made under another key; rows keyed by conversation cannot be orphaned by a pin (AdoptOrRefuseAsync).
            var (rows, checkable, matching) = await rowsUnder(NotificationIdentity.ProfileOf(localKey)).ConfigureAwait(false);
            if (checkable > 0 && matching == 0)
            {
                log?.Invoke($"space key pin refused reason=rows rows={rows} via={via}");
                return SpacePin.Refused;
            }
            return await WriteAsync(localKey, via).ConfigureAwait(false) ? SpacePin.Pinned : SpacePin.Refused;
        }
        finally { gate.Release(); }
    }

    // Called with the gate held. The pinned key, or null when there is no file; throws when there is one that cannot be read. A key once read is kept for
    // the run: the host is the file's only writer.
    private async Task<string?> LoadAsync()
    {
        if (pinned is not null) return pinned;
        if (!File.Exists(path)) { unreadable = false; return null; }
        try
        {
            var info = new FileInfo(path);
            if (info.Length is 0 or > MaxBytes) throw new InvalidDataException("The space key has the wrong size.");
            var stored = JsonSerializer.Deserialize<Stored>(await protector.UnprotectAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false));
            if (stored is not { Version: 1, LocalKey: { Length: > 0 } key } || string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("The space key could not be read.");
            pinned = key;
            if (unreadable) log?.Invoke("space key readable again");
            unreadable = false;
            log?.Invoke("space key loaded");
            return pinned;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Said once per failing spell; the file is tried again at each look, and never written over.
            if (!unreadable) log?.Invoke($"space key unreadable reason={failure.GetType().Name}");
            unreadable = true;
            throw new SpaceUnavailableException();
        }
    }

    // Called with the gate held, with no pin. An empty store (or none) leaves the space open for its first write; one whose rows were made under the live
    // key adopts it; any other refuses.
    private async Task AdoptOrRefuseAsync(string liveKey)
    {
        if (await StoreOpenAsync().ConfigureAwait(false)) return;
        if (refusedKnown && string.Equals(refusedFor, liveKey, StringComparison.Ordinal)) throw new SpaceUnavailableException();
        var (rows, checkable, matching) = await rowsUnder(NotificationIdentity.ProfileOf(liveKey)).ConfigureAwait(false);
        // Only rows whose key is one fixed row of the profile can say which profile they were made under, and only they can be orphaned by a pin: a row
        // of a category keyed by conversation (a snooze taken in from the host's files, slice 3) carries its own profile inside its digest whatever is pinned.
        // A store of those alone is open, as an empty one is: refusing it would lock the space for good, as nothing could ever be adopted.
        if (checkable == 0) { open = true; return; }
        if (matching == 0)
        {
            Refuse(liveKey, "unknown_rows", rows);
            throw new SpaceUnavailableException();
        }
        if (!await WriteAsync(liveKey, "adopt").ConfigureAwait(false)) throw new SpaceUnavailableException();
        log?.Invoke($"space key adopted rows={rows} matched={matching}");
    }

    // Called with the gate held. Whether the store holds no fixed row (see AdoptOrRefuseAsync), so any key may still be pinned. Once seen open with no pin it
    // stays so for the run, since a fixed row is only added by a write and a write pins first; an import that adds rows says so (RowsAdded) all the same.
    private async Task<bool> StoreOpenAsync()
    {
        if (open) return true;
        var (_, checkable, _) = await rowsUnder(null).ConfigureAwait(false);
        open = checkable == 0;
        return open;
    }

    /// <summary>The store took rows in without a write (an import, under its own gate): what was known of it is read again at the next look.</summary>
    internal void RowsAdded() => open = false;

    private void Refuse(string? liveKey, string reason, int rows = -1)
    {
        if (refusedKnown && string.Equals(refusedFor, liveKey, StringComparison.Ordinal)) return;
        refusedFor = liveKey; refusedKnown = true;
        log?.Invoke($"space key refused reason={reason}{(rows >= 0 ? $" rows={rows}" : "")}");
    }

    // Called with the gate held. Written beside the file and moved into place without replacing anything, so a pin is never written over another, and a
    // half-written file is never read as a key. Only the owner can read it off Windows, as the device and push registries.
    private async Task<bool> WriteAsync(string localKey, string via)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        var sealedKey = await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(new Stored(1, localKey))).ConfigureAwait(false);
        var temporary = Path.Combine(folder, FileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(sealedKey).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another writer got there first (none should): what is there now decides.
            TryDelete(temporary);
            pinned = null;
            try { return string.Equals(await LoadAsync().ConfigureAwait(false), localKey, StringComparison.Ordinal); }
            catch (SpaceUnavailableException) { return false; }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
        pinned = localKey;
        unreadable = false; refusedKnown = false; keptOverKnown = false;
        log?.Invoke($"space key pinned via={via}");
        return true;
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>The space key cannot be served now (<see cref="SharedSpace"/>). Crosses the wire as unavailable, never with a key or a path.</summary>
public sealed class SpaceUnavailableException() : Exception("The server cannot tell where its shared state is kept.")
{
    /// <summary>What <c>state.*</c> answers in its place: unavailable, so a client keeps its pending writes for a later connection.</summary>
    internal static BackendException Answer() => new(BackendFailure.Unavailable, "The server cannot open its shared state right now.");
}

/// <summary>
/// The host's identity as a server serves it: the live one (Google paired, the saved-message scopes) with the pinned space key as its <c>LocalKey</c>.
/// While the space cannot be served, reading fails, so the host's state carries no identity and its clients keep the one they last knew.
/// </summary>
internal sealed class SpaceIdentity(IHostIdentity live, SharedSpace space) : IHostIdentity
{
    public bool GooglePaired => live.GooglePaired;

    public async Task<HostIdentity> ReadAsync(CancellationToken cancellationToken = default)
    {
        var now = await live.ReadAsync(cancellationToken).ConfigureAwait(false);
        var key = await space.ServeAsync(now.LocalKey, cancellationToken).ConfigureAwait(false);
        if (string.Equals(key, now.LocalKey, StringComparison.Ordinal)) return now;
        // Google's saved messages are filed by a scope made from the key ("google:" and the key's profile, HostIdentityPort), so a server that pairs Google as a new device
        // would otherwise show none of the PC's: its own scope names the live key, and the saved rows (adopted verbatim by a move) name the pinned one (slice 13g). Only
        // an entry that is exactly the live key's Google scope follows the pin; every other account's scope is its own, and nothing is added where Google is not paired.
        var liveScope = NotificationIdentity.ProfileOf(now.LocalKey) is { } from ? GoogleScopePrefix + from : null;
        var pinnedScope = NotificationIdentity.ProfileOf(key) is { } to ? GoogleScopePrefix + to : null;
        return now with
        {
            LocalKey = key,
            SavedScopes = liveScope is null || pinnedScope is null ? now.SavedScopes
                : [.. now.SavedScopes.Select(scope => string.Equals(scope.Scope, liveScope, StringComparison.Ordinal) ? scope with { Scope = pinnedScope } : scope)],
        };
    }

    private const string GoogleScopePrefix = "google:";

    public Task RefreshAsync(CancellationToken cancellationToken = default) => live.RefreshAsync(cancellationToken);

    public event EventHandler? Changed
    {
        add => live.Changed += value;
        remove => live.Changed -= value;
    }
}
