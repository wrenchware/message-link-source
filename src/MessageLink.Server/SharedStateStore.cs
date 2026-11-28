using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// The server's one authoritative copy of the shared state (docs/research/switchboard-stage6.md): <c>&lt;root&gt;\Server\shared.db</c>,
/// served as <c>state.sync</c>, <c>state.get</c> and <c>state.put</c>. Only a server keeps one; the app's in-process server never makes the file.
/// </summary>
/// <remarks>
/// <para><b>Rows.</b> <c>rows(key, payload, rev, deleted, size, since, updated)</c> (<c>since</c>: the protocol version of the row's category, which a caller must speak to see it): each value sealed by the server's <see cref="IStateProtector"/>, a delete kept as a
/// tombstone (no payload) for <see cref="TombstonesKept"/> so a client that was away still hears of it. Every commit takes the next rev, one for all the rows it
/// writes, and a tombstone that is let go raises the horizon to its rev: a client whose position is older than that is given a snapshot instead.</para>
/// <para><b>Meta.</b> <c>meta(epoch, rev, horizon)</c>. The epoch is made when the file is made and never changes, so positions from another file (one made
/// anew) are told apart. <c>rev</c> is the latest commit, kept here because the newest rows can be tombstones that have gone.</para>
/// <para><b>Applied.</b> <c>applied(client_op_id, fingerprint, rev, result, at)</c>: each put's answer (sealed: a conflict's answer holds a value), so a repeat
/// of its operation id is answered with it and writes nothing, and the same id with other writes is refused. Kept <see cref="AppliedKept"/> or
/// <see cref="AppliedLimit"/> entries. This is deliberately not the <see cref="OperationJournal"/>: these writes come every few seconds and would push the
/// sends' entries out of it.</para>
/// <para><b>Imports.</b> <c>imports(name, added, skipped, refused, at)</c>: what was taken in once from the server's older stores (<see cref="ImportOnce"/>).</para>
/// <para>One gate serves every call, so a commit, its applied entry and its <c>shared</c> event are one step: the events are in the server's sequence in the
/// order of their revs. Logs carry counts and fixed words only; never a key or a value.</para>
/// <para><b>Space key.</b> A server attaches its <see cref="Space"/> (<see cref="AttachSpace"/>): the keys are then judged under the key pinned in
/// <c>space.protected</c>, the first write pins the key it was judged under before its rows are kept, and every call answers unavailable while the space
/// cannot be served. A store attached without one (<see cref="Attach"/>) never reads or writes that file.</para>
/// </remarks>
public sealed class SharedStateStore : ISharedState
{
    public const string FileName = "shared.db";
    public static readonly TimeSpan TombstonesKept = TimeSpan.FromDays(90);
    public static readonly TimeSpan AppliedKept = TimeSpan.FromDays(7);
    public const int AppliedLimit = 50_000;
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    private sealed record Meta(string Epoch, long Rev, long Horizon);
    private sealed record Stored(string Key, byte[]? Payload, long Rev, bool Deleted, int Size, int Since);

    private readonly string path;
    private readonly IStateProtector protector;
    private readonly Action<string>? log;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private Action<SharedChange>? publish;
    private Func<CancellationToken, Task<string?>>? profile;
    // Whether a server has attached the store yet (Attach or AttachSpace): before that, nothing can say what the server's rows are filed under.
    private volatile bool attached;
    private volatile bool spaced;
    private DateTimeOffset pruned = DateTimeOffset.MinValue;
    private readonly List<(string Name, Func<CancellationToken, Task<ImportRows>> Source)> imports = [];
    // Under the gate: this run has tried its imports.
    private bool imported;
    // Whether this store gives private reading's defaults (GiveReadingDefaults), and, under the gate, whether this run has looked.
    private volatile bool readingDefaults;
    private bool readingDefaultsLooked;

    public SharedStateStore(string path, IStateProtector protector, Action<string>? log = null, TimeProvider? clock = null)
    {
        this.path = path; this.protector = protector; this.log = log; this.clock = clock ?? TimeProvider.System;
        // Beside the store, so a server's two files are always one space. Nothing is read or written until a server attaches it.
        Space = new SharedSpace(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, SharedSpace.FileName), protector, log, RowsUnderAsync);
    }

    /// <summary>The key this space is filed under (<c>space.protected</c>). Used only once <see cref="AttachSpace"/> has been called.</summary>
    public SharedSpace Space { get; }

    /// <summary>
    /// Raised for each commit, in rev order, right after the server's log is given it and still under the store's gate: a handler must not call back into the store
    /// before it returns (queue the work). For what the server does with its own rows (ServerSavedPhotos keeps the saved messages' photos). Never raised for a commit
    /// that failed, and a handler that throws changes nothing.
    /// </summary>
    internal event EventHandler<SharedChange>? Committed;

    // Under the gate: the commit goes to the server's log, then to whatever watches the rows.
    private void Publish(SharedChange change)
    {
        try { publish?.Invoke(change); }
        finally
        {
            try { Committed?.Invoke(this, change); } catch (Exception) { /* a watcher that fails must not undo a commit */ }
        }
    }

    /// <summary>
    /// What the server gives the store: where each commit goes (the event log's <see cref="EventLog.AppendShared"/>, called under the store's gate), and the profile
    /// the keys are filed under now, which decides which keys are shared (<see cref="SharedCategories"/>). Without a profile no single row is shared.
    /// </summary>
    internal void Attach(Action<SharedChange>? publish, Func<CancellationToken, Task<string?>>? profile)
    {
        this.publish = publish;
        this.profile = profile;
        attached = true;
        spaced = false;
    }

    /// <summary>
    /// What a server gives the store: where each commit goes, and the server's own identity, whose key a first write pins. Returns the identity
    /// the server serves from then on: the live one with the pinned key as its <c>LocalKey</c> (<see cref="SpaceIdentity"/>).
    /// </summary>
    internal IServerIdentity AttachSpace(Action<SharedChange>? publish, IServerIdentity live)
    {
        this.publish = publish;
        profile = null;
        Space.Follow(async cancellationToken => (await live.ReadAsync(cancellationToken).ConfigureAwait(false)).LocalKey);
        spaced = true;
        attached = true;
        return new SpaceIdentity(live, Space);
    }

    // The filing a call is judged under, or the answer a call gets while the space cannot be served.
    private async Task<SharedSpace.Filing> FilingAsync(string method, CancellationToken cancellationToken)
    {
        if (!spaced) return new SharedSpace.Filing(profile is null ? null : await profile(cancellationToken).ConfigureAwait(false), null);
        SharedSpace.Filing filing;
        try { filing = await Space.FilingAsync(cancellationToken).ConfigureAwait(false); }
        catch (SpaceUnavailableException)
        {
            log?.Invoke($"shared {method} refused reason=space");
            throw SpaceUnavailableException.Answer();
        }
        // Nothing pinned and nothing signed in on the server: there is no key to file under. The categories keyed by conversation would otherwise take a row
        // under whatever profile the client brought, with no pin beside it; unavailable keeps the client's writes pending until the server has a key (its clients
        // have none either then, as they take theirs from the server). The server's own snoozes.* do not come this way (SharedChatSnoozes).
        if (filing.Profile is null)
        {
            log?.Invoke($"shared {method} refused reason=no_key");
            throw SpaceUnavailableException.Answer();
        }
        return filing;
    }

    /// <summary>
    /// The profile the server's own rows are filed under now: its space key's once one is attached (the pinned key, or the live one until a first write pins it),
    /// otherwise the one it was attached with; null while there is none. What every <see cref="SharedSpaceView{T}"/> files its rows under. Throws
    /// <see cref="SpaceUnavailableException"/> while the space cannot be served, and <see cref="InvalidOperationException"/> before a server has attached the
    /// store: a feature made before the server's identity (the incoming unarchive's merges) then leaves its step rather than take "no profile" for an answer.
    /// </summary>
    internal async Task<string?> ProfileAsync(CancellationToken cancellationToken = default)
    {
        if (!attached) throw new InvalidOperationException("The host's identity is not made yet.");
        return spaced ? NotificationIdentity.ProfileOf(await Space.CurrentKeyAsync(cancellationToken).ConfigureAwait(false))
            : profile is null ? null : await profile(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One category of this space as the server's own features read and write it (<see cref="SharedSpaceView{T}"/>).</summary>
    internal SharedSpaceView<T> View<T>(string category, Func<T, string?>? idOf = null, Func<T, byte[]>? encode = null, Func<byte[], T?>? decode = null) where T : class =>
        new(this, category, idOf, encode, decode);

    /// <summary>
    /// Rows the server held elsewhere before this store kept them (slice 3: its own <c>ChatSnoozes-v1</c> files, <see cref="SharedChatSnoozes"/>), taken in once:
    /// at the store's first use in a run, under its gate, so nothing is served before them. Each key is added only where the space holds no row for it (live or
    /// deleted: an end made here is never undone by the old copy), in one transaction (a rev for every SharedLimits.PutWrites rows), and the import is recorded with its counts in the same transaction, and
    /// then beside the store (<c>&lt;name&gt;.imported</c>, which outlives a shared.db made anew), so a later run, or a second import of the same name, adds
    /// nothing. An import is finished only when its source read every entry (<see cref="ImportRows.Unreadable"/> 0): otherwise what was read is taken in,
    /// nothing is recorded done, <c>shared import kind=… unreadable=n</c> is logged, and the next start tries again, never taking in a key twice. The source is
    /// never changed. Registering does nothing: like the file, it waits for use. Rows taken in are keyed by conversation, so they never decide the space key
    /// (<see cref="SharedSpace"/>).
    /// </summary>
    internal void ImportOnce(string name, Func<CancellationToken, Task<ImportRows>> source) => imports.Add((name, source));

    /// <summary>
    /// Private reading's default for a space no device has made the choice in (owner, 2026-11-23). A client reads a service with no row as private
    /// (SharedPrivateReading, so a receipt never goes out on a choice it has not read yet), and nothing but a device's own toggle or a PC's move ever wrote one: a new
    /// install read every service privately, sent no read receipts and marked nothing read on the phone, while the guide says off. With this, after a put has
    /// pinned the space, the four rows are written as false once, only where no device ever wrote any of them (a row deleted since counts as a choice), and the
    /// space records that it looked (<see cref="ReadingDefaultsName"/>), so a run after it never adds them again. A space made anew by <c>--reset-space</c> is
    /// recorded as looked at already (<see cref="CreateFresh"/>): the person had choices there, and it stays private until one is made or moved in. So is a
    /// restored one (<see cref="CreateRestored"/>), since a backup carries neither that record nor a deleted row.
    /// Only the two servers call this; the sample server writes its own choice.
    /// </summary>
    internal void GiveReadingDefaults() => readingDefaults = true;

    /// <summary>The <c>imports</c> entry that says a space was looked at for private reading's defaults.</summary>
    internal const string ReadingDefaultsName = "private-reading-defaults";

    // Under the gate, after a put kept a row (so the space is pinned under `profile`), once a run.
    private async Task ReadingDefaultsAsync(string profile)
    {
        readingDefaultsLooked = true;
        try
        {
            var keys = SharedCategories.PrivateReadingWords.Select(word => SharedCategories.PrivateReadingKey(profile, word)).ToList();
            var (done, chosen) = await Task.Run(() =>
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = ImportsTable;
                command.ExecuteNonQuery();
                var done = Query(connection, "SELECT 1 FROM imports WHERE name = $name", r => 1, ("$name", ReadingDefaultsName)).Count > 0;
                var chosen = keys.Any(key => Query(connection, "SELECT 1 FROM rows WHERE key = $key", r => 1, ("$key", key)).Count > 0);
                return (done, chosen);
            }).ConfigureAwait(false);
            if (done) return;
            var off = "false"u8.ToArray();
            var accepted = new List<(string Key, byte[] Value, int Since, byte[] Sealed)>();
            if (!chosen)
                foreach (var key in keys)
                    accepted.Add((key, off, SharedCategories.Match(key, profile)!.Since, await protector.ProtectAsync(off).ConfigureAwait(false)));
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var added = await AddAbsentAsync(null, accepted, now, (connection, transaction, count) =>
                Execute(connection, transaction, "INSERT OR IGNORE INTO imports(name, added, skipped, refused, at) VALUES($name, $added, 0, 0, $now)",
                    ("$name", ReadingDefaultsName), ("$added", count), ("$now", now))).ConfigureAwait(false);
            log?.Invoke($"shared defaults kind=private-reading outcome={(chosen ? "chosen" : "off")} added={added.Count}");
        }
        // The put has committed: a failure here leaves the space as it was (private), and the next run looks again.
        catch (Exception failure) { log?.Invoke($"shared defaults kind=private-reading failed error={failure.GetType().Name}"); }
    }

    /// <summary>What an import's source holds: the rows it could read, and how many of its entries it could not (which leave the import unfinished).</summary>
    internal sealed record ImportRows(IReadOnlyList<(string Key, byte[] Value)> Rows, int Unreadable);

    // Under the gate. A failure is logged and tried again on the next run; this one serves without it rather than not at all.
    private async Task ImportsAsync()
    {
        if (imported) return;
        imported = true;
        foreach (var (name, source) in imports)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var marker = MarkerOf(name);
                var done = await Task.Run(() =>
                {
                    using var connection = Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = ImportsTable;
                    command.ExecuteNonQuery();
                    return Query(connection, "SELECT 1 FROM imports WHERE name = $name", r => 1, ("$name", name)).Count > 0;
                }).ConfigureAwait(false);
                // The marker answers for a shared.db made anew since (deleted, or set aside as damaged): the files are still there, untouched, and taking them
                // in again would bring back every snooze ended since.
                if (done || File.Exists(marker))
                {
                    log?.Invoke($"shared import kind={name} outcome=before{(done ? "" : " source=marker")}");
                    continue;
                }
                var offered = await source(CancellationToken.None).ConfigureAwait(false);
                // Finished only when every entry of the source was read: a file passed over now (locked, unreadable for the moment) is taken in at a later start,
                // so nothing is recorded done until then. What was read is taken in meanwhile, add-never-replace, so trying again is harmless.
                var complete = offered.Unreadable == 0;
                var refused = 0;
                var accepted = new List<(string Key, byte[] Value, int Since, byte[] Sealed)>();
                foreach (var (key, value) in offered.Rows.DistinctBy(row => row.Key, StringComparer.Ordinal))
                {
                    if (key is not { Length: > 0 and <= SharedLimits.KeyLength } || SharedCategories.Match(key, null) is not { } category || value.Length > SharedLimits.ValueBytes
                        || !IsJson(value))
                    {
                        refused++;
                        continue;
                    }
                    accepted.Add((key, value, category.Since, await protector.ProtectAsync(value).ConfigureAwait(false)));
                }
                var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
                var added = await AddAbsentAsync(name, accepted, now, complete
                    ? (connection, transaction, count) => Execute(connection, transaction, "INSERT INTO imports(name, added, skipped, refused, at) VALUES($name, $added, $skipped, $refused, $now)",
                        ("$name", name), ("$added", count), ("$skipped", accepted.Count - count), ("$refused", refused), ("$now", now))
                    : null).ConfigureAwait(false);
                log?.Invoke($"shared import kind={name} added={added.Count} skipped={accepted.Count - added.Count} refused={refused} ms={timer.ElapsedMilliseconds}");
                if (complete) WriteMarker(marker, added.Count, accepted.Count - added.Count, refused, now);
                else log?.Invoke($"shared import kind={name} unreadable={offered.Unreadable}");
            }
            catch (Exception failure) { log?.Invoke($"shared import kind={name} failed error={failure.GetType().Name}"); }
        }
    }

    // Under the gate. Adds the rows the space holds nothing for (a row ended since counts as held: an end made here is never undone by an older copy), in one
    // transaction but a rev for every SharedLimits.PutWrites rows added, so its commits are shaped like puts' and no state.sync page or published event outgrows a
    // frame however many rows come (a commit is never split across pages, so one rev for tens of thousands of rows would be a page no frame could carry). Tells the
    // clients already connected, one event per rev, as a put does. Returns the indices of `accepted` that were added. With a `name` (a source that can be read again
    // after a stop) every key it took in or found there is noted (import_keys), so a later run never brings back one that ended since; `within` runs in the same
    // transaction, after the rows, with how many were added. The one add-never-replace rule of the server's imports of its older stores (ImportsAsync) and of a PC's
    // move (ImportBundleAsync).
    private async Task<List<int>> AddAbsentAsync(string? name, List<(string Key, byte[] Value, int Since, byte[] Sealed)> accepted, long now,
        Action<SqliteConnection, SqliteTransaction, int>? within)
    {
        var (epoch, firstRev, added) = await Task.Run(() =>
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var meta = Query(connection, "SELECT epoch, rev, horizon FROM meta WHERE id = 1", r => new Meta(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), transaction).Single();
            var added = new List<int>();
            for (var i = 0; i < accepted.Count; i++)
            {
                // A key an earlier, unfinished run took in is never taken in again: ended since, its delete may be forgotten (after 90 days), and the
                // old copy must not bring it back.
                var seen = false;
                if (name is not null)
                {
                    seen = Query(connection, "SELECT 1 FROM import_keys WHERE name = $name AND key = $key", r => 1, transaction, ("$name", name), ("$key", accepted[i].Key)).Count > 0;
                    Execute(connection, transaction, "INSERT OR IGNORE INTO import_keys(name, key) VALUES($name, $key)", ("$name", name), ("$key", accepted[i].Key));
                }
                if (seen || Query(connection, "SELECT 1 FROM rows WHERE key = $key", r => 1, transaction, ("$key", accepted[i].Key)).Count > 0) continue;
                Execute(connection, transaction, "INSERT INTO rows(key, payload, rev, deleted, size, since, updated) VALUES($key, $payload, $rev, 0, $size, $since, $now)",
                    ("$key", accepted[i].Key), ("$payload", accepted[i].Sealed), ("$rev", meta.Rev + 1 + added.Count / SharedLimits.PutWrites), ("$size", accepted[i].Value.Length),
                    ("$since", accepted[i].Since), ("$now", now));
                added.Add(i);
            }
            // The last rev used, which is where the space stands for a client that reads it all (and what a move reports as its position).
            if (added.Count > 0) Execute(connection, transaction, "UPDATE meta SET rev = $rev", ("$rev", meta.Rev + 1 + (added.Count - 1) / SharedLimits.PutWrites));
            within?.Invoke(connection, transaction, added.Count);
            transaction.Commit();
            return (meta.Epoch, meta.Rev + 1, added);
        }).ConfigureAwait(false);
        if (added.Count > 0)
        {
            // One event per commit, as a put's: the clients already connected hear of it (none is, the first time: this runs before anything is served).
            for (var from = 0; from < added.Count; from += SharedLimits.PutWrites)
            {
                var rev = firstRev + from / SharedLimits.PutWrites;
                var budget = SharedLimits.EventBytes;
                var rows = new List<SharedRow>(SharedLimits.PutWrites);
                foreach (var i in added.Skip(from).Take(SharedLimits.PutWrites))
                {
                    var (key, value, since, _) = accepted[i];
                    var inline = value.Length <= SharedLimits.InlineBytes && value.Length <= budget;
                    if (inline) budget -= value.Length;
                    rows.Add(new SharedRow(key, rev, inline ? JsonSerializer.Deserialize<JsonElement>(value) : null, false, value.Length) { Since = since });
                }
                try { Publish(new SharedChange(epoch, rev, rows)); } catch (Exception) { /* as a put's: clients catch up with state.sync */ }
            }
            Space.RowsAdded();
        }
        return added;
    }

    /// <summary>What <see cref="ImportBundleAsync"/> did, by a fixed word and counts.</summary>
    internal sealed record BundleImported(string Status, int Added, int Skipped, int Refused, IReadOnlyDictionary<string, int> PerKind, string Digest, string Epoch, long Rev,
        bool Pinned, string Values = "")
    {
        /// <summary>Nothing was kept, and the answer says only why.</summary>
        public static BundleImported Kept(string status) => new(status, 0, 0, 0, new Dictionary<string, int>(), "", "", 0, false);
    }

    /// <summary>
    /// A PC's move: the rows of <paramref name="bundle"/>, taken in once. The space must be filed under the bundle's key: one with none yet is
    /// filed under it now (<see cref="SharedSpace.PinAsync"/>, <c>space key pinned via=import</c>), the same key is fine, and any other answers
    /// <see cref="StateImportStatuses.SpaceMismatch"/> with nothing kept. Rows are added where the space holds nothing for the key and never replace one, so a repeat
    /// adds nothing, and one the server's clients changed or ended meanwhile is theirs. A row the server does not keep (not a shared kind under the bundle's profile,
    /// a row only a server writes, too large) is refused and counted. The answer says which of the bundle's keys the space now holds live, by kind, as a digest of the keys and
    /// as a digest of the keys with their stored values (<see cref="MoveCounts.ValuesDigestOf"/>), to compare with the bundle's own: a key the space ended since, or holds
    /// with another value than the PC's, makes the move look incomplete rather than whole.
    /// <paramref name="resume"/>: the caller says this is a retry of a move it began and did not see finish. A space that is already filed under the bundle's key and holds
    /// rows is otherwise refused (<see cref="StateImportStatuses.SpaceInUse"/>): it has had a life of its own, and a move into it is not a first move.
    /// </summary>
    internal async Task<BundleImported> ImportBundleAsync(MoveBundle bundle, bool resume = false, CancellationToken cancellationToken = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var profile = NotificationIdentity.ProfileOf(bundle.LocalKey) ?? throw new ArgumentException("The bundle names no key.", nameof(bundle));
        var refused = 0;
        var accepted = new List<(string Key, byte[] Value, int Since, byte[] Sealed)>();
        foreach (var row in bundle.Rows.DistinctBy(row => row.Key, StringComparer.Ordinal))
        {
            var value = Encoding.UTF8.GetBytes(row.Value.GetRawText());
            if (row.Key is not { Length: > 0 and <= SharedLimits.KeyLength } || SharedCategories.Match(row.Key, profile) is not { HostOwned: false } category || value.Length > SharedLimits.ValueBytes)
            {
                refused++;
                continue;
            }
            accepted.Add((row.Key, value, category.Since, await protector.ProtectAsync(value).ConfigureAwait(false)));
        }
        var keys = bundle.Rows.Select(row => row.Key).Distinct(StringComparer.Ordinal).ToList();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ImportsAsync().ConfigureAwait(false);
            // The key the rows are filed under, before any is kept. Pinned already to the bundle's key is a repeat; pinned to another, or rows made under another, is not a
            // space this PC's rows belong in (a server that has been used is started anew first: the Linux server's --reset-space).
            var pinned = false;
            if (spaced)
            {
                var pin = await Space.PinAsync(bundle.LocalKey, "import", cancellationToken).ConfigureAwait(false);
                if (pin == SpacePin.Refused)
                {
                    log?.Invoke("shared import kind=move outcome=space_mismatch");
                    return BundleImported.Kept(StateImportStatuses.SpaceMismatch);
                }
                pinned = pin == SpacePin.Pinned;
            }
            else if (!string.Equals(profile, this.profile is null ? null : await this.profile(cancellationToken).ConfigureAwait(false), StringComparison.Ordinal))
            {
                log?.Invoke("shared import kind=move outcome=space_mismatch");
                return BundleImported.Kept(StateImportStatuses.SpaceMismatch);
            }
            // A space that was already filed under this key and holds rows is not empty: only a retry of this very move may add to it.
            if (!pinned && !resume && await Task.Run(() => { using var connection = Open(); return Query(connection, "SELECT 1 FROM rows LIMIT 1", r => 1).Count > 0; }, CancellationToken.None).ConfigureAwait(false))
            {
                log?.Invoke("shared import kind=move outcome=space_in_use");
                return BundleImported.Kept(StateImportStatuses.SpaceInUse);
            }
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var added = await AddAbsentAsync(null, accepted, now, null).ConfigureAwait(false);
            // What the space holds now of the keys that came, live, with the values it stores: a key ended since, or held with another value, is not what the PC sent.
            var (meta, held) = await Task.Run(() =>
            {
                using var connection = Open();
                var found = new List<(string Key, byte[] Payload)>();
                foreach (var key in keys)
                    if (Query(connection, "SELECT key, payload, rev, deleted, size, since FROM rows WHERE key = $key AND deleted = 0", ("$key", key)) is [{ Payload: { } payload }]) found.Add((key, payload));
                return (ReadMeta(connection), found);
            }, CancellationToken.None).ConfigureAwait(false);
            var live = new List<(string Key, byte[] Value)>(held.Count);
            foreach (var (key, payload) in held) live.Add((key, await protector.UnprotectAsync(payload).ConfigureAwait(false)));
            var present = live.Select(row => row.Key).ToList();
            log?.Invoke($"shared import kind=move added={added.Count} skipped={accepted.Count - added.Count} refused={refused} ms={timer.ElapsedMilliseconds}");
            return new(StateImportStatuses.Done, added.Count, accepted.Count - added.Count, refused, MoveCounts.Of(present), MoveCounts.DigestOf(present), meta.Epoch, meta.Rev, pinned,
                MoveCounts.ValuesDigestOf(live));
        }
        finally { gate.Release(); }
    }

    // An import's record beside shared.db (Server\<name>.imported), as well as in its imports table. Beside the store rather than beside the files taken in:
    // the files may be the in-process app's own (a local server shares its root), which this server never writes, and renaming them away would take the
    // in-process app's snoozes (its rollback copy) with them. A record here goes only with the whole Server folder, and a server started anew taking its
    // files in again is what a new server should do. Counts and a moment only.
    private string MarkerOf(string name) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name + ".imported");

    private void WriteMarker(string marker, int added, int skipped, int refused, long at)
    {
        var temporary = marker + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Added = added, Skipped = skipped, Refused = refused, At = at }));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, marker, overwrite: true);
        }
        // The imports table still records it; only a store made anew would then take the files in again.
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"shared import marker failed error={failure.GetType().Name}");
            try { File.Delete(temporary); } catch (Exception) { }
        }
    }

    private static bool IsJson(byte[] value)
    {
        try
        {
            JsonSerializer.Deserialize<JsonElement>(value);
            return true;
        }
        catch (JsonException) { return false; }
    }

    // imports: each import finished, with its counts. import_keys: every key an import has taken in or found there, finished or not.
    private const string ImportsTable =
        "CREATE TABLE IF NOT EXISTS imports (name TEXT PRIMARY KEY, added INTEGER NOT NULL, skipped INTEGER NOT NULL, refused INTEGER NOT NULL, at INTEGER NOT NULL);" +
        "CREATE TABLE IF NOT EXISTS import_keys (name TEXT NOT NULL, key TEXT NOT NULL, PRIMARY KEY (name, key))";

    /// <summary>
    /// The value of one live row, or null: what the server's own <c>snoozes.get</c> reads (<see cref="SharedChatSnoozes"/>). Like <see cref="LiveRowsAsync"/> and
    /// <see cref="WriteOwnBatchAsync"/>, it is not judged under the space key: a snooze's key carries the profile its client gave, as its file's name did, and
    /// <c>snoozes.*</c> answered whatever the server's sign-ins were before slice 3, so it still does.
    /// </summary>
    internal async Task<byte[]?> ReadLiveAsync(string key, CancellationToken cancellationToken = default) =>
        (await LiveRowAsync(key, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary><see cref="ReadLiveAsync"/> with the row's rev, in one read: 0 and null when there is no live row.</summary>
    internal async Task<(byte[]? Value, long Rev)> LiveRowAsync(string key, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ImportsAsync().ConfigureAwait(false);
            var stored = await Task.Run(() =>
            {
                using var connection = Open();
                return Query(connection, "SELECT key, payload, rev, deleted, size, since FROM rows WHERE key = $key AND deleted = 0", ("$key", key));
            }, cancellationToken).ConfigureAwait(false);
            return stored is [{ Payload: { } payload } row] ? (await protector.UnprotectAsync(payload).ConfigureAwait(false), row.Rev) : (null, 0);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Every live row whose key starts with <paramref name="prefix"/>, with its value: what the server's own <c>snoozes.list</c> reads (<see cref="SharedChatSnoozes"/>).
    /// The server's alone; no client reads this way.
    /// </summary>
    internal async Task<IReadOnlyList<(string Key, byte[] Value)>> LiveRowsAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ImportsAsync().ConfigureAwait(false);
            var stored = await Task.Run(() =>
            {
                using var connection = Open();
                return Query(connection, "SELECT key, payload, rev, deleted, size, since FROM rows WHERE deleted = 0 AND substr(key, 1, $length) = $prefix ORDER BY key",
                    ("$length", prefix.Length), ("$prefix", prefix));
            }, cancellationToken).ConfigureAwait(false);
            var values = new List<(string, byte[])>(stored.Count);
            foreach (var row in stored)
                if (row.Payload is { } payload) values.Add((row.Key, await protector.UnprotectAsync(payload).ConfigureAwait(false)));
            return values;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// <see cref="LiveRowsAsync"/> with each row's rev: the server's own schedule of scheduled sends (<see cref="ServerScheduledSends"/>) reads its rows with the
    /// revs its clients' commands are made against. Like the other server reads, not judged under the space key.
    /// </summary>
    internal async Task<IReadOnlyList<(string Key, byte[] Value, long Rev)>> LiveRowsWithRevAsync(string prefix, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ImportsAsync().ConfigureAwait(false);
            var stored = await Task.Run(() =>
            {
                using var connection = Open();
                return Query(connection, "SELECT key, payload, rev, deleted, size, since FROM rows WHERE deleted = 0 AND substr(key, 1, $length) = $prefix ORDER BY key",
                    ("$length", prefix.Length), ("$prefix", prefix));
            }, cancellationToken).ConfigureAwait(false);
            var values = new List<(string, byte[], long)>(stored.Count);
            foreach (var row in stored)
                if (row.Payload is { } payload) values.Add((row.Key, await protector.UnprotectAsync(payload).ConfigureAwait(false), row.Rev));
            return values;
        }
        finally { gate.Release(); }
    }

    /// <summary>The rev of one live row, or 0 when there is none: what a command's <c>ifRev</c> is held against.</summary>
    internal async Task<long> LiveRevAsync(string key, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ImportsAsync().ConfigureAwait(false);
            var stored = await Task.Run(() =>
            {
                using var connection = Open();
                return Query(connection, "SELECT key, payload, rev, deleted, size, since FROM rows WHERE key = $key AND deleted = 0", ("$key", key));
            }, cancellationToken).ConfigureAwait(false);
            return stored is [{ } row] ? row.Rev : 0;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// The server's own write of rows in one commit, one rev and one <c>shared</c> event, with no operation id: every write of the server's own features
    /// (<see cref="SharedSpaceView{T}"/>). The scheduled sends save a whole list, which is rows here; a snooze is one row, written whatever the space's key (a row
    /// keyed by conversation never decides the space, <see cref="SharedSpace"/>). A delete of a row that is not live is skipped, and a batch with nothing left
    /// to write commits nothing. Returns the commit's rev, or 0 when there was none. Each key must be a fixed-row-less shared key (<see cref="SharedCategories.Match"/>
    /// with no row), or a category's one row filed under <paramref name="filedUnder"/>. At most <see cref="SharedLimits.PageRows"/> rows, so one commit is
    /// always one page of <c>state.sync</c>.
    /// </summary>
    /// <param name="filedUnder">The profile a category's one row is filed under (the labels' book): only that row of it is kept.</param>
    /// <param name="ifRevs">Beside each write, the rev it holds only at, as a put's <c>ifRev</c> (0: only while there is no live row; null: always). One that does
    /// not hold keeps nothing, pins nothing and throws <see cref="SharedRowMovedException"/>; the look and the commit are under one hold of the store's gate.</param>
    /// <param name="pin">The first row kept in a space pins the key it is filed under, before it is kept, as a client's put does (a message scheduled or a send recorded names that key in its own
    /// digest, and a key that later changed would orphan it); a pin that does not hold keeps nothing and answers the space unavailable.</param>
    /// <param name="retire">Ids that stop being usable for good with this commit (see <see cref="IsRetiredAsync"/>).</param>
    /// <param name="makes">Ids this commit makes a row for. If any is retired the commit keeps nothing and throws <see cref="RetiredIdException"/>; the look and the commit are one transaction,
    /// so nothing can retire an id between them.</param>
    internal async Task<long> WriteOwnBatchAsync(IReadOnlyList<(string Key, byte[]? Value)> writes, bool pin = false, IReadOnlyList<string>? retire = null, IReadOnlyList<string>? makes = null,
        CancellationToken cancellationToken = default, string? filedUnder = null, IReadOnlyList<long?>? ifRevs = null)
    {
        if (writes.Count is 0 or > SharedLimits.PageRows || writes.Select(write => write.Key).Distinct(StringComparer.Ordinal).Count() != writes.Count
            || ifRevs is not null && (ifRevs.Count != writes.Count || ifRevs.Any(rev => rev < 0)))
            throw new ArgumentException("The server does not keep that.", nameof(writes));
        var categories = new SharedCategory[writes.Count];
        for (var i = 0; i < writes.Count; i++)
        {
            var (key, value) = writes[i];
            var category = key is not { Length: > 0 and <= SharedLimits.KeyLength } ? null
                : SharedCategories.Match(key, null) is { Row: null } any ? any
                : filedUnder is not null && SharedCategories.Match(key, filedUnder) is { Row: not null } one ? one : null;
            if (category is null || value is { Length: > SharedLimits.ValueBytes } || value is not null && !IsJson(value))
                throw new ArgumentException("The server does not keep that.", nameof(writes));
            categories[i] = category;
        }
        var sealedValues = new byte[]?[writes.Count];
        for (var i = 0; i < writes.Count; i++)
            if (writes[i].Value is { } value) sealedValues[i] = await protector.ProtectAsync(value).ConfigureAwait(false);
        // Read before the gate: the identity may read a file. Only a write that keeps a row has a key to pin; a category's one row is also judged under it.
        var fixedRow = categories.Any(category => category.Row is not null);
        var filing = pin && writes.Any(write => write.Value is not null) || fixedRow ? await FilingAsync("own", cancellationToken).ConfigureAwait(false) : null;
        // A category's one row is kept only under the profile the space files under now, as a put judges it: a writer that read the profile before the space was
        // pinned under another (a move) is refused rather than keep a row no client reads.
        if (fixedRow && !string.Equals(filing?.Profile, filedUnder, StringComparison.Ordinal))
        {
            log?.Invoke("shared own write refused reason=key");
            throw new ArgumentException("The server does not keep that.", nameof(writes));
        }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ImportsAsync().ConfigureAwait(false);
            // A write held to a rev, as a put's ifRev: 0 creates only, any other holds only while the row (live or deleted) is at that rev. Looked at before the pin, so
            // a write that does not hold pins nothing, as a put's; under the gate, so nothing moves the row before the commit.
            if (ifRevs is not null)
            {
                var moved = await Task.Run(() =>
                {
                    using var connection = Open();
                    for (var i = 0; i < writes.Count; i++)
                    {
                        if (ifRevs[i] is not { } expected) continue;
                        var row = Query(connection, "SELECT rev, deleted FROM rows WHERE key = $key", r => (Rev: r.GetInt64(0), Deleted: r.GetInt64(1) != 0), ("$key", writes[i].Key));
                        if (expected == 0 ? row is [{ Deleted: false }] : row is not [{ } found] || found.Rev != expected) return true;
                    }
                    return false;
                }, CancellationToken.None).ConfigureAwait(false);
                if (moved) throw new SharedRowMovedException();
            }
            // The key the rows are filed under is pinned before the first of them is kept; one that does not hold keeps nothing.
            if (filing?.Unpinned is { } unpinned && await Space.PinAsync(unpinned, "write", cancellationToken).ConfigureAwait(false) == SpacePin.Refused)
            {
                log?.Invoke("shared own write refused reason=space");
                throw SpaceUnavailableException.Answer();
            }
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var committed = await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                if (makes is { Count: > 0 })
                {
                    Execute(connection, transaction, RetiredTable);
                    var gone = makes.Where(id => Query(connection, "SELECT 1 FROM retired WHERE id = $id", r => 1, transaction, ("$id", NotificationIdentity.Hash(id))).Count > 0).ToList();
                    if (gone.Count > 0) throw new RetiredIdException(gone);
                }
                var written = new List<int>();
                for (var i = 0; i < writes.Count; i++)
                {
                    if (writes[i].Value is null
                        && Query(connection, "SELECT 1 FROM rows WHERE key = $key AND deleted = 0", r => 1, transaction, ("$key", writes[i].Key)).Count == 0)
                        continue;
                    written.Add(i);
                }
                if (written.Count == 0) return ((string, long, List<int>)?)null;
                var meta = Query(connection, "SELECT epoch, rev, horizon FROM meta WHERE id = 1", r => new Meta(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), transaction).Single();
                var rev = meta.Rev + 1;
                foreach (var i in written)
                    Execute(connection, transaction, "INSERT INTO rows(key, payload, rev, deleted, size, since, updated) VALUES($key, $payload, $rev, $deleted, $size, $since, $now) " +
                        "ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, rev = excluded.rev, deleted = excluded.deleted, size = excluded.size, since = excluded.since, updated = excluded.updated",
                        ("$key", writes[i].Key), ("$payload", (object?)sealedValues[i] ?? DBNull.Value), ("$rev", rev), ("$deleted", writes[i].Value is null ? 1 : 0),
                        ("$size", writes[i].Value?.Length ?? 0), ("$since", categories[i].Since), ("$now", now));
                Execute(connection, transaction, "UPDATE meta SET rev = $rev", ("$rev", rev));
                // The ids the commit retires (the messages it removes), in the same transaction: a removal and its tombstone are one fact, kept past any journal window.
                if (retire is { Count: > 0 })
                {
                    Execute(connection, transaction, RetiredTable);
                    foreach (var id in retire)
                        Execute(connection, transaction, "INSERT OR IGNORE INTO retired(id, at) VALUES($id, $now)", ("$id", NotificationIdentity.Hash(id)), ("$now", now));
                }
                transaction.Commit();
                return (meta.Epoch, rev, written);
            }, CancellationToken.None).ConfigureAwait(false);
            if (committed is not { } commit) return 0;
            var budget = SharedLimits.EventBytes;
            var rows = new List<SharedRow>(commit.Item3.Count);
            foreach (var i in commit.Item3)
            {
                var value = writes[i].Value;
                var size = value?.Length ?? 0;
                var inline = value is not null && size <= SharedLimits.InlineBytes && size <= budget;
                if (inline) budget -= size;
                rows.Add(new SharedRow(writes[i].Key, commit.Item2, inline ? JsonSerializer.Deserialize<JsonElement>(value!) : null, value is null, size) { Since = categories[i].Since });
            }
            try { Publish(new SharedChange(commit.Item1, commit.Item2, rows)); } catch (Exception) { /* as a put's: clients catch up with state.sync */ }
            // A write held to a rev is the server writing a client's row as a client's put would (the Marketplace label): private reading's defaults follow it as they
            // follow a put. The server's own rows (the schedule, its records) never gave them, and still do not.
            if (ifRevs is not null && filing?.Profile is { } pinnedUnder && readingDefaults && !readingDefaultsLooked && spaced) await ReadingDefaultsAsync(pinnedUnder).ConfigureAwait(false);
            return commit.Item2;
        }
        finally { gate.Release(); }
    }

    // retired: the digest of every id a server has let go of for good (a scheduled message cancelled, taken back, sent or interrupted), with when. Not the journal, whose entries go
    // after a day: an id that came back from a client a day later must still be refused. Made where missing, without a new user_version, like the imports.
    private const string RetiredTable = "CREATE TABLE IF NOT EXISTS retired (id TEXT PRIMARY KEY, at INTEGER NOT NULL)";

    /// <summary>Whether <paramref name="id"/> was let go of by a commit that retired it (<see cref="WriteOwnBatchAsync"/>), however long ago.</summary>
    internal async Task<bool> IsRetiredAsync(string id, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = RetiredTable;
                command.ExecuteNonQuery();
                return Query(connection, "SELECT 1 FROM retired WHERE id = $id", r => 1, ("$id", NotificationIdentity.Hash(id))).Count > 0;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // Never pruned: a retirement is a 64-character hash and a number, and no age is long enough to prove that no client still holds the id (a device kept away for
    // two years holds its stand-in as long, and the id coming back into use would send the message a second time).
    /// <summary>Retires ids with no row to remove: a message that was never made here and, once this returns, never will be (a create for it is refused).</summary>
    internal async Task RetireAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                Execute(connection, transaction, RetiredTable);
                foreach (var id in ids)
                    Execute(connection, transaction, "INSERT OR IGNORE INTO retired(id, at) VALUES($id, $now)", ("$id", NotificationIdentity.Hash(id)), ("$now", now));
                transaction.Commit();
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // ---- state.sync ----

    public async Task<StateSyncResult> SyncAsync(StateSyncParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(request.Limit ?? SharedLimits.PageRows, 1, SharedLimits.PageRows);
        // Rows of a space whose key cannot be served are not handed out: a client would keep them under a key the server is not filing under.
        if (spaced) await FilingAsync("sync", cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ImportsAsync().ConfigureAwait(false);
            var (meta, reset, page, more) = await Task.Run(() =>
            {
                using var connection = Open();
                var meta = ReadMeta(connection);
                var since = request.Since;
                // Another file, a position this file never reached, or one older than the deletes it still remembers: a snapshot. A client still reading a
                // snapshot holds nothing older than it, so the horizon is no reason to start it again.
                var reset = since is null || !string.Equals(since.Epoch, meta.Epoch, StringComparison.Ordinal) || since.Rev > meta.Rev || since.Rev < 0
                    || !request.Snapshot && since.Rev < meta.Horizon;
                var (page, more) = ReadPage(connection, reset ? 0 : since!.Rev, onlyLive: reset, limit);
                return (meta, reset, page, more);
            }, cancellationToken).ConfigureAwait(false);
            // Rows of a category the caller's version does not know are left out, after the page is cut, so the positions are the same for every caller.
            var rows = await RowsAsync([.. page.Where(row => row.Since <= protocol)], SharedLimits.PageBytes, SharedLimits.InlineBytes, firstAlways: false).ConfigureAwait(false);
            // A page that stops early ends on a whole commit, so the client holds everything up to its last rev; a last page brings it to the latest.
            return new StateSyncResult(meta.Epoch, more ? page[^1].Rev : meta.Rev, reset, rows, more);
        }
        finally { gate.Release(); }
    }

    // Rows after `after` in commit order, whole commits only: at most `limit` rows, unless one commit alone is larger.
    private static (List<Stored> Page, bool More) ReadPage(SqliteConnection connection, long after, bool onlyLive, int limit)
    {
        var fetch = limit + SharedLimits.PutWrites + 1;
        var fetched = Query(connection, $"SELECT key, payload, rev, deleted, size, since FROM rows WHERE rev > $after{(onlyLive ? " AND deleted = 0" : "")} ORDER BY rev, key LIMIT $limit",
            ("$after", after), ("$limit", fetch));
        var end = fetched.Count < fetch;
        var page = new List<Stored>();
        var index = 0;
        while (index < fetched.Count)
        {
            var rev = fetched[index].Rev;
            var next = index;
            while (next < fetched.Count && fetched[next].Rev == rev) next++;
            // The last commit fetched may go on past what was fetched.
            if (next == fetched.Count && !end) break;
            if (page.Count > 0 && page.Count + (next - index) > limit) break;
            page.AddRange(fetched.GetRange(index, next - index));
            index = next;
        }
        if (page.Count == 0 && fetched.Count > 0)
        {
            // One commit larger than the fetch: it comes whole, alone.
            page = Query(connection, $"SELECT key, payload, rev, deleted, size, since FROM rows WHERE rev = $rev{(onlyLive ? " AND deleted = 0" : "")} ORDER BY key", ("$rev", fetched[0].Rev));
            return (page, Query(connection, $"SELECT key, payload, rev, deleted, size, since FROM rows WHERE rev > $after{(onlyLive ? " AND deleted = 0" : "")} LIMIT 1",
                ("$after", page[^1].Rev)).Count > 0);
        }
        return (page, index < fetched.Count || !end);
    }

    // ---- state.get ----

    public async Task<StateGetResult> GetAsync(StateGetParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default)
    {
        var keys = request.Keys ?? [];
        if (keys.Count is 0 or > SharedLimits.GetKeys || keys.Any(key => key is not { Length: > 0 and <= SharedLimits.KeyLength }))
            throw Refused("get", "shape");
        if (spaced) await FilingAsync("get", cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ImportsAsync().ConfigureAwait(false);
            var found = await Task.Run(() =>
            {
                using var connection = Open();
                var rows = new List<Stored>();
                foreach (var key in keys.Distinct(StringComparer.Ordinal))
                    rows.AddRange(Query(connection, "SELECT key, payload, rev, deleted, size, since FROM rows WHERE key = $key", ("$key", key)));
                return rows;
            }, cancellationToken).ConfigureAwait(false);
            // The first value always comes, whatever its size, so a client that asks again for what came by size gets further each time.
            return new StateGetResult(await RowsAsync([.. found.Where(row => row.Since <= protocol)], SharedLimits.PageBytes, SharedLimits.ValueBytes, firstAlways: true)
                .ConfigureAwait(false));
        }
        finally { gate.Release(); }
    }

    // ---- state.put ----

    public async Task<StatePutResult> PutAsync(StatePutParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default)
    {
        var writes = request.Writes ?? [];
        if (request.ClientOpId is not { Length: > 0 and <= 128 } || writes.Count is 0 or > SharedLimits.PutWrites
            || writes.Any(write => write?.Key is not { Length: > 0 and <= SharedLimits.KeyLength } || write.IfRev < 0)
            || writes.Select(write => write.Key).Distinct(StringComparer.Ordinal).Count() != writes.Count)
            throw Refused("put", "shape");
        var fingerprint = OperationJournal.Fingerprint(ProtocolMethods.StatePut, ProtocolJson.ToElement(request));
        // Read before the gate: the identity may read a file.
        var filing = await FilingAsync("put", cancellationToken).ConfigureAwait(false);
        var filedUnder = filing.Profile;
        // The values as the rows will hold them: the JSON text, which is what a client reads back.
        var values = writes.Select(write => write.Value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } value ? Encoding.UTF8.GetBytes(value.GetRawText()) : null).ToArray();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Ahead of any write, so an import adds only where nobody has written since.
            await ImportsAsync().ConfigureAwait(false);
            // First, so an answer past its week no longer answers for its id.
            await PruneAsync().ConfigureAwait(false);
            var earlier = await Task.Run(() =>
            {
                using var connection = Open();
                var found = Query(connection, "SELECT fingerprint, result FROM applied WHERE client_op_id = $id", r => (Fingerprint: r.GetString(0), Result: (byte[])r[1]),
                    ("$id", request.ClientOpId));
                return found.Count == 0 ? ((string Fingerprint, byte[] Result)?)null : found[0];
            }, CancellationToken.None).ConfigureAwait(false);
            // A repeat: its first answer, and nothing written again. Looked for before the keys are judged, so a batch the server took is answered as it was
            // even if what is shared has changed since.
            if (earlier is { } first)
            {
                if (!string.Equals(first.Fingerprint, fingerprint, StringComparison.Ordinal)) throw Refused("put", "reused");
                log?.Invoke("shared put repeat");
                return JsonSerializer.Deserialize<StatePutResult>(await protector.UnprotectAsync(first.Result).ConfigureAwait(false), ProtocolJson.Options)
                    ?? throw new InvalidDataException("An applied answer could not be read.");
            }
            // What the caller's version shares under the profile the server files keys under now. Any other key is refused by itself and never stored, so a
            // client can tell "the server will never keep this" from a failure worth trying again. A row only the server writes (the scheduled sends) is one of
            // those: a put is never a way to schedule, change or fire a message.
            var categories = writes.Select(write => SharedCategories.Match(write.Key, filedUnder, protocol) is { HostOwned: false } category ? category : null).ToArray();
            if (categories.Any(category => category is null)) log?.Invoke($"shared put refused reason=key n={categories.Count(category => category is null)}");

            var (meta, current) = await Task.Run(() =>
            {
                using var connection = Open();
                var current = new Dictionary<string, Stored>(StringComparer.Ordinal);
                foreach (var write in writes)
                    foreach (var row in Query(connection, "SELECT key, payload, rev, deleted, size, since FROM rows WHERE key = $key", ("$key", write.Key)))
                        current[row.Key] = row;
                return (ReadMeta(connection), current);
            }, CancellationToken.None).ConfigureAwait(false);

            var written = new List<int>();
            var results = new SharedWriteResult[writes.Count];
            var rev = meta.Rev + 1;
            // Revs start again in a space made anew, so a condition made in another space can hold by chance for a different row: it never holds.
            var foreignEpoch = request.Epoch is { } known && !string.Equals(known, meta.Epoch, StringComparison.Ordinal);
            for (var i = 0; i < writes.Count; i++)
            {
                var write = writes[i];
                var row = current.GetValueOrDefault(write.Key);
                var live = row is { Deleted: false };
                // Not a key this caller may keep here: nothing of the server's row is told, not even its rev.
                if (categories[i] is null)
                    results[i] = new SharedWriteResult(write.Key, SharedWriteStatus.Refused, 0);
                else if (values[i] is { Length: > SharedLimits.ValueBytes })
                    results[i] = new SharedWriteResult(write.Key, SharedWriteStatus.Refused, row?.Rev ?? 0);
                // ifRev 0 creates only; any other holds only while the row is at that rev.
                else if (write.IfRev is { } expected && (foreignEpoch || (expected == 0 ? live : row?.Rev != expected)))
                    results[i] = new SharedWriteResult(write.Key, SharedWriteStatus.Conflict, row?.Rev ?? 0,
                        row is null ? null : (await RowsAsync([row], SharedLimits.InlineBytes, SharedLimits.InlineBytes, firstAlways: false).ConfigureAwait(false))[0]);
                else
                {
                    written.Add(i);
                    results[i] = new SharedWriteResult(write.Key, SharedWriteStatus.Written, rev);
                }
            }
            // The first row kept in a space pins the key it was judged under, before it is kept. A pin that does not hold (another key was pinned since the
            // filing was read) keeps nothing, and the client sends the same operation again later.
            if (written.Count > 0 && filing.Unpinned is { } unpinned && await Space.PinAsync(unpinned, "write", cancellationToken).ConfigureAwait(false) == SpacePin.Refused)
            {
                log?.Invoke("shared put refused reason=space");
                throw SpaceUnavailableException.Answer();
            }
            var result = new StatePutResult(written.Count > 0 ? rev : meta.Rev, results);
            var sealedResult = await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(result, ProtocolJson.Options)).ConfigureAwait(false);
            var payloads = new byte[]?[writes.Count];
            foreach (var i in written)
                if (values[i] is { } value) payloads[i] = await protector.ProtectAsync(value).ConfigureAwait(false);
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            await Task.Run(() =>
            {
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                foreach (var i in written)
                    Execute(connection, transaction, "INSERT INTO rows(key, payload, rev, deleted, size, since, updated) VALUES($key, $payload, $rev, $deleted, $size, $since, $now) " +
                        "ON CONFLICT(key) DO UPDATE SET payload = excluded.payload, rev = excluded.rev, deleted = excluded.deleted, size = excluded.size, since = excluded.since, updated = excluded.updated",
                        ("$key", writes[i].Key), ("$payload", (object?)payloads[i] ?? DBNull.Value), ("$rev", rev), ("$deleted", values[i] is null ? 1 : 0),
                        ("$size", values[i]?.Length ?? 0), ("$since", categories[i]!.Since), ("$now", now));
                if (written.Count > 0) Execute(connection, transaction, "UPDATE meta SET rev = $rev", ("$rev", rev));
                Execute(connection, transaction, "INSERT INTO applied(client_op_id, fingerprint, rev, result, at) VALUES($id, $fingerprint, $rev, $result, $now)",
                    ("$id", request.ClientOpId), ("$fingerprint", fingerprint), ("$rev", result.Rev), ("$result", sealedResult), ("$now", now));
                transaction.Commit();
            }, CancellationToken.None).ConfigureAwait(false);
            if (written.Count > 0)
            {
                // Under the gate, so the next commit's event cannot overtake this one's. The values the client wrote go out as they came.
                var budget = SharedLimits.EventBytes;
                var rows = new List<SharedRow>(written.Count);
                foreach (var i in written)
                {
                    var size = values[i]?.Length ?? 0;
                    var inline = values[i] is not null && size <= SharedLimits.InlineBytes && size <= budget;
                    if (inline) budget -= size;
                    // Since: the dispatcher hands each connection only the rows its version knows.
                    rows.Add(new SharedRow(writes[i].Key, rev, inline ? writes[i].Value : null, values[i] is null, size) { Since = categories[i]!.Since });
                }
                try { Publish(new SharedChange(meta.Epoch, rev, rows)); } catch (Exception) { /* a log that fails must not undo a commit; clients catch up with state.sync */ }
            }
            // A row kept means the space is pinned under this profile: private reading's defaults, after this put's own commit and event.
            if (written.Count > 0 && readingDefaults && !readingDefaultsLooked && spaced && filedUnder is not null) await ReadingDefaultsAsync(filedUnder).ConfigureAwait(false);
            return result;
        }
        finally { gate.Release(); }
    }

    private ProtocolException Refused(string method, string reason)
    {
        log?.Invoke($"shared {method} refused reason={reason}");
        return new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The server does not keep that."));
    }

    // At most once an hour, under the gate: answers past their week (or past the count), and tombstones past their 90 days, which raise the horizon.
    private async Task PruneAsync()
    {
        var now = clock.GetUtcNow();
        if (now - pruned < PruneEvery) return;
        pruned = now;
        var (answers, tombstones) = await Task.Run(() =>
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var answers = Execute(connection, transaction, "DELETE FROM applied WHERE at < $cutoff", ("$cutoff", (now - AppliedKept).ToUnixTimeMilliseconds()));
            answers += Execute(connection, transaction,
                $"DELETE FROM applied WHERE client_op_id NOT IN (SELECT client_op_id FROM applied ORDER BY at DESC LIMIT {AppliedLimit})");
            var cutoff = (now - TombstonesKept).ToUnixTimeMilliseconds();
            var newest = Query(connection, "SELECT MAX(rev) FROM rows WHERE deleted = 1 AND updated < $cutoff", r => r.IsDBNull(0) ? 0L : r.GetInt64(0), transaction, ("$cutoff", cutoff))[0];
            var tombstones = 0;
            if (newest > 0)
            {
                Execute(connection, transaction, "UPDATE meta SET horizon = MAX(horizon, $rev)", ("$rev", newest));
                tombstones = Execute(connection, transaction, "DELETE FROM rows WHERE deleted = 1 AND rev <= $rev", ("$rev", newest));
            }
            transaction.Commit();
            return (answers, tombstones);
        }).ConfigureAwait(false);
        if (answers > 0 || tombstones > 0) log?.Invoke($"shared store pruned applied={answers} tombstones={tombstones}");
    }

    // ---- The space's rows ----

    // How many rows the file holds (tombstones too: their keys were made under a profile as well), how many are one fixed row of a profile and so can say
    // which profile they were made under, and how many of those were made under `filedUnder`. A missing file is no rows, and is not made by asking.
    private async Task<(int Rows, int Checkable, int Matching)> RowsUnderAsync(string? filedUnder)
    {
        if (!File.Exists(path)) return (0, 0, 0);
        var keys = await Task.Run(() =>
        {
            using var connection = Open();
            return Query(connection, "SELECT key FROM rows", r => r.GetString(0));
        }).ConfigureAwait(false);
        int checkable = 0, matching = 0;
        // Every fixed row of a category is a candidate (the quick replies and the pinned order share "preferences"), so a key matches when it is any of them
        // under the profile; the first category alone would never match the pinned order.
        var fixedRows = SharedCategories.All.Where(category => category.Row is not null).ToArray();
        HashSet<string> under = filedUnder is null ? [] : fixedRows.Select(category => SharedCategories.KeyOf(filedUnder, category.Category, category.Row!)).ToHashSet(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!fixedRows.Any(category => key.StartsWith(category.Category + ":", StringComparison.Ordinal) && key.Length == category.Category.Length + 1 + 64)) continue;
            checkable++;
            if (under.Contains(key)) matching++;
        }
        return (keys.Count, checkable, matching);
    }

    /// <summary>
    /// How many scheduled messages (<c>scheduled:</c>) and sends that may not have gone through (<c>outbox:</c>) a data folder's <c>Server\shared.db</c> holds live, read without
    /// making or changing anything (a file that is not there holds none). Null when the file is there and cannot be read. For <c>--reset-space</c>, which must never throw
    /// away a message a person asked the server to send.
    /// </summary>
    public static (int Scheduled, int Unsettled)? LiveSends(string dataRoot)
    {
        var file = Path.Combine(dataRoot, "Server", FileName);
        if (!File.Exists(file)) return (0, 0);
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(substr(key, 1, 10) = 'scheduled:'), 0), COALESCE(SUM(substr(key, 1, 7) = 'outbox:'), 0) FROM rows WHERE deleted = 0";
            using var reader = command.ExecuteReader();
            return reader.Read() ? ((int)reader.GetInt64(0), (int)reader.GetInt64(1)) : (0, 0);
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>
    /// The retired ids (their digests, with when) of a data folder's <c>Server\shared.db</c>, read without changing what it holds; none when the file or the table is not there, null
    /// when the file is there and cannot be read, or its write-ahead log cannot be emptied into it. For <c>--reset-space</c>, which starts the space again but must not let a message
    /// go out a second time (<see cref="CreateFresh"/>), and which then deletes the <c>-wal</c>: emptied first, so a reset that stops after deleting it has lost no commit, retired ids included.
    /// </summary>
    public static IReadOnlyList<(string Id, long At)>? RetiredRows(string dataRoot)
    {
        var file = Path.Combine(dataRoot, "Server", FileName);
        if (!File.Exists(file)) return [];
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            // busy (the first column) is 1 when a reader or writer kept the log from being emptied whole.
            if (Query(connection, "PRAGMA wal_checkpoint(TRUNCATE)", r => r.GetInt64(0)) is not [0]) return null;
            if (Query(connection, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'retired'", r => 1).Count == 0) return [];
            return Query(connection, "SELECT id, at FROM retired", r => (r.GetString(0), r.GetInt64(1)));
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>
    /// Makes a shared state file with no rows, a new epoch and the given retired ids, replacing whatever is at <paramref name="file"/>. Complete when it returns, so it can be
    /// moved over a database in one step: a reset that stops half way leaves either the old file or this one, never rows without their retired record.
    /// <paramref name="keepPrivate"/>: a space started anew (<c>--reset-space</c>) is never given private reading's defaults (<see cref="GiveReadingDefaults"/>).
    /// </summary>
    public static void CreateFresh(string file, IEnumerable<(string Id, long At)> retired, bool keepPrivate = false)
    {
        File.Delete(file);
        using var connection = OpenFile(file, null);
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, RetiredTable);
        foreach (var (id, at) in retired) Execute(connection, transaction, "INSERT OR IGNORE INTO retired(id, at) VALUES($id, $at)", ("$id", id), ("$at", at));
        if (keepPrivate)
            Execute(connection, transaction, "INSERT OR IGNORE INTO imports(name, added, skipped, refused, at) VALUES($name, 0, 0, 0, $now)",
                ("$name", ReadingDefaultsName), ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        transaction.Commit();
    }

    /// <summary>A live row as the file keeps it (its value still sealed), with the protocol version its category came in.</summary>
    internal sealed record SealedRow(string Key, byte[] Sealed, int Since);

    /// <summary>
    /// Every live row of a shared state file (tombstones are left: the file a restore makes is a new epoch, which every client takes whole) and its retired ids, read in one
    /// transaction without changing anything, for the server's backup. Nothing when the file is not there.
    /// </summary>
    internal static (IReadOnlyList<SealedRow> Rows, IReadOnlyList<(string Id, long At)> Retired) ReadSealed(string file, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(file)) return ([], []);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var rows = Query(connection, "SELECT key, payload, since FROM rows WHERE deleted = 0 AND payload IS NOT NULL ORDER BY key",
            r => new SealedRow(r.GetString(0), (byte[])r[1], r.GetInt32(2)), transaction);
        cancellationToken.ThrowIfCancellationRequested();
        var retired = Query(connection, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'retired'", r => 1, transaction).Count == 0 ? []
            : Query(connection, "SELECT id, at FROM retired", r => (r.GetString(0), r.GetInt64(1)), transaction);
        return (rows, retired);
    }

    /// <summary>
    /// Makes a shared state file holding <paramref name="rows"/> (sealed by the caller, with the server's own protector), all under one first commit, and
    /// <paramref name="retired"/>, under a new epoch (<see cref="CreateFresh"/>), replacing whatever is at <paramref name="file"/>. What a restore puts in place.
    /// </summary>
    internal static void CreateRestored(string file, IEnumerable<SealedRow> rows, Func<SealedRow, int> size, IEnumerable<(string Id, long At)> retired, DateTimeOffset now)
    {
        foreach (var leftover in new[] { file + "-wal", file + "-shm", file + "-journal" }) if (File.Exists(leftover)) File.Delete(leftover);
        // Never given private reading's defaults: a backup carries live rows only, so one taken of a space started anew (or with its choices deleted) would
        // otherwise come back as a space nobody had chosen in.
        CreateFresh(file, retired, keepPrivate: true);
        using var connection = OpenFile(file, null);
        using var transaction = connection.BeginTransaction();
        // A rev for every SharedLimits.PutWrites rows, as AddAbsentAsync gives them: a commit is never split across state.sync pages, so one rev for every row
        // made the first sync after a restore a single page past the row bound and, with enough rows, past the frame limit.
        var count = 0;
        foreach (var row in rows)
        {
            Execute(connection, transaction, "INSERT INTO rows(key, payload, rev, deleted, size, since, updated) VALUES($key, $payload, $rev, 0, $size, $since, $now)",
                ("$key", row.Key), ("$payload", row.Sealed), ("$rev", 1 + count / SharedLimits.PutWrites), ("$size", size(row)), ("$since", row.Since),
                ("$now", now.ToUnixTimeMilliseconds()));
            count++;
        }
        if (count > 0) Execute(connection, transaction, "UPDATE meta SET rev = $rev", ("$rev", 1 + (count - 1) / SharedLimits.PutWrites));
        transaction.Commit();
    }

    // ---- The file ----

    private SqliteConnection Open() => OpenFile(path, log);

    private static SqliteConnection OpenFile(string path, Action<string>? log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version > 1) throw new InvalidDataException("The shared state needs a newer server.");
            if (version == 0)
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                command.Transaction = transaction;
                command.CommandText = "PRAGMA user_version";
                if (Convert.ToInt32(command.ExecuteScalar()) == 0)
                {
                    command.CommandText =
                        "CREATE TABLE IF NOT EXISTS rows (key TEXT PRIMARY KEY, payload BLOB, rev INTEGER NOT NULL, deleted INTEGER NOT NULL, size INTEGER NOT NULL, since INTEGER NOT NULL, updated INTEGER NOT NULL);" +
                        "CREATE INDEX IF NOT EXISTS rows_rev ON rows(rev);" +
                        "CREATE TABLE IF NOT EXISTS meta (id INTEGER PRIMARY KEY CHECK (id = 1), epoch TEXT NOT NULL, rev INTEGER NOT NULL, horizon INTEGER NOT NULL);" +
                        "CREATE TABLE IF NOT EXISTS applied (client_op_id TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, rev INTEGER NOT NULL, result BLOB NOT NULL, at INTEGER NOT NULL);" +
                        "CREATE INDEX IF NOT EXISTS applied_at ON applied(at);" +
                        // What was taken in once from elsewhere (ImportOnce). Made again where missing by the import itself, without a new user_version, for a
                        // file from before slice 3, which the build that made it still opens.
                        ImportsTable + ";" +
                        "INSERT INTO meta(id, epoch, rev, horizon) VALUES(1, $epoch, 0, 0);" +
                        "PRAGMA user_version=1;";
                    // The epoch is made only here, with the file: every position a client holds from another file is then from another epoch.
                    command.Parameters.AddWithValue("$epoch", Guid.NewGuid().ToString("N"));
                    command.ExecuteNonQuery();
                    log?.Invoke("shared store created");
                }
                transaction.Commit();
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static Meta ReadMeta(SqliteConnection connection) =>
        Query(connection, "SELECT epoch, rev, horizon FROM meta WHERE id = 1", r => new Meta(r.GetString(0), r.GetInt64(1), r.GetInt64(2))).Single();

    // The rows as they go out: a value no larger than `largest` and within the budget comes with its row, the rest by their size. With `firstAlways` the
    // first value comes whatever its size, so a reader that asks again for what came by size always gets further.
    private async Task<IReadOnlyList<SharedRow>> RowsAsync(IReadOnlyList<Stored> rows, int budget, int largest, bool firstAlways)
    {
        var result = new List<SharedRow>(rows.Count);
        var sent = false;
        foreach (var row in rows)
        {
            if (row.Deleted || row.Payload is null)
            {
                result.Add(new SharedRow(row.Key, row.Rev, null, true, 0));
                continue;
            }
            var inline = firstAlways && !sent || row.Size <= largest && row.Size <= budget;
            sent |= inline;
            if (!inline)
            {
                result.Add(new SharedRow(row.Key, row.Rev, null, false, row.Size));
                continue;
            }
            budget -= row.Size;
            var value = JsonSerializer.Deserialize<JsonElement>(await protector.UnprotectAsync(row.Payload).ConfigureAwait(false));
            result.Add(new SharedRow(row.Key, row.Rev, value, false, row.Size));
        }
        return result;
    }

    private static List<Stored> Query(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        Query(connection, sql, r => new Stored(r.GetString(0), r.IsDBNull(1) ? null : (byte[])r[1], r.GetInt64(2), r.GetInt64(3) != 0, r.GetInt32(4), r.GetInt32(5)), parameters);

    private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> read, params (string Name, object Value)[] parameters) =>
        Query(connection, sql, read, null, parameters);

    private static List<T> Query<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> read, SqliteTransaction? transaction, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var items = new List<T>();
        while (reader.Read()) items.Add(read(reader));
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
}

/// <summary>A commit would have made a row for an id the server has retired for good; it kept nothing (<see cref="SharedStateStore.WriteOwnBatchAsync"/>).</summary>
/// <summary>A server write held to a rev found the row at another one (<see cref="SharedStateStore.WriteOwnBatchAsync"/>): nothing was kept.</summary>
internal sealed class SharedRowMovedException() : InvalidOperationException("The row changed since it was read.");

internal sealed class RetiredIdException(IReadOnlyList<string> ids) : InvalidOperationException("That message has already gone from the server.")
{
    /// <summary>Every id of the commit that is retired.</summary>
    public IReadOnlyList<string> Ids { get; } = ids;
}
