using System.Text.Json;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// This PC's memory of the last identity the server reported (<see cref="ServerIdentity"/>), protected with the same <see cref="IStateProtector"/>
/// as the rest of what a client keeps. It exists so that a launch draws from local data and never waits for the server's first state. It holds
/// only one-way derived values (the local key, the saved-message scopes), but they key everything the person has saved, so it is not left in the clear.
/// </summary>
public sealed class LastKnownIdentity(string path, IStateProtector protector)
{
    private const long MaxBytes = 64 * 1024;

    /// <summary>The identity last saved, or none: no file, an unreadable one (another Windows user's, damaged, too large) and a version this build does not know all read as none.</summary>
    public async Task<ServerIdentity?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(path, cancellationToken));
            try { return JsonSerializer.Deserialize<ServerIdentity>(plain, ProtocolJson.Options); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>Replaces the saved identity. Written beside the file and moved over it, so a crash leaves the old one whole.</summary>
    public async Task SaveAsync(ServerIdentity identity, CancellationToken cancellationToken = default)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(identity, ProtocolJson.Options);
        try
        {
            var sealedBytes = await protector.ProtectAsync(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
                {
                    await file.WriteAsync(sealedBytes, cancellationToken);
                    file.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }
}

/// <summary>
/// The client's <see cref="IServerIdentity"/>: what the server's pushed state says (<see cref="ServerStateCache.Identity"/>), else what this run last
/// saw, else the <see cref="LastKnownIdentity"/> from the last run, else nothing. Nothing here waits for the server: before the first state it
/// answers from this PC's own memory, and when a state arrives that differs it answers from that and saves it.
/// </summary>
/// <remarks>
/// An unprimed cache with no memory is the conservative answer ("unknown capability means unavailable"): not paired, no key, no
/// account. A server that sends no identity (an older one) changes nothing: the last known stands. Nothing is logged: no value here is a thing to print.
/// </remarks>
public sealed class StateIdentity : IServerIdentity, IDisposable
{
    private readonly ServerStateCache cache;
    private readonly LastKnownIdentity? memory;
    private readonly Action<string>? log;
    private readonly Task<ServerIdentity?> remembered;
    private readonly Func<CancellationToken, Task<ServerIdentity?>>? refresh;
    private readonly object gate = new();
    private ServerIdentity? seen;
    private ServerIdentity? pending;
    private bool writing, disposed;

    /// <param name="refresh">
    /// Asks the server to read the identity again now and to publish it, returning what it will say (or none). A server in this process gives it;
    /// a client with none relies on the server following its own files, which it does within its next look.
    /// </param>
    public StateIdentity(ServerStateCache cache, LastKnownIdentity? memory = null, Action<string>? log = null, Func<CancellationToken, Task<ServerIdentity?>>? refresh = null)
    {
        this.cache = cache;
        this.memory = memory;
        this.log = log;
        this.refresh = refresh;
        // On the pool, so that waiting for it from an interface thread (GooglePaired) cannot wait on that thread.
        remembered = memory is null ? Task.FromResult<ServerIdentity?>(null) : Task.Run(() => memory.LoadAsync());
        cache.IdentityChanged += Arrived;
        if (cache.Identity is { } primed) Remember(primed);
    }

    public bool GooglePaired => Known() is { } identity ? identity.GooglePaired : Last()?.GooglePaired ?? false;

    public async Task<ServerIdentity> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (Known() is { } identity) return identity;
        return await remembered.WaitAsync(cancellationToken) ?? ServerIdentity.Unknown;
    }

    public event EventHandler? Changed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (refresh is null) return;
        ServerIdentity? expected;
        try { expected = await refresh(cancellationToken); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return; }
        if (expected is null) return;
        // The server has published it; this client's cache has it once the state's frame has been read, a moment later.
        var wanted = JsonSerializer.Serialize(expected, ProtocolJson.Options);
        bool Reached() => Known() is { } known && string.Equals(JsonSerializer.Serialize(known, ProtocolJson.Options), wanted, StringComparison.Ordinal);
        if (Reached()) return;
        var caughtUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Heard(object? sender, ServerIdentity identity) { if (Reached()) caughtUp.TrySetResult(); }
        cache.IdentityChanged += Heard;
        try
        {
            if (!Reached()) await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        }
        catch (TimeoutException) { /* the state arrives on its own */ }
        finally { cache.IdentityChanged -= Heard; }
    }

    // The newest the server has said, in this run.
    private ServerIdentity? Known()
    {
        if (cache.Identity is { } current) return current;
        lock (gate) return seen;
    }

    // Before any state: this PC's memory of the last run. The bound is only for a disk that never answers.
    private ServerIdentity? Last() => remembered.Wait(TimeSpan.FromSeconds(2)) ? remembered.Result : null;

    private void Arrived(object? sender, ServerIdentity identity)
    {
        Remember(identity);
        try { Changed?.Invoke(this, EventArgs.Empty); } catch (Exception) { /* a listener that fails must not fail the feed */ }
    }

    private void Remember(ServerIdentity identity)
    {
        lock (gate)
        {
            if (disposed) return;
            seen = identity;
            if (memory is null) return;
            pending = identity;
            if (writing) return;
            writing = true;
        }
        _ = Task.Run(SaveAsync);
    }

    private async Task SaveAsync()
    {
        try
        {
            // What the disk holds is not written again.
            var onDisk = await remembered.ConfigureAwait(false) is { } before ? JsonSerializer.Serialize(before, ProtocolJson.Options) : null;
            while (true)
            {
                ServerIdentity next;
                lock (gate)
                {
                    if (pending is not { } taken) { writing = false; return; }
                    next = taken;
                    pending = null;
                }
                var json = JsonSerializer.Serialize(next, ProtocolJson.Options);
                if (string.Equals(json, onDisk, StringComparison.Ordinal)) continue;
                try { await memory!.SaveAsync(next).ConfigureAwait(false); onDisk = json; }
                catch (Exception) { try { log?.Invoke("identity memory save failed"); } catch (Exception) { /* a logger that fails must not fail the client */ } }
            }
        }
        catch (Exception)
        {
            lock (gate) writing = false;
        }
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        cache.IdentityChanged -= Arrived;
    }
}

/// <summary>
/// The access-token store the client's windows and services read the local key through (<c>NotificationIdentity.ProfileAsync</c>), answered from
/// <see cref="IServerIdentity"/> instead of the Google session files. It is the host-side <c>GoogleSessionProfile</c> seen from a client: the same
/// value, never used in HTTP.
/// </summary>
public sealed class IdentityAccessTokens(IServerIdentity identity) : IAccessTokenStore
{
    public async ValueTask<string?> GetAsync(CancellationToken cancellationToken = default) => (await identity.ReadAsync(cancellationToken)).LocalKey;
}
