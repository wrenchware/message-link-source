using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MessageLink.Core.Backend;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;
using MessageLink.Storage;

namespace MessageLink.App.Services;

/// <summary>
/// The window's protected state when it is a client of a Messages Server (<c>local</c> and <c>remote</c>): a replica of the
/// server's shared state at <c>&lt;root&gt;\Replica\&lt;server id&gt;\state.db</c> (<see cref="ServerIdOf"/>), which every read draws from. The in-process app and
/// the loopback never register it, so their <c>state.db</c> is untouched, and data from two hosts never mixes.
/// </summary>
/// <remarks>
/// <para><b>Writes.</b> A key <see cref="SharedCategories"/> names (under the profile the keys are filed under) goes into the replica and, in the same
/// transaction, into a persisted <c>pending</c> table with an operation id made now. A worker sends the pending writes once a server that keeps the shared
/// state (protocol version 2) is connected, and again after a reconnect with the same id, which the server answers with its first answer (a <c>state.put</c> is
/// never a provider mutation, so sending it again is safe). Writes to one key not sent yet are folded into the newest before it goes, so a burst of edits
/// is one put. Every other key stays in the replica only, as it stayed on this PC before. Nothing that leads to a send is ever kept here.</para>
/// <para><b>Remote changes.</b> A <c>shared</c> event, a <c>state.sync</c> on every connection and after every resync, and the answer to each put fill the
/// replica. Only rows this client shares itself are taken (a row of a category it does not know, or of another profile, is left alone, and never touches a
/// row of its own). A row's rev orders it: an older one is ignored. A row with a pending write keeps the words written here, since that write follows in
/// the server's order; the newest rev seen for it meanwhile is kept (<c>overtaken</c>), and if the server's answer is older than that (the put had been
/// committed before, and its answer lost), the server's row is read and taken once nothing is pending for it. A write the server refuses is replaced by the
/// server's row, or dropped. <see cref="RemoteChanged"/> names the keys that changed. A snapshot (another epoch, a position the server no longer covers, or a
/// new profile) replaces the shared rows held from the server and keeps every pending write.</para>
/// <para><b>A root that was used in process.</b> When the replica is made, the one row of <c>state.db</c> a client must not lose is copied in, read-only:
/// the sends whose outcome is unknown (<c>refused:</c>, InterruptedSends), so their warning stays and they are still asked about with <c>ops.status</c>.
/// Scheduled sends are not copied: a copy would make two places that fire them.</para>
/// <para><b>Versions.</b> The categories grow with the protocol version (slice 3 added the chat preferences, names, snoozes and pinned order at version 3). A
/// write of a category newer than the connection's version waits in the queue, unsent, for a server that keeps it (one sent now would be refused and give way
/// to the server's row, or to none), and a snapshot drops only the rows of the categories its connection knows. The version the server last spoke is kept here
/// (<see cref="ServerProtocolAsync"/>): the stores that were files draw from these rows only when it is 3 or more.</para>
/// <para><b>Compare-and-set rows</b> (<see cref="SharedConflictPolicy.CompareAndSet"/>: the merges and the labels, each one row). A write is queued
/// with the rev the replica holds for the row (<c>if_rev</c>, 0 for none: create only), and with the tag of the newest change of its follower's it holds
/// (<see cref="ISharedRows"/>). What holds the row in memory (MergedConversations, LabelStore) reads it and becomes its follower in one step under the
/// replica's gate (<see cref="FollowAsync"/>), so its memory and the replica's rev never part: every row of the server's this replica takes for it afterwards is
/// handed to it (<see cref="ISharedRowFollower.CatchUpAsync"/>), as is the server's row when a write loses (<c>conflict</c>), after the replica holds the server's
/// row and has dropped the writes made before; the follower makes its changes again over it and writes the result. This is done by the worker, which sends
/// nothing meanwhile, so a write made from what the follower held before never reaches the server after the one made from the server's row. A follower that
/// cannot be brought to the row is abandoned (<see cref="ISharedRowFollower.AbandonAsync"/>). A write the server took is confirmed to the follower by its tag,
/// and the writes queued behind it take its rev as theirs. A row that loses more than <see cref="ReapplyLimit"/> times in a row keeps what this device holds
/// and its write, which waits (<see cref="ParkFirst"/>, doubling) until a newer row of the server's arrives, the wait ends, or the next connection, and says so
/// (<see cref="Told"/>). A row of such a category that this replica held before it was shared (no rev, nothing queued) is kept through a snapshot and offered
/// at the end of every sync, create only: if another device's is there first, that one wins, and a copy of this device's is kept under
/// <see cref="ReplacedPrefix"/> and said to be.</para>
/// <para><b>Drafts</b> (protocol 6: <c>draft:</c> and <c>draft-owner:</c>, last writer wins) are plain rows of the replica; the composers keep them in
/// memory and are told of the server's changes by <see cref="ServerChanged"/> (the keys <see cref="RemoteChanged"/> names), which they judge themselves: a row
/// with a write pending here is never raised, so the words typed on this device stand, as the other rows with a pending write do. Rows made by an earlier
/// build in a replica, with no rev from a server and nothing queued, are dropped by the first snapshot of a server that keeps them (as the other last-writer-wins
/// rows are; only the compare-and-set rows are offered).</para>
/// <para>Values are sealed by the client's own protector here, as in <c>state.db</c>. Logs carry counts and fixed words only: <c>shared pending n=</c>,
/// <c>shared sync reset= rows= ms=</c>, <c>shared put n= conflicts= ms=</c>, <c>shared applied rows= ms=</c>, <c>shared reapply n=</c>,
/// <c>shared reapply gave_up wait_ms=</c>, <c>shared local offered n=</c>, <c>shared local replaced n=</c>, <c>shared follow failed error=</c> and
/// <c>shared state unavailable</c>.</para>
/// </remarks>
internal sealed class ReplicatedStateStore : IPrivateStateStore, ICompactableStore, IConditionalSharedRows, IDisposable
{
    public const string FolderName = "Replica";
    public const string FileName = "state.db";
    /// <summary>The server id of the <c>local</c> mode's own server.</summary>
    public const string LocalServer = "local";
    /// <summary>The server id of a <c>remote</c> mode whose fingerprint is missing or not valid: it connects to nothing, and its replica mixes with no other.</summary>
    public const string UnpinnedServer = "unpinned";
    /// <summary>What is copied from the root's <c>state.db</c> when the replica is made: InterruptedSends' category.</summary>
    public const string CarriedCategory = SharedCategories.Refused + ":";
    // As SqlitePrivateStateStore: expendable copies trimmed oldest first, and nothing larger than a value the PC keeps.
    private const int CacheLimit = 64;
    private const int MaxValue = 16 * 1024 * 1024;

    /// <summary>How many times in a row a compare-and-set row's write is made again after losing before it waits for the next connection.</summary>
    public const int ReapplyLimit = 3;

    private sealed record Meta(string? Epoch, long Rev, bool Snapshot, string? Profile, int? HostProtocol);
    // IfRev: the rev the write was made from (compare-and-set rows only). Tag: the newest change of the row's follower it holds. Epoch: the space IfRev was read in, kept with the
    // write because the replica's own epoch moves on at a snapshot while the write waits, and a rev of the old space can match a different row of the new one (null: none, or a write queued before this was kept).
    private sealed record Pending(long Seq, string ClientOpId, string Key, byte[]? Payload, int Folded, long? IfRev = null, long? Tag = null, string? Epoch = null);
    private sealed record Arrival(SharedChange Change, long At);

    private readonly string path;
    private readonly string? carryFrom;
    private readonly IStateProtector protector;
    private readonly ProtocolClient client;
    private readonly ISharedState remote;
    private readonly IAccessTokenStore tokens;
    private readonly Action<string>? log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim wake = new(0);
    private readonly ConcurrentQueue<Arrival> arrivals = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private int connected, syncWanted, flushWanted;
    // The newest commit an event has shown, so a sync that ends short of it goes on.
    private long seen;
    // This connection's server keeps no shared state (version 1, or a server without the store); false again on the next connection.
    private volatile bool hostLacks;
    // The last server version heard (ServerProtocolAsync), as the replica keeps it; -1 until read.
    private int hostProtocol = -1;
    // Connections made (OnConnected), and the last of them whose version is settled (SettleAsync).
    private int connections, settled;
    // The client's connection number (ProtocolClient.ConnectionNumber) of the connection OnConnected last heard of: a higher one is up and not heard of yet.
    private int connectionSeen;
    // A connection has settled during this run: until one has, what the rows hold is last run's and the server may have changed since (ServerVersionNow).
    private int everSettled;
    // The meta table has the column a replica made by slice 1 lacks, and the pending table those made before slice 5 lack (Open, once per instance).
    private bool metaReady;
    private bool disposed;
    // What holds each compare-and-set row in memory (FollowAsync); how many times in a row each lost, this connection.
    private readonly ConcurrentDictionary<string, ISharedRowFollower> followers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> losses = new(StringComparer.Ordinal);
    // A parked row: the server's rev it lost to last (a newer one arriving unparks it), and how many times it gave up this connection (the wait doubles).
    private readonly ConcurrentDictionary<string, long> parked = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> parkings = new(StringComparer.Ordinal);

    /// <param name="carryFrom">The root's own <c>state.db</c>, from which the unsettled sends are copied when this replica is made; null copies nothing.</param>
    /// <param name="remote">The server's shared state; the proxy over <paramref name="client"/> unless a test gives its own.</param>
    public ReplicatedStateStore(string path, IStateProtector protector, ProtocolClient client, IAccessTokenStore tokens, Action<string>? log = null, ISharedState? remote = null,
        string? carryFrom = null)
    {
        this.path = path; this.protector = protector; this.client = client; this.tokens = tokens; this.log = log; this.carryFrom = carryFrom;
        this.remote = remote ?? new SharedStateProxy(client);
        client.Connected += OnConnected;
        client.SharedChanged += OnShared;
        client.FeedResynced += OnResynced;
        worker = Task.Run(RunAsync, CancellationToken.None);
        // The last server version, read ahead so Shares answers from it before a connection settles.
        _ = Task.Run(async () =>
        {
            try { await KnownServerProtocolAsync(stop.Token).ConfigureAwait(false); } catch (Exception) { /* Shares answers 0 until a connection settles */ }
        });
        // Made after the client was already connected: what a connection would have started, now.
        if (client.IsConnected) OnConnected(client, EventArgs.Empty);
    }

    /// <summary>
    /// The folder name for a server on the network (<c>remote</c>): the pinned certificate fingerprint in lower-case hex, so a client paired with another server
    /// starts another replica rather than mixing two servers' rows. The <c>local</c> mode's is <see cref="LocalServer"/>.
    /// </summary>
    public static string ServerIdOf(byte[]? fingerprint) => fingerprint is { Length: > 0 } pin ? Convert.ToHexStringLower(pin) : UnpinnedServer;

    /// <summary>
    /// Keys whose value changed because of the server (another device's write, or a snapshot); raised after the replica holds them, off the caller's thread, with
    /// the moment what brought them arrived, so a consumer can say how long the whole way to the screen took (<c>shared list applied</c>).
    /// </summary>
    public event EventHandler<SharedRowsChange>? RemoteChanged;

    // ---- IPrivateStateStore: the replica ----

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sealedValue = await Task.Run(() =>
            {
                using var connection = Open();
                return Scalar(connection, null, "SELECT payload FROM rows WHERE key = $key AND payload IS NOT NULL", ("$key", key)) as byte[];
            }, cancellationToken).ConfigureAwait(false);
            return sealedValue is null ? null : await protector.UnprotectAsync(sealedValue).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken = default) => WriteAsync(key, value, keepPlace: false, null, cancellationToken);

    public Task WriteInPlaceAsync(string key, byte[] value, CancellationToken cancellationToken = default) => WriteAsync(key, value, keepPlace: true, null, cancellationToken);

    /// <summary>A change to a compare-and-set row its follower holds, tagged with the newest change it holds (<see cref="ISharedRows"/>).</summary>
    public Task WriteAsync(string key, byte[] value, long tag, CancellationToken cancellationToken = default) => WriteAsync(key, value, keepPlace: false, tag, cancellationToken);

    /// <summary>
    /// Reads the compare-and-set row <paramref name="key"/> for what will hold it in memory, which from then on is told of the server's rows and of its own
    /// writes the server took. Both under the gate the worker takes a server row under: a row taken before is in what is read, and one taken after is handed to
    /// the follower, so the rev a write is conditional on (the replica's) is always the one the follower's memory was made from.
    /// </summary>
    public async Task<byte[]?> FollowAsync(string key, ISharedRowFollower follower, CancellationToken cancellationToken = default)
    {
        byte[]? sealedValue;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            followers[key] = follower;
            sealedValue = await Task.Run(() =>
            {
                using var connection = Open();
                return Scalar(connection, null, "SELECT payload FROM rows WHERE key = $key AND payload IS NOT NULL", ("$key", key)) as byte[];
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        return sealedValue is null ? null : await protector.UnprotectAsync(sealedValue).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the server keeps the row's category now: the version of the connection when it is settled, otherwise the last one heard (0 before any, and 1 for a
    /// server that keeps no shared state). Read without waiting, for a follower deciding whether to keep a change to make again.
    /// </summary>
    public bool Shares(string key, string profile) =>
        // A category with no fixed row shares every key under its prefix only for the drafts, the saved messages and the avatars; the others
        // are one row each, found by their key under the profile.
        SharedCategories.Match(key, profile) is { } category && (category.Row is not null || SharedCategories.SharesEveryKey(category))
        && category.Since <= ServerVersionNow().Version;

    public int SharedValueLimit => SharedLimits.ValueBytes;

    /// <summary>
    /// What this replica knows of its server's version without waiting: <c>Settling</c> while a connection is up whose first sync is not applied yet (its version
    /// is not the one the rows held here belong to), and while no connection has settled during this run at all (the rows are last run's); otherwise the
    /// connection's version once settled, or the last one heard when there is no connection (0 before any, 1 for a server that keeps no shared state).
    /// </summary>
    internal (bool Settling, int Version) ServerVersionNow()
    {
        if (client.IsConnected && client.Version is { } connected)
        {
            // A connection this replica has not been told of yet (OnConnected is a moment behind the client) is as unsettled as one it has: the last connection's
            // word is not this one's.
            if (client.ConnectionNumber != Volatile.Read(ref connectionSeen) || Volatile.Read(ref settled) != Volatile.Read(ref connections))
                return (true, Math.Max(0, Volatile.Read(ref hostProtocol)));
            return (false, hostLacks ? ProtocolVersion.Minimum : connected);
        }
        // No connection: after one settled this run, the last version heard is this run's, and the rows are the server's as of then; before any has, they are last
        // run's, which a server that has been changed since (another device's private reading turned on) may no longer hold.
        return (Volatile.Read(ref everSettled) == 0, Math.Max(0, Volatile.Read(ref hostProtocol)));
    }

    /// <summary>
    /// <see cref="ServerVersionNow"/> with the last version heard read from the replica's file when none has been this run (<c>ServerProtocolAsync</c> reads it too).
    /// What a choice shared with the other devices (SharedPrivateReading) decides from: it must know whether the server keeps it and whether the rows are current.
    /// </summary>
    internal async ValueTask<(bool Settling, int Version)> ServerViewAsync(CancellationToken cancellationToken = default)
    {
        await KnownServerProtocolAsync(cancellationToken).ConfigureAwait(false);
        return ServerVersionNow();
    }

    /// <summary>
    /// Raised, off the caller's thread, when what <see cref="ServerVersionNow"/> says may have changed: a connection began (settling again) or settled. A
    /// listener recomputes from it; it carries nothing.
    /// </summary>
    internal event EventHandler? ServerKnowledgeChanged;

    /// <summary>The rows a watcher is told of the moment the replica takes them, before its transaction commits and before any await: a boolean row's value, none for a row that is gone.</summary>
    internal sealed record RowWatch(HashSet<string> Keys, Action<string, bool?, long, string?> Told);

    /// <summary>Who is told when a row of its keys is taken from the server (or goes with a snapshot). One watcher: the private reading choice (SharedPrivateReading).</summary>
    internal volatile RowWatch? Watched;

    // A boolean row's value as the private reading choice reads it: no row, a row that is not a boolean and one that cannot be read are all private.
    private static bool IsPrivateValue(byte[]? plain)
    {
        try { return plain is null || (JsonSerializer.Deserialize<bool?>(plain) ?? true) != false; }
        catch (JsonException) { return true; }
    }

    /// <summary>
    /// Of these watched keys, those a write of this device's is (or was, until the server's row is read back) pending for, whose newest rev passed over said private or was a delete,
    /// with that rev. The row the replica holds for such a key is this device's own words at an older rev; what the server said is this, and survives a restart.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, long>> PassedOverPrivateAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = Open();
                var found = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var key in keys)
                    if (Scalar(connection, null, "SELECT rev FROM overtaken WHERE key = $key AND priv = 1", ("$key", key)) is long rev) found[key] = rev;
                return (IReadOnlyDictionary<string, long>)found;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // `epoch`: the space the rev belongs to (a snapshot's own, the replica's otherwise), so the watcher keeps a rev and its epoch as one condition.
    private static void NoteWatched(RowWatch watching, string key, byte[]? plain, long rev, string? epoch)
    {
        bool? value;
        try { value = plain is null ? null : JsonSerializer.Deserialize<bool?>(plain) ?? true; }
        catch (JsonException) { value = true; }
        try { watching.Told(key, value, rev, epoch); }
        catch (Exception) { /* a watcher that fails must not stop the replica */ }
    }

    private void KnowledgeChanged()
    {
        try { ServerKnowledgeChanged?.Invoke(this, EventArgs.Empty); } catch (Exception) { /* a listener that fails must not stop the replica */ }
    }

    /// <summary>
    /// A followed row this device could not get to the server, raised off the caller's thread: its write lost <see cref="ReapplyLimit"/> times more and waits
    /// (<see cref="SharedRowTold.Held"/>), or another device's row replaced one this device held before sharing (<see cref="SharedRowTold.Replaced"/>).
    /// </summary>
    public event EventHandler<SharedRowTold>? Told;

    /// <summary>How long a row that gave up waits before its write goes again, doubling each time it gives up again this connection.</summary>
    public TimeSpan ParkFirst { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan ParkLongest { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A write of a last-writer-wins row that goes to the server only while the row is still at <paramref name="ifRev"/> there (0: while there is none). The replica
    /// holds the value at once; if the server's row has moved on, the server's is taken instead and this is dropped.
    /// </summary>
    public Task WriteIfAsync(string key, byte[] value, long ifRev, string? epoch, CancellationToken cancellationToken = default) =>
        WriteAsync(key, value, keepPlace: false, null, cancellationToken, ifRev, epoch);

    /// <summary>
    /// A write of a last-writer-wins row this device made and the server has not taken yet (the replica holds no rev for it), made from what that write says. It goes
    /// only while the server's row is as that write leaves it: when a write of the row is on its way (sent, no answer yet) it waits for the answer and is made against
    /// the rev the answer gives; when none is (the write has not gone, and is folded into this one), against no row, which is create only. Never unconditionally,
    /// or it would undo what another device wrote between the two. One that finds the server's row moved on is dropped and the server's row taken.
    /// </summary>
    public Task WriteAfterPendingAsync(string key, byte[] value, CancellationToken cancellationToken = default) =>
        WriteAsync(key, value, keepPlace: false, null, cancellationToken, AfterPending);

    // The condition of a write that waits for the rev of the write ahead of it (stored in `pending.if_rev`, never sent: NextPendingAsync passes it over).
    private const long AfterPending = -1;

    /// <summary>
    /// The value and the rev the replica holds for a key (0 for no rev from the server), with the epoch the rev belongs to, read together under the gate: a snapshot of another
    /// space moves the replica's epoch on, and a rev kept from before it must not be taken for one of the new space.
    /// </summary>
    public async Task<(byte[]? Value, long Rev, string? Epoch)> ReadWithRevAsync(string key, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var found = await Task.Run(() =>
            {
                using var connection = Open();
                var sealedValue = Scalar(connection, null, "SELECT payload FROM rows WHERE key = $key AND payload IS NOT NULL", ("$key", key)) as byte[];
                var rev = Scalar(connection, null, "SELECT rev FROM rows WHERE key = $key", ("$key", key)) is long held ? held : 0;
                return (sealedValue, rev, Scalar(connection, null, "SELECT epoch FROM meta WHERE id = 1") as string);
            }, cancellationToken).ConfigureAwait(false);
            return (found.sealedValue is null ? null : await protector.UnprotectAsync(found.sealedValue).ConfigureAwait(false), found.rev, found.Item3);
        }
        finally { gate.Release(); }
    }

    private async Task WriteAsync(string key, byte[] value, bool keepPlace, long? tag, CancellationToken cancellationToken, long? ifRev = null, string? ifEpoch = null)
    {
        if (value.Length > MaxValue) throw new InvalidDataException("Local snapshot exceeds the storage limit.");
        var category = await SharedAsync(key, value, cancellationToken).ConfigureAwait(false);
        var shared = category is not null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var held = Stopwatch.GetTimestamp();
        try
        {
            var sealedValue = await protector.ProtectAsync(value).ConfigureAwait(false);
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction, keepPlace
                        ? "INSERT INTO rows(key, payload, rev, updated) VALUES($key, $payload, 0, $now) ON CONFLICT(key) DO UPDATE SET payload = excluded.payload"
                        : "INSERT INTO rows(key, payload, rev, updated) VALUES($key, $payload, 0, $now) ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, updated = excluded.updated",
                    ("$key", key), ("$payload", sealedValue), ("$now", Now()));
                if (shared) Queue(connection, transaction, key, sealedValue, category!, tag, ifRev, ifEpoch);
                // As SqlitePrivateStateStore: counted first, so the trim reads `updated` only when there is something to trim.
                if (Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM rows WHERE key LIKE 'cache:%'")) > CacheLimit)
                    Execute(connection, transaction,
                        $"DELETE FROM rows WHERE key LIKE 'cache:%' AND key NOT IN (SELECT key FROM rows WHERE key LIKE 'cache:%' ORDER BY updated DESC LIMIT {CacheLimit})");
                transaction.Commit();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Counted with state.db's writes, so the responsiveness log reads the same in every mode.
            SqlitePrivateStateStore.CountWrite(Stopwatch.GetTimestamp() - held);
            gate.Release();
        }
        if (shared) Want(ref flushWanted);
    }

    public async Task<bool> AddAsync(string key, byte[] value, CancellationToken cancellationToken = default)
    {
        if (value.Length > MaxValue) throw new InvalidDataException("Local snapshot exceeds the storage limit.");
        var category = await SharedAsync(key, value, cancellationToken).ConfigureAwait(false);
        var shared = category is not null;
        bool added;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sealedValue = await protector.ProtectAsync(value).ConfigureAwait(false);
            added = await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                // Only where nothing is held: no row, or a delete that has not been forgotten yet.
                var stored = Execute(connection, transaction,
                    "INSERT INTO rows(key, payload, rev, updated) VALUES($key, $payload, 0, $now) ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, updated = excluded.updated WHERE rows.payload IS NULL",
                    ("$key", key), ("$payload", sealedValue), ("$now", Now())) == 1;
                if (stored && shared) Queue(connection, transaction, key, sealedValue, category!, null);
                transaction.Commit();
                return stored;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        if (added && shared) Want(ref flushWanted);
        return added;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var category = await SharedAsync(key, null, cancellationToken).ConfigureAwait(false);
        var shared = category is not null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                // A row the server has a rev for keeps it, as a delete, so an older value of it arriving later is known to be older.
                if (shared) Execute(connection, transaction, "UPDATE rows SET payload = NULL WHERE key = $key AND rev > 0", ("$key", key));
                Execute(connection, transaction, shared ? "DELETE FROM rows WHERE key = $key AND rev = 0" : "DELETE FROM rows WHERE key = $key", ("$key", key));
                if (shared) Queue(connection, transaction, key, null, category!, null);
                transaction.Commit();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        if (shared) Want(ref flushWanted);
    }

    public async Task<IReadOnlyList<string>> KeysAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run<IReadOnlyList<string>>(() =>
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT key FROM rows WHERE substr(key, 1, $length) = $prefix AND payload IS NOT NULL ORDER BY key";
                command.Parameters.AddWithValue("$length", prefix.Length); command.Parameters.AddWithValue("$prefix", prefix);
                using var reader = command.ExecuteReader();
                var keys = new List<string>();
                while (reader.Read()) keys.Add(reader.GetString(0));
                return keys;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Every live row whose key starts with <paramref name="prefix"/>, with its value, in one read: what the stores that were files load into memory once
    /// (<see cref="ReplicaRows"/>), rather than a read per key for every conversation the list draws.
    /// </summary>
    internal async Task<IReadOnlyList<(string Key, byte[] Value)>> ReadPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        List<(string Key, byte[] Sealed)> found;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            found = await Task.Run(() =>
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT key, payload FROM rows WHERE substr(key, 1, $length) = $prefix AND payload IS NOT NULL";
                command.Parameters.AddWithValue("$length", prefix.Length); command.Parameters.AddWithValue("$prefix", prefix);
                using var reader = command.ExecuteReader();
                var rows = new List<(string, byte[])>();
                while (reader.Read()) rows.Add((reader.GetString(0), (byte[])reader[1]));
                return rows;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        var values = new List<(string, byte[])>(found.Count);
        foreach (var (key, sealedValue) in found) values.Add((key, await protector.UnprotectAsync(sealedValue).ConfigureAwait(false)));
        return values;
    }

    /// <summary>
    /// <see cref="ReadPrefixAsync"/> with the rev each row has from the server (0 for a row written here and not yet taken back): the scheduled sends, which a
    /// command is made against the rev of (<see cref="ScheduledServerLink"/>).
    /// </summary>
    internal async Task<IReadOnlyList<(string Key, byte[] Value, long Rev)>> ReadPrefixWithRevAsync(string prefix, CancellationToken cancellationToken = default)
    {
        List<(string Key, byte[] Sealed, long Rev)> found;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            found = await Task.Run(() =>
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT key, payload, rev FROM rows WHERE substr(key, 1, $length) = $prefix AND payload IS NOT NULL";
                command.Parameters.AddWithValue("$length", prefix.Length); command.Parameters.AddWithValue("$prefix", prefix);
                using var reader = command.ExecuteReader();
                var rows = new List<(string, byte[], long)>();
                while (reader.Read()) rows.Add((reader.GetString(0), (byte[])reader[1], reader.IsDBNull(2) ? 0 : reader.GetInt64(2)));
                return rows;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        var values = new List<(string, byte[], long)>(found.Count);
        foreach (var (key, sealedValue, rev) in found) values.Add((key, await protector.UnprotectAsync(sealedValue).ConfigureAwait(false), rev));
        return values;
    }

    /// <summary>
    /// The protocol version of this replica's server: the connection's while there is one (the first version when it turned out to keep no shared state),
    /// otherwise the last one heard, which the replica keeps so that a launch draws from the same place the last connection did before it connects again;
    /// 0 before any server was heard. The stores that were files go by it (<see cref="ReplicaRows"/>): below <see cref="ProtocolVersion.SharedPreferences"/>
    /// they stay on their files, exactly as before.
    /// </summary>
    internal async ValueTask<int> ServerProtocolAsync(CancellationToken cancellationToken = default)
    {
        // The connection's version counts only once it is settled: its first sync applied (or it keeps no shared state). Before that the replica may not
        // hold the rows yet, and the last version heard is what the rows here belong to.
        if (client.IsConnected && client.Version is { } version && Volatile.Read(ref settled) == Volatile.Read(ref connections))
            return hostLacks ? ProtocolVersion.Minimum : version;
        return await KnownServerProtocolAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<int> KnownServerProtocolAsync(CancellationToken cancellationToken)
    {
        var known = Volatile.Read(ref hostProtocol);
        if (known >= 0) return known;
        known = (await MetaAsync(cancellationToken).ConfigureAwait(false)).HostProtocol ?? 0;
        Interlocked.CompareExchange(ref hostProtocol, known, -1);
        return Volatile.Read(ref hostProtocol);
    }

    /// <summary>
    /// What <see cref="ServerProtocolAsync"/> says changed (a connection settled on another version than the last one heard): raised off the caller's thread,
    /// so what reads by it (the snooze reminders, ReplicaChatSnoozes) reads again.
    /// </summary>
    public event EventHandler? ServerProtocolChanged;

    // The worker, once connection `number` is settled: its version is what ServerProtocolAsync answers from now, and what the replica keeps for the next launch.
    private async Task SettleAsync(int number, int version, CancellationToken token)
    {
        var before = await KnownServerProtocolAsync(token).ConfigureAwait(false);
        await KeepHostProtocolAsync(version, token).ConfigureAwait(false);
        Volatile.Write(ref settled, number);
        Volatile.Write(ref everSettled, 1);
        KnowledgeChanged();
        if (before != version)
            try { ServerProtocolChanged?.Invoke(this, EventArgs.Empty); } catch (Exception) { /* a listener that fails must not stop the replica */ }
    }

    private async Task KeepHostProtocolAsync(int version, CancellationToken token)
    {
        Volatile.Write(ref hostProtocol, version);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                // The highest version this replica has been on, before the version it keeps as the last heard is replaced (a replica made before this was kept starts it
                // from that one).
                Execute(connection, transaction, "UPDATE meta SET peak_protocol = MAX(COALESCE(peak_protocol, 0), COALESCE(host_protocol, 0), $version)", ("$version", version));
                Execute(connection, transaction, "UPDATE meta SET host_protocol = $version WHERE host_protocol IS NULL OR host_protocol <> $version", ("$version", version));
                // (All under the replica's gate, like ForgetPeakAsync and the first read.)
                if (Volatile.Read(ref peakProtocol) is >= 0 and var peak && peak < version) Volatile.Write(ref peakProtocol, version);
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private int peakProtocol = -1;

    /// <summary>
    /// The highest version this replica has been on (0 when none, or since <see cref="ForgetPeakAsync"/>): whether this device has ever shared state with a server
    /// that keeps the private reading choice, which it cannot know below version 7.
    /// </summary>
    internal async ValueTask<int> PeakProtocolAsync(CancellationToken cancellationToken = default)
    {
        var known = Volatile.Read(ref peakProtocol);
        if (known >= 0) return known;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            known = await Task.Run(() =>
            {
                using var connection = Open();
                return Convert.ToInt32(Scalar(connection, null, "SELECT MAX(COALESCE(peak_protocol, 0), COALESCE(host_protocol, 0)) FROM meta WHERE id = 1"));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        Interlocked.CompareExchange(ref peakProtocol, known, -1);
        return Volatile.Read(ref peakProtocol);
    }

    /// <summary>The peak is spent: a device that has been made private for every service below version 7 starts from there, until a connection at 7 or later.</summary>
    internal async Task ForgetPeakAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction, "UPDATE meta SET peak_protocol = 0");
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref peakProtocol, 0);
        }
        finally { gate.Release(); }
    }

    /// <summary>Rewrites the replica without its free pages, inside the store's own gate (Settings > Troubleshooting > Storage), as state.db compacts.</summary>
    public async Task<StoreCompaction> CompactAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => StoreCompaction.Run(path, Open, "VACUUM"), CancellationToken.None).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private Task<string?> ProfileAsync(CancellationToken cancellationToken) => NotificationIdentity.ProfileAsync(tokens, cancellationToken);

    // The category a write of this key goes to the server under, or null when it stays here. Only a category that holds a shared row costs the profile; a value
    // that is not JSON, or too large for the server, stays on this PC (it would only ever be refused), and says so once per write.
    private async Task<SharedCategory?> SharedAsync(string key, byte[]? value, CancellationToken cancellationToken)
    {
        if (!SharedCategories.MayBeShared(key)) return null;
        if (SharedCategories.Match(key, await ProfileAsync(cancellationToken).ConfigureAwait(false)) is not { } category) return null;
        // The server's own rows (the scheduled sends) are never sent: a client changes them only through the server's typed methods, and the server refuses a put anyway.
        if (category.HostOwned) return null;
        if (value is null) return category;
        if (value.Length > SharedLimits.ValueBytes)
        {
            log?.Invoke("shared write kept reason=too_large");
            return null;
        }
        try { JsonSerializer.Deserialize<JsonElement>(value); }
        catch (JsonException)
        {
            log?.Invoke("shared write kept reason=not_json");
            return null;
        }
        return category;
    }

    // In the write's own transaction: the batch and its operation id are on disk with the value, or neither is. The writes of the key not sent yet are folded
    // into this one here, so the queue holds at most one unsent write a key however long it waits (a pinned order held for a server of an older version is
    // written at every pin); one already sent stays, to be sent again as it was. A compare-and-set row's write is made from what the replica holds, whose rev
    // is the server's row this device last took (its own writes keep it): that is its condition, and 0 (create only) where the server has given none.
    // A last-writer-wins row can be written with a condition of the caller's (<paramref name="ifRevOverride"/>): the private reading choice's less private answer
    // is made against the rev it was chosen against, so a stale one never lands over a newer write of another device's (SharedPrivateReading).
    private static void Queue(SqliteConnection connection, SqliteTransaction transaction, string key, byte[]? sealedValue, SharedCategory category, long? tag,
        long? ifRevOverride = null, string? ifEpochOverride = null)
    {
        long? ifRev = category.Policy == SharedConflictPolicy.CompareAndSet
            ? Scalar(connection, transaction, "SELECT rev FROM rows WHERE key = $key", ("$key", key)) is long rev ? rev : 0
            : ifRevOverride;
        // After a write on its way: its answer's rev becomes this one's condition (SettleAsync); with none on its way, create only.
        if (ifRev == AfterPending && Scalar(connection, transaction, "SELECT 1 FROM pending WHERE key = $key AND sent = 1 LIMIT 1", ("$key", key)) is null) ifRev = 0;
        // A write conditional on a rev says the writer knew every row up to it: what was passed over at or before that rev is no longer news (a deliberate "not private").
        if (ifRev is >= 0 and var knew) Execute(connection, transaction, "UPDATE overtaken SET priv = NULL WHERE key = $key AND rev <= $rev", ("$key", key), ("$rev", knew));
        Execute(connection, transaction, "DELETE FROM pending WHERE key = $key AND sent = 0", ("$key", key));
        // The space the condition's rev was read in. A caller that read the rev earlier names that epoch, and it is kept as it is even when the replica has moved to another
        // space since (a snapshot between the read and this write): the server then answers the write as lost, where re-tagging it with the replica's epoch would let a rev of the old
        // space match a different row of the new one. A compare-and-set row's rev is the replica's own at this moment, so is its epoch; a write that waits for another's rev gets
        // both when that one is answered (SettleAsync).
        var epoch = ifRev is >= 0 ? ifEpochOverride ?? Scalar(connection, transaction, "SELECT epoch FROM meta WHERE id = 1") as string : null;
        Execute(connection, transaction, "INSERT INTO pending(client_op_id, key, payload, sent, if_rev, tag, epoch) VALUES($id, $key, $payload, 0, $ifRev, $tag, $epoch)",
            ("$id", ProtocolClient.NewOperationId()), ("$key", key), ("$payload", (object?)sealedValue ?? DBNull.Value), ("$ifRev", (object?)ifRev ?? DBNull.Value),
            ("$tag", (object?)tag ?? DBNull.Value), ("$epoch", (object?)epoch ?? DBNull.Value));
    }

    // ---- The connection's side ----

    private void OnConnected(object? sender, EventArgs e)
    {
        // A new connection: its version counts only once settled (ServerProtocolAsync).
        // The count first: a reader that sees the new connection number before connectionSeen has caught up sees an unsettled connection either way.
        Interlocked.Increment(ref connections);
        Volatile.Write(ref connectionSeen, client.ConnectionNumber);
        KnowledgeChanged();
        // A compare-and-set row that gave up tries again, as many times again.
        losses.Clear();
        parked.Clear();
        parkings.Clear();
        Interlocked.Exchange(ref connected, 1);
        Signal();
    }

    private void OnShared(object? sender, SharedChange change)
    {
        arrivals.Enqueue(new Arrival(change, Stopwatch.GetTimestamp()));
        Signal();
    }

    private void OnResynced(object? sender, EventArgs e) => Want(ref syncWanted);

    private void Want(ref int flag)
    {
        Interlocked.Exchange(ref flag, 1);
        Signal();
    }

    private void Signal()
    {
        try { wake.Release(); } catch (ObjectDisposedException) { }
    }

    private bool Available => !hostLacks && client.IsConnected && client.Version >= ProtocolVersion.SharedState;

    // One worker, so the replica's changes from the server are applied in the order they came and no two syncs or flushes overlap.
    private async Task RunAsync()
    {
        var token = stop.Token;
        while (!token.IsCancellationRequested)
        {
            try { await wake.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try
            {
                if (Interlocked.Exchange(ref connected, 0) == 1) await ConnectedAsync(token).ConfigureAwait(false);
                while (arrivals.TryDequeue(out var arrival)) await ArrivedAsync(arrival, token).ConfigureAwait(false);
                if (!Available) continue;
                if (Interlocked.Exchange(ref syncWanted, 0) == 1) await SyncAsync(token).ConfigureAwait(false);
                if (Interlocked.Exchange(ref flushWanted, 0) == 1) await FlushAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            // A replica that cannot be read or written now is tried again at the next connection; the reads the window makes fail on their own.
            catch (Exception failure) { log?.Invoke($"shared replica error={failure.GetType().Name}"); }
        }
    }

    private async Task ConnectedAsync(CancellationToken token)
    {
        var number = Volatile.Read(ref connections);
        hostLacks = false;
        if (!client.IsConnected) return;
        if (client.Version is { } version && version < ProtocolVersion.SharedState)
        {
            // Everything stays in the replica, as it stayed on this PC before; what is pending waits for a server that keeps it.
            hostLacks = true;
            log?.Invoke($"shared state unavailable host_protocol={version}");
            await SettleAsync(number, version, token).ConfigureAwait(false);
            return;
        }
        log?.Invoke($"shared pending n={await PendingCountAsync(token).ConfigureAwait(false)}");
        Interlocked.Exchange(ref syncWanted, 1);
        Interlocked.Exchange(ref flushWanted, 1);
    }

    // ---- Applying what the server says ----

    private async Task ArrivedAsync(Arrival arrival, CancellationToken token)
    {
        var change = arrival.Change;
        var meta = await MetaAsync(token).ConfigureAwait(false);
        var profile = await ProfileAsync(token).ConfigureAwait(false);
        if (!string.Equals(change.Epoch, meta.Epoch, StringComparison.Ordinal) || !string.Equals(profile, meta.Profile, StringComparison.Ordinal))
        {
            // Another space, none read yet, or keys filed under another profile now: a sync settles which, and a snapshot follows if it must.
            Want(ref syncWanted);
            return;
        }
        // Already held, whole: the feed resumed from an earlier position, or the sync was faster.
        if (change.Rev <= meta.Rev) return;
        IReadOnlyList<SharedRow> rows;
        try { rows = await CompleteAsync(change.Rows, profile, reset: false, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // A value that came by its size could not be read: the sync that follows brings the whole commit.
            Want(ref syncWanted);
            return;
        }
        var contiguous = change.Rev == meta.Rev + 1 && !meta.Snapshot;
        await ApplyAsync(rows, contiguous ? meta with { Rev = change.Rev } : null, reset: false, profile, token, arrived: arrival.At).ConfigureAwait(false);
        if (!contiguous)
        {
            // A commit ahead of a gap: its rows are newer than anything held for them, but the position waits for the sync to fill the gap.
            InterlockedMax(ref seen, change.Rev);
            Want(ref syncWanted);
        }
        log?.Invoke($"shared applied rows={rows.Count} ms={(long)Stopwatch.GetElapsedTime(arrival.At).TotalMilliseconds}");
    }

    private async Task SyncAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        int rows = 0;
        var reset = false;
        var number = Volatile.Read(ref connections);
        // The version this connection speaks: a snapshot replaces only the rows of the categories it knows, and keeps the rest (a newer category's
        // rows, held here for a server that will take them) as they are.
        var version = client.Version ?? ProtocolVersion.Current;
        try
        {
            var profile = await ProfileAsync(token).ConfigureAwait(false);
            for (var pass = 0; pass < 3; pass++)
            {
                while (true)
                {
                    var meta = await MetaAsync(token).ConfigureAwait(false);
                    // Keys filed under another profile now: what was held is not what is shared any more, so it starts from a snapshot.
                    var same = meta.Epoch is not null && string.Equals(profile, meta.Profile, StringComparison.Ordinal);
                    var page = await remote.SyncAsync(new StateSyncParams(same ? new SharedPosition(meta.Epoch!, meta.Rev) : null, null, same && meta.Snapshot),
                        cancellationToken: token).ConfigureAwait(false);
                    var heard = Stopwatch.GetTimestamp();
                    var complete = await CompleteAsync(page.Rows, profile, page.Reset, token).ConfigureAwait(false);
                    // A rev an event showed before a snapshot may be from another space.
                    if (page.Reset) Interlocked.Exchange(ref seen, 0);
                    // A snapshot that goes on over more pages is marked, so a stop between them goes on from the page it reached rather than starting again.
                    await ApplyAsync(complete, new Meta(page.Epoch, page.Rev, page.More && (page.Reset || meta.Snapshot), profile, meta.HostProtocol), page.Reset, profile, token, version,
                        heard).ConfigureAwait(false);
                    rows += complete.Count;
                    reset |= page.Reset;
                    if (!page.More) break;
                }
                // An event showed a commit this sync had not reached when it read its last page.
                if (Volatile.Read(ref seen) <= (await MetaAsync(token).ConfigureAwait(false)).Rev) break;
            }
            // Rows whose own write was overtaken or refused, left from a connection that ended before the server's row was read.
            await ResolveAsync(token).ConfigureAwait(false);
            await OfferLocalAsync(profile, version, token).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            hostLacks = true;
            log?.Invoke($"shared state unavailable host_protocol={client.Version} reason=not_supported");
            await SettleAsync(number, ProtocolVersion.Minimum, token).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // What was applied stays; the rest comes with the next connection's sync, or the next resync's. The connection is not settled: the stores that
            // were files keep drawing from where the last settled one left them.
            log?.Invoke("shared sync failed reason=" + (ex is BackendException { Failure: BackendFailure.Unavailable } ? "unavailable" : "other"));
            return;
        }
        // Its rows are in the replica now: from here the stores that were files go by this connection's version.
        if (Volatile.Read(ref settled) != number) await SettleAsync(number, version, token).ConfigureAwait(false);
        log?.Invoke($"shared sync reset={(reset ? "true" : "false")} rows={rows} ms={clock.ElapsedMilliseconds}");
    }

    // The rows this client shares, each with its value: one that came by its size is read with state.get, a few at a time, until each has come or is gone.
    // One the replica would not take anyway (it holds that rev already: a writer hearing its own commit) is never read, and one a write here is pending for
    // keeps coming by its size: only its rev is noted.
    private async Task<IReadOnlyList<SharedRow>> CompleteAsync(IReadOnlyList<SharedRow> rows, string? profile, bool reset, CancellationToken token)
    {
        rows = rows.Where(row => SharedCategories.Match(row.Key, profile) is not null).ToList();
        if (!rows.Any(row => row.SizeOnly)) return rows;
        var (pending, held) = await KnownAsync(rows.Where(row => row.SizeOnly).ToList(), reset, token).ConfigureAwait(false);
        rows = rows.Where(row => !(row.SizeOnly && held.Contains(row.Key))).ToList();
        var read = new Dictionary<string, SharedRow>(StringComparer.Ordinal);
        var wanted = rows.Where(row => row.SizeOnly && !pending.Contains(row.Key)).Select(row => row.Key).Distinct(StringComparer.Ordinal).ToList();
        while (wanted.Count > 0)
        {
            var asked = wanted.Take(SharedLimits.GetKeys).ToArray();
            var answer = await remote.GetAsync(new StateGetParams(asked), cancellationToken: token).ConfigureAwait(false);
            var progressed = false;
            foreach (var row in answer.Rows.Where(row => !row.SizeOnly))
            {
                read[row.Key] = row;
                progressed |= wanted.Remove(row.Key);
            }
            // A key the server no longer holds at all: nothing to read.
            foreach (var key in asked.Where(key => answer.Rows.All(row => row.Key != key))) progressed |= wanted.Remove(key);
            if (!progressed) throw new BackendException(BackendFailure.InvalidResponse, ProtocolClient.UnreadableSentence);
        }
        // What state.get read is at least as new as what the page named; a key it found gone is left alone.
        return rows.Select(row => row.SizeOnly && read.TryGetValue(row.Key, out var full) ? full : row)
            .Where(row => !row.SizeOnly || pending.Contains(row.Key)).ToList();
    }

    // Of these keys: which a write here is pending for, and which the replica holds at the row's rev or later. After a snapshot the revs held here are from
    // before it, so none counts as held then.
    private async Task<(HashSet<string> Pending, HashSet<string> Held)> KnownAsync(IReadOnlyList<SharedRow> rows, bool reset, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = Open();
                var pending = new HashSet<string>(StringComparer.Ordinal);
                var held = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    if (Scalar(connection, null, "SELECT 1 FROM pending WHERE key = $key LIMIT 1", ("$key", row.Key)) is not null) pending.Add(row.Key);
                    else if (!reset && Scalar(connection, null, "SELECT rev FROM rows WHERE key = $key", ("$key", row.Key)) is long rev && rev >= row.Rev) held.Add(row.Key);
                }
                return (pending, held);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // Rows from the server into the replica, with the new position when there is one, in one transaction. Only rows this client shares under `profile` are
    // taken. A snapshot first drops the shared rows held from before of the categories `version` knows (the snapshot's connection's), but for those a write
    // here is still pending for: a newer category's rows were never the server's to give, so they are not its to drop.
    // `arrived`: when what brought the rows came (the event, the sync page); now when not given (a put's answer, a row read back).
    private async Task ApplyAsync(IReadOnlyList<SharedRow> rows, Meta? position, bool reset, string? profile, CancellationToken token, int version = ProtocolVersion.Current,
        long? arrived = null)
    {
        rows = rows.Where(row => SharedCategories.Match(row.Key, profile) is not null).ToList();
        // The private reading choice's rows under this profile, for which what the server said is kept while a write of this device's is pending.
        var privateKeys = profile is null ? [] : SharedCategories.PrivateReadingWords.Select(word => SharedCategories.PrivateReadingKey(profile, word)).ToHashSet(StringComparer.Ordinal);
        var plainValues = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var sealedValues = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row is { Deleted: false, Value: { } value })
            {
                plainValues[row.Key] = System.Text.Encoding.UTF8.GetBytes(value.GetRawText());
                sealedValues[row.Key] = await protector.ProtectAsync(plainValues[row.Key]).ConfigureAwait(false);
            }
        List<string> changed;
        // The compare-and-set rows a follower holds whose server row the replica took here, with that row (null: none), handed to the follower once committed.
        var followed = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        var unparked = new List<string>();
        // Compare-and-set rows held before they were shared, which a snapshot keeps, and how many of them another device's row replaced here.
        var localOnly = new HashSet<string>(StringComparer.Ordinal);
        var replaced = 0;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            changed = await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                var changed = new List<string>();
                // The space these rows are of: the position's when it moves the replica on (a snapshot of a new space), otherwise the replica's.
                var epochNow = position?.Epoch ?? Scalar(connection, transaction, "SELECT epoch FROM meta WHERE id = 1") as string;
                if (reset)
                {
                    // Only the rows this client shares: a per-device row is never the server's to replace.
                    var prefixes = SharedCategories.All.Select(category => category.Category + ":").Distinct().ToArray();
                    foreach (var (key, live) in Rows(connection, transaction, "SELECT key, payload IS NOT NULL FROM rows WHERE key NOT IN (SELECT key FROM pending)"))
                    {
                        if (!prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)) || SharedCategories.Match(key, profile, version) is not { } category) continue;
                        // A host-owned row (the scheduled and unsettled sends) is dropped only if it came from a server (it has a rev): the in-process app's whole-list rows
                        // share those prefixes under the key of whichever profile wrote them, which no category can tell by its key, and a snapshot is not theirs to drop.
                        if (category.HostOwned && Convert.ToInt64(Scalar(connection, transaction, "SELECT rev FROM rows WHERE key = $key", ("$key", key))) == 0) continue;
                        // A compare-and-set row this replica held before it was shared (no rev from the server, nothing queued) is kept, not dropped: the sync
                        // offers it to the server when it ends (OfferLocalAsync).
                        if (live && category.Policy == SharedConflictPolicy.CompareAndSet
                            && Convert.ToInt64(Scalar(connection, transaction, "SELECT rev FROM rows WHERE key = $key", ("$key", key))) == 0)
                        {
                            localOnly.Add(key);
                            continue;
                        }
                        Execute(connection, transaction, "DELETE FROM rows WHERE key = $key", ("$key", key));
                        if (live) changed.Add(key);
                        if (live && Watched is { } watchingReset && watchingReset.Keys.Contains(key)) NoteWatched(watchingReset, key, null, 0, epochNow);
                        if (Followed(key, profile)) followed[key] = null;
                    }
                    // Revs and overtaken writes from before the snapshot mean nothing now. A key with a write still pending is noted as overtaken at the
                    // snapshot's own rev: the snapshot holds live rows only, so it says nothing of a key deleted meanwhile, and the pending write's answer may be
                    // an earlier commit's (its answer was lost, and the server answers the repeat from its record). An answer older than the snapshot then has the
                    // server's row read back (SettleAsync, ResolveAsync); a write committed after the snapshot stands. The snapshot's rows raise these as usual.
                    Execute(connection, transaction, "UPDATE rows SET rev = 0 WHERE rev > 0");
                    Execute(connection, transaction, "DELETE FROM overtaken");
                    Execute(connection, transaction, "INSERT INTO overtaken(key, rev) SELECT DISTINCT key, $rev FROM pending WHERE true ON CONFLICT(key) DO NOTHING",
                        ("$rev", position?.Rev ?? long.MaxValue));
                }
                foreach (var row in rows)
                {
                    // A write made here is on its way and will come after this in the server's order: the replica keeps its words, and notes the newest rev
                    // it passed over, which settles what the server's answer means (SettleAsync).
                    if (Scalar(connection, transaction, "SELECT 1 FROM pending WHERE key = $key LIMIT 1", ("$key", row.Key)) is not null)
                    {
                        Execute(connection, transaction, "INSERT INTO overtaken(key, rev) VALUES($key, $rev) ON CONFLICT(key) DO UPDATE SET rev = MAX(rev, excluded.rev)",
                            ("$key", row.Key), ("$rev", row.Rev));
                        // A row parked after losing, and another device has written it since it lost: its write goes again now (made again over this row).
                        if (parked.TryGetValue(row.Key, out var lostTo) && row.Rev > lostTo) unparked.Add(row.Key);
                        // The watcher is told of a row that is newer than the one held all the same: it keeps its words for the pending write, but a move toward private cannot wait for that
                        // write to lose (a receipt can go out in the meantime), and a row not newer than the held one has nothing to tell.
                        // Kept for every private reading row whether or not a watcher is registered yet: a device that starts with its own "off" still pending sees the newer row
                        // in its first sync, before the choice has settled and registered (SharedPrivateReading looks only once the connection has), and the choice reads what
                        // was kept on its first look (PassedOverPrivateAsync).
                        var pendingWatch = Watched;
                        var watchedRow = pendingWatch is not null && pendingWatch.Keys.Contains(row.Key);
                        if ((watchedRow || privateKeys.Contains(row.Key)) && !row.SizeOnly
                            && !(Scalar(connection, transaction, "SELECT rev FROM rows WHERE key = $key", ("$key", row.Key)) is long heldRev && heldRev >= row.Rev))
                        {
                            var plain = row.Deleted ? null : plainValues[row.Key];
                            if (watchedRow) NoteWatched(pendingWatch!, row.Key, plain, row.Rev, epochNow);
                            // And kept, with the rows: a connection that ends (or an app that restarts) before the write is answered must not lose what the server said, for the
                            // sync that follows starts after this rev and the replica's row is still this device's own words.
                            Execute(connection, transaction, "UPDATE overtaken SET priv = $priv WHERE key = $key AND rev = $rev",
                                ("$priv", IsPrivateValue(plain) ? 1 : 0), ("$key", row.Key), ("$rev", row.Rev));
                        }
                        continue;
                    }
                    var held = Scalar(connection, transaction, "SELECT rev FROM rows WHERE key = $key", ("$key", row.Key)) is long rev ? rev : (long?)null;
                    if (held >= row.Rev || row.SizeOnly) continue;
                    // Told before anything is awaited and before the transaction commits, for the rows a watcher asked for (SharedPrivateReading): a move toward private must
                    // not wait for the event that follows, and one that then fails to commit only leaves the watcher private.
                    if (Watched is { } watching && watching.Keys.Contains(row.Key)) NoteWatched(watching, row.Key, row.Deleted ? null : plainValues[row.Key], row.Rev, epochNow);
                    var payload = row.Deleted ? null : sealedValues[row.Key];
                    var had = Scalar(connection, transaction, "SELECT 1 FROM rows WHERE key = $key AND payload IS NOT NULL", ("$key", row.Key)) is not null;
                    // Another device's row over a compare-and-set row this device held before it was shared (no rev from the server, nothing queued; a
                    // snapshot's revs are all 0 by now, so it goes by the rows it kept): this device's words are kept, and said to be.
                    // (The read-here marks are merged by their follower instead, with nothing replaced and nothing said: ReadHereMarks.)
                    if (had && IsCompareAndSet(row.Key, profile) && !(profile is not null && row.Key == SharedCategories.ReadHereKey(profile)) && (reset ? localOnly.Contains(row.Key) : held == 0))
                    {
                        Execute(connection, transaction,
                            "INSERT INTO rows(key, payload, rev, updated) SELECT $copy, payload, 0, $now FROM rows WHERE key = $key " +
                            "ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, updated = excluded.updated",
                            ("$copy", ReplacedPrefix + row.Key), ("$key", row.Key), ("$now", Now()));
                        replaced++;
                    }
                    Execute(connection, transaction,
                        "INSERT INTO rows(key, payload, rev, updated) VALUES($key, $payload, $rev, $now) ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, rev = excluded.rev, updated = excluded.updated",
                        ("$key", row.Key), ("$payload", (object?)payload ?? DBNull.Value), ("$rev", row.Rev), ("$now", Now()));
                    if (payload is not null || had) changed.Add(row.Key);
                    if (Followed(row.Key, profile)) followed[row.Key] = row.Deleted ? null : plainValues[row.Key];
                }
                if (position is not null)
                {
                    Execute(connection, transaction, "UPDATE meta SET epoch = $epoch, rev = $rev, snapshot = $snapshot, profile = $profile",
                        ("$epoch", (object?)position.Epoch ?? DBNull.Value), ("$rev", position.Rev), ("$snapshot", position.Snapshot ? 1 : 0),
                        ("$profile", (object?)position.Profile ?? DBNull.Value));
                    // Deletes the position has passed: nothing older than them can arrive any more (anything at or below the position is ignored).
                    Execute(connection, transaction, "DELETE FROM rows WHERE payload IS NULL AND rev <= $rev AND key NOT IN (SELECT key FROM pending)", ("$rev", position.Rev));
                }
                transaction.Commit();
                return changed;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        foreach (var key in unparked) Unpark(key);
        if (replaced > 0)
        {
            log?.Invoke($"shared local replaced n={replaced}");
            Tell(SharedRowTold.Replaced);
        }
        // What holds the rows in memory takes them before anything is told they changed, and before anything is sent.
        foreach (var (key, value) in followed) await CatchUpAsync(key, value, token).ConfigureAwait(false);
        if (changed.Count > 0) Raise(changed.Distinct(StringComparer.Ordinal).ToArray(), arrived ?? Stopwatch.GetTimestamp());
    }

    // At the end of every sync: a compare-and-set row this replica holds that the server never gave it and nothing here is sending (no rev, nothing queued) was
    // made before the row was shared (a client from before slice 5, or a server below 5 then). It is offered to the server, create only: kept there if the server
    // has none, and if another device's is there first, that one wins and a copy of this one is kept (KeepReplacedAsync).
    private async Task OfferLocalAsync(string? profile, int version, CancellationToken token)
    {
        int offered;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            offered = await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                var offered = 0;
                foreach (var (key, _) in Rows(connection, transaction, "SELECT key, 1 FROM rows WHERE rev = 0 AND payload IS NOT NULL AND key NOT IN (SELECT key FROM pending)"))
                {
                    if (!IsCompareAndSet(key, profile) || SharedCategories.Match(key, profile, version) is null) continue;
                    offered += Execute(connection, transaction,
                        "INSERT INTO pending(client_op_id, key, payload, sent, if_rev, tag, epoch) SELECT $id, key, payload, 0, 0, NULL, (SELECT epoch FROM meta WHERE id = 1) FROM rows WHERE key = $key",
                        ("$id", ProtocolClient.NewOperationId()), ("$key", key));
                }
                transaction.Commit();
                return offered;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        if (offered == 0) return;
        log?.Invoke($"shared local offered n={offered}");
        Interlocked.Exchange(ref flushWanted, 1);
    }

    // A compare-and-set row something holds in memory (FollowAsync).
    private bool Followed(string key, string? profile) => followers.ContainsKey(key) && SharedCategories.MayBeShared(key) && IsCompareAndSet(key, profile);

    // By the category the key is under the profile, not by its prefix: since slice 7 the "preferences" category holds a compare-and-set row (the read-here marks)
    // beside the last-writer-wins ones (the pinned order, the quick replies, the private reading choice).
    private static bool IsCompareAndSet(string key, string? profile) =>
        SharedCategories.Match(key, profile) is { Policy: SharedConflictPolicy.CompareAndSet };

    // The replica holds the server's row of a followed key now (null: none): its follower is brought to it, here in the worker, which sends nothing meanwhile.
    // A follower that holds the server's row as it is leaves its writes from before to be dropped, up to the tag it names, and the replica the server's row; one
    // that wrote has folded them into its own. One that fails is treated as one that holds the server's row: what it had sent may be lost, but nothing made
    // from an older row reaches the server over a newer one.
    // Returns what the follower did (none when there is none). A follower that fails is abandoned
    // (ISharedRowFollower.AbandonAsync): it drops its kept changes, refuses new ones until it reads the row again, and has landed every write it made, which
    // are all dropped here.
    private async Task<SharedRowCaughtUp> CatchUpAsync(string key, byte[]? current, CancellationToken token)
    {
        if (!followers.TryGetValue(key, out var follower)) return default;
        SharedRowCaughtUp caught;
        try { caught = await follower.CatchUpAsync(current, token).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException || !token.IsCancellationRequested)
        {
            log?.Invoke($"shared follow failed error={failure.GetType().Name}");
            try { await follower.AbandonAsync().ConfigureAwait(false); }
            catch (Exception abandoned) { log?.Invoke($"shared follow abandon failed error={abandoned.GetType().Name}"); }
            caught = new SharedRowCaughtUp(false, long.MaxValue);
        }
        if (caught.Wrote) return caught;
        var sealedValue = current is null ? null : await protector.ProtectAsync(current).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction, "DELETE FROM pending WHERE key = $key AND sent = 0 AND (tag IS NULL OR tag <= $through)", ("$key", key), ("$through", caught.Through));
                if (Scalar(connection, transaction, "SELECT 1 FROM pending WHERE key = $key LIMIT 1", ("$key", key)) is null)
                {
                    // The words of a write dropped just now go with it.
                    if (sealedValue is not null) Execute(connection, transaction, "UPDATE rows SET payload = $payload WHERE key = $key", ("$key", key), ("$payload", sealedValue));
                    else
                    {
                        Execute(connection, transaction, "UPDATE rows SET payload = NULL WHERE key = $key AND rev > 0", ("$key", key));
                        Execute(connection, transaction, "DELETE FROM rows WHERE key = $key AND rev = 0", ("$key", key));
                    }
                }
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        return caught;
    }

    private void Raise(IReadOnlyList<string> keys, long arrived)
    {
        try { RemoteChanged?.Invoke(this, new SharedRowsChange(keys, arrived)); } catch (Exception) { /* a listener that fails must not stop the replica */ }
        try { ServerChanged?.Invoke(this, keys); } catch (Exception) { /* nor must a composer */ }
    }

    /// <summary>The same keys as <see cref="RemoteChanged"/>, for what holds rows in memory without a follower (<see cref="ISharedRows.ServerChanged"/>: the composers' drafts).</summary>
    public event EventHandler<IReadOnlyList<string>>? ServerChanged;

    // ---- Sending what is pending ----

    private async Task FlushAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && Available)
        {
            // A write of a category newer than this connection's version waits for a server that keeps it: sent now, it would be refused, and a refused write
            // gives way to the server's row (or to none), which would lose it.
            var profile = await ProfileAsync(token).ConfigureAwait(false);
            var version = client.Version ?? ProtocolVersion.Current;
            // A compare-and-set row that gave up waits for the next connection.
            var next = await NextPendingAsync(key => (SharedCategories.SinceOf(key, profile) is not { } since || since <= version) && !parked.ContainsKey(key), token)
                .ConfigureAwait(false);
            if (next is null) return;
            JsonElement? value = next.Payload is null ? null : JsonSerializer.Deserialize<JsonElement>(await protector.UnprotectAsync(next.Payload).ConfigureAwait(false));
            var clock = Stopwatch.StartNew();
            StatePutResult result;
            // The epoch the write's condition was read in, not the replica's now (a snapshot of a new space moves that on while the write waits): the server judges a rev of another space as lost.
            try { result = await remote.PutAsync(new StatePutParams(next.ClientOpId, [new SharedWrite(next.Key, value, next.IfRev)], next.Epoch), cancellationToken: token).ConfigureAwait(false); }
            catch (NotSupportedException)
            {
                hostLacks = true;
                log?.Invoke($"shared state unavailable host_protocol={client.Version} reason=not_supported");
                await SettleAsync(Volatile.Read(ref connections), ProtocolVersion.Minimum, token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // The connection went, or the server could not answer or would not read the request: this and everything behind it wait, in order, for the next
                // connection, and go again with the same operation id. Only a write the server answers refused is given up (SettleAsync).
                var reason = ex switch
                {
                    BackendException { Failure: BackendFailure.Unavailable } => "unavailable",
                    SharedStateRefusedException => "refused",
                    _ => "other",
                };
                log?.Invoke("shared put failed reason=" + reason);
                return;
            }
            var conflicts = result.Results.Count(item => item.Status == SharedWriteStatus.Conflict);
            log?.Invoke($"shared put n=1 conflicts={conflicts} ms={clock.ElapsedMilliseconds}{(next.Folded > 0 ? $" folded={next.Folded}" : "")}");
            await SettleAsync(next, result.Results.FirstOrDefault(item => item.Key == next.Key), token).ConfigureAwait(false);
        }
    }

    // The batch is done with: off the pending table, and what the server made of it into the replica. When nothing more is pending for the key, a write the server
    // refused, or one whose answer is older than a rev the replica passed over while it was pending (the put had been committed before and its answer lost,
    // and another device wrote after it), leaves the key to be read back from the server (ResolveAsync).
    private async Task SettleAsync(Pending sent, SharedWriteResult? outcome, CancellationToken token)
    {
        if (outcome is { Status: SharedWriteStatus.Refused }) log?.Invoke("shared put refused n=1");
        // A compare-and-set row something holds in memory lost to another device's write: made again from the server's row.
        if (outcome is { Status: SharedWriteStatus.Conflict } && sent.IfRev is not null && Followed(sent.Key, await ProfileAsync(token).ConfigureAwait(false)))
        {
            await LostAsync(sent, outcome.Current, token).ConfigureAwait(false);
            return;
        }
        // A row this device held before it was shared, offered create only, that another device's replaces below: this device's copy is kept.
        if (outcome is { Status: SharedWriteStatus.Conflict } && sent.IfRev == 0 && IsCompareAndSet(sent.Key, await ProfileAsync(token).ConfigureAwait(false))) await KeepReplacedAsync(sent, token).ConfigureAwait(false);
        SharedRow? current = null;
        if (outcome is { Status: SharedWriteStatus.Conflict, Current: { } row })
            current = (await CompleteAsync([row], await ProfileAsync(token).ConfigureAwait(false), reset: false, token).ConfigureAwait(false)).FirstOrDefault();
        bool resolve;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            resolve = await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction, "DELETE FROM pending WHERE seq = $seq", ("$seq", sent.Seq));
                // A write that waited for this one's rev (WriteAfterPendingAsync) was made from what this one wrote; this one did not land, so neither does it.
                if (outcome is not { Status: SharedWriteStatus.Written })
                    Execute(connection, transaction, "DELETE FROM pending WHERE key = $key AND sent = 0 AND if_rev = $after", ("$key", sent.Key), ("$after", AfterPending));
                var more = Scalar(connection, transaction, "SELECT 1 FROM pending WHERE key = $key LIMIT 1", ("$key", sent.Key)) is not null;
                var overtaken = Scalar(connection, transaction, "SELECT rev FROM overtaken WHERE key = $key", ("$key", sent.Key)) is long rev ? rev : 0;
                var resolve = false;
                if (outcome is { Status: SharedWriteStatus.Written })
                {
                    if (!more && overtaken > outcome.Rev)
                    {
                        // The server's row is newer than this write: the replica's rev is this write's, so the row read back is taken.
                        Execute(connection, transaction, "UPDATE rows SET rev = $rev WHERE key = $key", ("$key", sent.Key), ("$rev", outcome.Rev));
                        resolve = true;
                    }
                    else
                    {
                        Execute(connection, transaction, "UPDATE rows SET rev = MAX(rev, $rev) WHERE key = $key", ("$key", sent.Key), ("$rev", outcome.Rev));
                        // A compare-and-set row's writes queued behind this one were made from it: their condition is the rev it made. A write with no condition (the private
                        // choice made private, which must land whatever the server holds) stays without one.
                        // The rev is the answering space's, which is the replica's epoch: the worker applies no snapshot between a put and settling it. (Were the server made anew in between, the
                        // replica's older epoch makes the condition fail, never match.)
                        if (sent.IfRev is not null)
                            Execute(connection, transaction, "UPDATE pending SET if_rev = $rev, epoch = (SELECT epoch FROM meta WHERE id = 1) WHERE key = $key AND sent = 0 AND if_rev IS NOT NULL", ("$key", sent.Key), ("$rev", outcome.Rev));
                        else
                            Execute(connection, transaction, "UPDATE pending SET if_rev = $rev, epoch = (SELECT epoch FROM meta WHERE id = 1) WHERE key = $key AND sent = 0 AND if_rev = $after", ("$key", sent.Key), ("$rev", outcome.Rev), ("$after", AfterPending));
                        // A delete the server made keeps its rev here, so the value it replaced cannot come back from an event that was late.
                        if (sent.Payload is null)
                            Execute(connection, transaction, "INSERT INTO rows(key, payload, rev, updated) VALUES($key, NULL, $rev, $now) ON CONFLICT(key) DO NOTHING",
                                ("$key", sent.Key), ("$rev", outcome.Rev), ("$now", Now()));
                        if (!more) Execute(connection, transaction, "DELETE FROM overtaken WHERE key = $key", ("$key", sent.Key));
                    }
                }
                else if (outcome is null or { Status: SharedWriteStatus.Refused } or { Status: SharedWriteStatus.Conflict, Current: null })
                {
                    // The server does not keep what this replica holds for the key (or, for a conflict that names no row, has none: a condition from another space): unless a later
                    // write is pending, its own row (or none) replaces it.
                    if (!more)
                    {
                        Execute(connection, transaction, "INSERT INTO overtaken(key, rev) VALUES($key, 0) ON CONFLICT(key) DO NOTHING", ("$key", sent.Key));
                        resolve = true;
                    }
                }
                else if (!more)
                {
                    Execute(connection, transaction, "DELETE FROM overtaken WHERE key = $key", ("$key", sent.Key));
                    // A condition from another space loses to a row that may carry the very rev number the replica holds (revs start again in a space made anew), so the server's
                    // row is taken whatever rev is held. Only then: within one space a held rev above the answer's is a newer row already taken, never to be put back.
                    if (outcome is { Status: SharedWriteStatus.Conflict } && sent.Epoch is { } asked
                        && !string.Equals(asked, Scalar(connection, transaction, "SELECT epoch FROM meta WHERE id = 1") as string, StringComparison.Ordinal))
                        Execute(connection, transaction, "UPDATE rows SET rev = 0 WHERE key = $key", ("$key", sent.Key));
                }
                transaction.Commit();
                return resolve;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        if (outcome is { Status: SharedWriteStatus.Written })
        {
            losses.TryRemove(sent.Key, out _);
            parkings.TryRemove(sent.Key, out _);
            // Before the server's newer row is read back (resolve), so the follower does not make again what the server already holds.
            if (sent.Tag is { } tag && followers.TryGetValue(sent.Key, out var follower))
                try { follower.Confirmed(tag); } catch (Exception) { /* a follower that fails must not stop the replica */ }
        }
        // Last writer wins never conflicts; a conditional write that lost takes the server's row unless this PC has written again since.
        if (current is not null) await ApplyAsync([current], null, reset: false, await ProfileAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
        if (resolve) await ResolveAsync(token).ConfigureAwait(false);
    }

    // A followed compare-and-set row's write lost (`conflict`): the replica takes the server's row, drops every write of the row made from the one before, and
    // hands the row to the follower, which makes its changes again over it and writes them (CatchUpAsync). After more than ReapplyLimit losses in a row
    // this connection, the row keeps what this device holds and its write waits, unsent, for the next connection.
    private async Task LostAsync(Pending sent, SharedRow? current, CancellationToken token)
    {
        // The server's row whole: one that came by its size is read.
        if (current is { SizeOnly: true })
            current = (await remote.GetAsync(new StateGetParams([sent.Key]), cancellationToken: token).ConfigureAwait(false)).Rows.FirstOrDefault(row => row.Key == sent.Key && !row.SizeOnly)
                ?? throw new BackendException(BackendFailure.InvalidResponse, ProtocolClient.UnreadableSentence);
        var losing = losses.AddOrUpdate(sent.Key, 1, (_, count) => count + 1);
        if (losing > ReapplyLimit)
        {
            await GiveUpAsync(sent, current?.Rev ?? 0, token).ConfigureAwait(false);
            return;
        }
        var plain = current is { Deleted: false, Value: { } value } ? System.Text.Encoding.UTF8.GetBytes(value.GetRawText()) : null;
        var sealedValue = plain is null ? null : await protector.ProtectAsync(plain).ConfigureAwait(false);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                // This write and those queued behind it were all made from a row the server no longer holds.
                Execute(connection, transaction, "DELETE FROM pending WHERE key = $key", ("$key", sent.Key));
                if (current is null) Execute(connection, transaction, "DELETE FROM rows WHERE key = $key", ("$key", sent.Key));
                else
                    Execute(connection, transaction,
                        "INSERT INTO rows(key, payload, rev, updated) VALUES($key, $payload, $rev, $now) ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, rev = excluded.rev, updated = excluded.updated",
                        ("$key", sent.Key), ("$payload", (object?)sealedValue ?? DBNull.Value), ("$rev", current.Rev), ("$now", Now()));
                Execute(connection, transaction, "DELETE FROM overtaken WHERE key = $key", ("$key", sent.Key));
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        log?.Invoke($"shared reapply n={losing}");
        var caught = await CatchUpAsync(sent.Key, plain, token).ConfigureAwait(false);
        // A create-only write the follower kept no changes for (its words were made before the row was shared, or by a run before this one) and wrote nothing
        // over: another device's replaced it. One whose changes were left out says so itself; one whose changes the server already held lost nothing.
        if (sent.IfRev == 0 && caught is { Wrote: false, Through: 0 }) await KeepReplacedAsync(sent, token).ConfigureAwait(false);
        // What the follower holds now has another device's change in it.
        Raise([sent.Key], Stopwatch.GetTimestamp());
    }

    // This device's words of a row another device's replaced, kept under `replaced:` and the row's key (a category no server keeps, so it stays on this device:
    // the one copy, the latest, for whoever needs it back), and said to be.
    private async Task KeepReplacedAsync(Pending sent, CancellationToken token)
    {
        if (sent.Payload is null) return;
        // The read-here marks merge by the later moment per conversation (ReadHereMarks folds what this device made before the row was shared over the server's),
        // so nothing of this device's is replaced and nothing is said.
        if (await ProfileAsync(token).ConfigureAwait(false) is { } profile && sent.Key == SharedCategories.ReadHereKey(profile)) return;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction,
                    "INSERT INTO rows(key, payload, rev, updated) VALUES($key, $payload, 0, $now) ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, updated = excluded.updated",
                    ("$key", ReplacedPrefix + sent.Key), ("$payload", sent.Payload), ("$now", Now()));
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        log?.Invoke("shared local replaced n=1");
        Tell(SharedRowTold.Replaced);
    }

    /// <summary>Where this device's copy of a row another device's replaced is kept: this prefix and the row's key.</summary>
    public const string ReplacedPrefix = "replaced:";

    private void Tell(SharedRowTold what)
    {
        try { Told?.Invoke(this, what); } catch (Exception) { /* a listener that fails must not stop the replica */ }
    }

    // A parked row goes again (a newer row of the server's arrived, or its wait is over): its write first, as it was, then once more made again.
    private void Unpark(string key)
    {
        if (!parked.TryRemove(key, out _)) return;
        losses[key] = ReapplyLimit - 1;
        Want(ref flushWanted);
    }

    // The lost write goes back on the queue as it was (its condition, its tag, the words the replica holds) and is parked: held for a wait that doubles each
    // time this connection, until a newer row of the server's arrives, or until the next connection, whichever is first. It then loses again unless the other
    // device has stopped, and is made again. Meanwhile this device keeps its own words, and says so.
    private async Task GiveUpAsync(Pending sent, long lostTo, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction, "DELETE FROM pending WHERE seq = $seq", ("$seq", sent.Seq));
                // A later write of the row already waits with the same condition and newer words.
                if (Scalar(connection, transaction, "SELECT 1 FROM pending WHERE key = $key LIMIT 1", ("$key", sent.Key)) is null)
                    Execute(connection, transaction,
                        "INSERT INTO pending(client_op_id, key, payload, sent, if_rev, tag, epoch) SELECT $id, key, payload, 0, $ifRev, $tag, $epoch FROM rows WHERE key = $key",
                        ("$id", ProtocolClient.NewOperationId()), ("$key", sent.Key), ("$ifRev", (object?)sent.IfRev ?? DBNull.Value), ("$tag", (object?)sent.Tag ?? DBNull.Value),
                        ("$epoch", (object?)sent.Epoch ?? DBNull.Value));
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        parked[sent.Key] = lostTo;
        var times = parkings.AddOrUpdate(sent.Key, 1, (_, count) => count + 1);
        var wait = TimeSpan.FromTicks(Math.Min(ParkLongest.Ticks, ParkFirst.Ticks * (1L << Math.Min(times - 1, 20))));
        log?.Invoke($"shared reapply gave_up wait_ms={(long)wait.TotalMilliseconds}");
        Tell(SharedRowTold.Held);
        _ = UnparkLaterAsync(sent.Key, lostTo, wait);
    }

    private async Task UnparkLaterAsync(string key, long lostTo, TimeSpan wait)
    {
        try { await Task.Delay(wait, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        // Only the parking this wait was for: one made since (after another loss) has its own.
        if (parked.TryGetValue(key, out var still) && still == lostTo) Unpark(key);
    }

    // Every key left to be read back from the server (noted in `overtaken` with nothing pending for it): the server's row replaces the replica's, or, when the server
    // holds none, the replica's goes. A key whose read fails stays noted for the next sync.
    private async Task ResolveAsync(CancellationToken token)
    {
        IReadOnlyList<string> keys;
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            keys = await Task.Run(() =>
            {
                using var connection = Open();
                return Rows(connection, null, "SELECT key, 1 FROM overtaken WHERE key NOT IN (SELECT key FROM pending)").Select(item => item.Key).ToList();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        if (keys.Count == 0) return;
        var profile = await ProfileAsync(token).ConfigureAwait(false);
        // A key of a category newer than this connection stays noted for a server that knows it: this one would answer it missing (it gives a connection only
        // the categories its version knows), and the replica's row would go.
        var version = client.Version ?? ProtocolVersion.Current;
        keys = [.. keys.Where(key => SharedCategories.SinceOf(key, profile) is not { } since || since <= version)];
        if (keys.Count == 0) return;
        foreach (var batch in keys.Chunk(SharedLimits.GetKeys))
        {
            var answer = await remote.GetAsync(new StateGetParams(batch), cancellationToken: token).ConfigureAwait(false);
            var found = (await CompleteAsync(answer.Rows, profile, reset: true, token).ConfigureAwait(false)).ToDictionary(row => row.Key, StringComparer.Ordinal);
            var gone = new List<string>();
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    using var connection = Open();
                    using var transaction = connection.BeginTransaction();
                    foreach (var key in batch)
                    {
                        // Written again meanwhile: that write settles it.
                        if (Scalar(connection, transaction, "SELECT 1 FROM pending WHERE key = $key LIMIT 1", ("$key", key)) is not null) continue;
                        Execute(connection, transaction, "DELETE FROM overtaken WHERE key = $key", ("$key", key));
                        if (found.ContainsKey(key)) Execute(connection, transaction, "UPDATE rows SET rev = 0 WHERE key = $key", ("$key", key));
                        else if (Execute(connection, transaction, "DELETE FROM rows WHERE key = $key", ("$key", key)) > 0) gone.Add(key);
                    }
                    transaction.Commit();
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally { gate.Release(); }
            // What holds a row that went takes that first; then told as soon as the rows are gone, not behind the apply of the ones read back (which tells
            // its own): never before the replica shows the change.
            foreach (var key in gone.Where(key => Followed(key, profile))) await CatchUpAsync(key, null, token).ConfigureAwait(false);
            if (gone.Count > 0) Raise(gone, Stopwatch.GetTimestamp());
            await ApplyAsync([.. found.Values], null, reset: false, profile, token).ConfigureAwait(false);
        }
    }

    // The next batch to send. A write not sent yet takes with it every later write to the same key, folded into the newest (which keeps its own operation id),
    // and is marked sent before it goes, in one transaction: from then on it is sent again only as it is, with that id. A write already sent is never folded.
    // Writes of keys the connection may not take yet (`sendable`) are passed over and stay as they are, in their order, for a server that takes them.
    private async Task<Pending?> NextPendingAsync(Func<string, bool> sendable, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                long? first = null;
                using (var keys = connection.CreateCommand())
                {
                    keys.Transaction = transaction;
                    keys.CommandText = "SELECT seq, key FROM pending WHERE if_rev IS NULL OR if_rev <> -1 ORDER BY seq";
                    using var reader = keys.ExecuteReader();
                    while (first is null && reader.Read())
                        if (sendable(reader.GetString(1))) first = reader.GetInt64(0);
                }
                if (first is null) return null;
                var head = Next(connection, transaction, "SELECT seq, client_op_id, key, payload, sent, if_rev, tag, epoch FROM pending WHERE seq = $seq", ("$seq", first.Value));
                if (head is null) return null;
                if (head.Value.Sent) return head.Value.Write;
                // The newest keeps its own condition and tag: the condition every unsent write of the key has, and the newest change it holds.
                var newest = Next(connection, transaction, "SELECT seq, client_op_id, key, payload, sent, if_rev, tag, epoch FROM pending WHERE key = $key ORDER BY seq DESC LIMIT 1",
                    ("$key", head.Value.Write.Key))!.Value.Write;
                var folded = Execute(connection, transaction, "DELETE FROM pending WHERE key = $key AND seq < $seq", ("$key", newest.Key), ("$seq", newest.Seq));
                Execute(connection, transaction, "UPDATE pending SET sent = 1 WHERE seq = $seq", ("$seq", newest.Seq));
                transaction.Commit();
                return newest with { Folded = folded };
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }

        static (Pending Write, bool Sent)? Next(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? (new Pending(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : (byte[])reader[3], 0,
                    reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7)), reader.GetInt64(4) != 0)
                : null;
        }
    }

    /// <summary>How many writes wait for the host.</summary>
    internal async Task<int> PendingCountAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = Open();
                return Convert.ToInt32(Scalar(connection, null, "SELECT COUNT(*) FROM pending"));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Where the replica stands against its server: the space's epoch (null before the first sync) and the last commit it holds whole.</summary>
    internal async Task<(string? Epoch, long Rev)> PositionAsync(CancellationToken token = default)
    {
        var meta = await MetaAsync(token).ConfigureAwait(false);
        return (meta.Epoch, meta.Rev);
    }

    private async Task<Meta> MetaAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT epoch, rev, snapshot, profile, host_protocol FROM meta WHERE id = 1";
                using var reader = command.ExecuteReader();
                reader.Read();
                return new Meta(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2) != 0, reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4));
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // ---- The file ----

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version > 1) throw new InvalidDataException("Local database requires a newer application.");
            if (version == 0)
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                {
                    // rows: what every read draws from (payload NULL: a delete the server has a rev for). pending: the writes on their way, each with its own
                    // operation id, `sent` once it may have reached the server, and for a compare-and-set row its condition (`if_rev`) and its follower's tag.
                    // overtaken: the newest rev passed over for a key while a write of it was pending, or a key to read back from the server. meta: the last
                    // commit held whole, whether a snapshot is still being read, the profile then, and the version the server last spoke (ServerProtocolAsync).
                    command.CommandText =
                        "CREATE TABLE IF NOT EXISTS rows (key TEXT PRIMARY KEY, payload BLOB, rev INTEGER NOT NULL, updated INTEGER NOT NULL);" +
                        "CREATE TABLE IF NOT EXISTS pending (seq INTEGER PRIMARY KEY AUTOINCREMENT, client_op_id TEXT NOT NULL, key TEXT NOT NULL, payload BLOB, sent INTEGER NOT NULL, if_rev INTEGER, tag INTEGER, epoch TEXT);" +
                        "CREATE INDEX IF NOT EXISTS pending_key ON pending(key);" +
                        "CREATE TABLE IF NOT EXISTS overtaken (key TEXT PRIMARY KEY, rev INTEGER NOT NULL);" +
                        "CREATE TABLE IF NOT EXISTS meta (id INTEGER PRIMARY KEY CHECK (id = 1), epoch TEXT, rev INTEGER NOT NULL, snapshot INTEGER NOT NULL, profile TEXT, host_protocol INTEGER);" +
                        "INSERT INTO meta(id, epoch, rev, snapshot, profile) VALUES(1, NULL, 0, 0, NULL);" +
                        "PRAGMA user_version=1;";
                    command.ExecuteNonQuery();
                    var carried = Carry(connection, transaction);
                    if (carried >= 0) log?.Invoke($"shared replica made carried={carried}");
                }
                transaction.Commit();
                command.Transaction = null;
            }
            if (!metaReady)
            {
                // A replica made before slice 3 has no host_protocol column. Added in place, without a new user_version, so the build that made it still opens
                // it (it names its columns, so one more is nothing to it).
                // Likewise a replica made before slice 5 has no condition or tag on its queued writes (none of them is of a compare-and-set row).
                // overtaken.priv: for a watched row (the private reading choice) passed over while a write was pending, whether the newest rev passed over said private (1) or not (0);
                // see PassedOverPrivateAsync.
                // pending.epoch: the space a queued write's condition was read in (Pending); text, which an INTEGER column would turn into a number when it looks like one.
                foreach (var (table, column, type) in new[] { ("meta", "host_protocol", "INTEGER"), ("pending", "if_rev", "INTEGER"), ("pending", "tag", "INTEGER"), ("meta", "peak_protocol", "INTEGER"),
                    ("overtaken", "priv", "INTEGER"), ("pending", "epoch", "TEXT") })
                {
                    if (ColumnPresent(command, table, column)) continue;
                    command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
                    try
                    {
                        command.ExecuteNonQuery();
                        // The writes an older build queued have no epoch, and a server takes none as a rev of its own space. Their revs were read before the last sync this replica
                        // made (the older build is what ran since), so the replica's epoch now is the one they belong to: labelled now, before a sync of a space made anew can move it on.
                        if (table == "pending" && column == "epoch")
                        {
                            command.CommandText = "UPDATE pending SET epoch = (SELECT epoch FROM meta WHERE id = 1) WHERE if_rev IS NOT NULL";
                            command.ExecuteNonQuery();
                        }
                    }
                    // Another process added it first.
                    catch (SqliteException) when (ColumnPresent(command, table, column)) { }
                }
                RenameRefused(connection);
                metaReady = true;
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }

        static bool ColumnPresent(SqliteCommand command, string table, string column)
        {
            command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }
    }

    // The refused sends were filed as outbox: until protocol 35 (SharedCategories.RefusedBefore): the record of this PC's own sends of unknown outcome (InterruptedSends, carried
    // in) and the copies of the host's. Renamed once a run in one transaction, the same rev, value and digest (the digest is of the profile and the id), so the position this
    // replica holds still describes what it holds: a host of 35 files the same rows under the new name, and a host below 35 is read through SharedCategories.FromWire. None of
    // them is ever a pending write (the host's alone) and none is overtaken, but a key is renamed wherever it is kept. Running again finds nothing. Where both names are there (a build from
    // before 35 ran after a rename and took the host's later change under the old one), the row with the later rev stays, a tombstone among them, and an equal
    // rev keeps the renamed one, as state.db does.
    private static void RenameRefused(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        const string Range = "key >= 'outbox:' AND key < 'outbox;'", Now = "'refused:' || substr(key, 8)";
        Execute(connection, transaction, $"DELETE FROM rows WHERE key >= 'refused:' AND key < 'refused;' AND rev < (SELECT older.rev FROM rows older WHERE older.key = 'outbox:' || substr(rows.key, 9))");
        Execute(connection, transaction, $"UPDATE OR IGNORE rows SET key = {Now} WHERE {Range}");
        Execute(connection, transaction, $"DELETE FROM rows WHERE {Range}");
        Execute(connection, transaction, $"UPDATE OR REPLACE overtaken SET key = {Now} WHERE {Range}");
        Execute(connection, transaction, $"UPDATE pending SET key = {Now} WHERE {Range}");
        transaction.Commit();
    }

    // Once, as the replica is made: the root's unsettled sends, copied as they are (sealed by the same protector) from a state.db opened read-only, so nothing
    // there changes. -1 when there is nothing to copy from. Scheduled sends stay behind on purpose: a copy here would be a second place that fires them.
    private int Carry(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (carryFrom is null || !File.Exists(carryFrom)) return -1;
        try
        {
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = carryFrom, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            source.Open();
            using var read = source.CreateCommand();
            // A state.db this build has not opened yet still files them as outbox:, the name before protocol 35 (SqlitePrivateStateStore renames it when it opens it): taken under the
            // new name, as the replica's own rename would leave it, and where both are there the new name's row first, as state.db's own rename keeps it.
            read.CommandText = "SELECT $prefix || substr(key, $length + 1), payload, updated, 0 FROM private_state WHERE substr(key, 1, $length) = $prefix "
                + "UNION ALL SELECT $prefix || substr(key, $before + 1), payload, updated, 1 FROM private_state WHERE substr(key, 1, $before) = $old ORDER BY 1, 4";
            read.Parameters.AddWithValue("$length", CarriedCategory.Length);
            read.Parameters.AddWithValue("$prefix", CarriedCategory);
            read.Parameters.AddWithValue("$before", SharedCategories.RefusedBefore.Length + 1);
            read.Parameters.AddWithValue("$old", SharedCategories.RefusedBefore + ":");
            using var reader = read.ExecuteReader();
            var carried = 0;
            while (reader.Read())
                carried += Execute(connection, transaction, "INSERT INTO rows(key, payload, rev, updated) VALUES($key, $payload, 0, $updated) ON CONFLICT(key) DO NOTHING",
                    ("$key", reader.GetString(0)), ("$payload", (byte[])reader[1]), ("$updated", reader.GetInt64(2)));
            return carried;
        }
        // A state.db that cannot be read (in use, damaged, or from a newer build) gives nothing; the replica is made all the same.
        catch (SqliteException)
        {
            log?.Invoke("shared replica carry failed");
            return 0;
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static void InterlockedMax(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var found = Interlocked.CompareExchange(ref target, value, current);
            if (found == current) return;
            current = found;
        }
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    private static List<(string Key, bool Flag)> Rows(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var items = new List<(string, bool)>();
        while (reader.Read()) items.Add((reader.GetString(0), reader.GetInt64(1) != 0));
        return items;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        client.Connected -= OnConnected;
        client.SharedChanged -= OnShared;
        client.FeedResynced -= OnResynced;
        stop.Cancel();
        // What is pending stays on disk for the next run; a worker in the middle of a put is not waited for long.
        try { worker.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
}

/// <summary>
/// Keys the server changed in a replica (<see cref="ReplicatedStateStore.RemoteChanged"/>, <see cref="ReplicaRows.Changed"/>), and when what changed them arrived:
/// a <see cref="Stopwatch"/> timestamp of the <c>shared</c> event, the sync page or the put's answer.
/// </summary>
internal sealed record SharedRowsChange(IReadOnlyList<string> Keys, long Arrived);

/// <summary>What <see cref="ReplicatedStateStore.Told"/> says of a compare-and-set row (the merges, the labels).</summary>
internal enum SharedRowTold
{
    /// <summary>Its write kept losing to another device's and waits a while before it goes again; this device keeps its own meanwhile.</summary>
    Held,
    /// <summary>Another device's row replaced the one this device held from before it was shared; a copy of this device's is kept under <c>replaced:</c>.</summary>
    Replaced,
}
