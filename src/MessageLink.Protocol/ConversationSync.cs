using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// Recheck names rows the client changed by events or pushes after its last revision. Even a row that changed back to its
// baseline (or appeared and disappeared between snapshots) must be reconciled, rather than leaving the intermediate copy.
public sealed record ChatsSyncParams(string? Since = null, string? Page = null, IReadOnlyList<string>? Recheck = null);
/// <summary>Reset replaces the list (except unavailable scopes); otherwise apply upserts and removals. Commit Revision only after NextPage ends.
/// Restart means a paged read expired: discard that read and start again without a page, from the same Since. No cursor is committed for a partial
/// provider read, whose removals name only conversations outside its unavailable scopes.</summary>
public sealed record ChatsSyncResult(IReadOnlyList<ChatSummary> Items, IReadOnlyList<string> Removed, bool Reset,
    string? Revision = null, string? NextPage = null, bool Restart = false, IReadOnlyList<string>? UnavailableScopes = null);

/// <summary>
/// A list revision is independent of the event ring and survives a nightly restart. Only ids and truncated SHA-256 fingerprints are retained, in the
/// server's protected store. Comparing a fresh provider enumeration also catches changes while the server was down, which an event-only journal cannot do.
/// The expensive provider reads stay on the server; only changed rows cross the phone's connection. No provider mutation is involved.
/// </summary>
/// <param name="maxTransfers">Paged reads held at once; the sample server's --sync-transfers 0 holds none, so every page after the first says restart.</param>
public sealed class ConversationSync(IChatReader chats, IPrivateStateStore? store = null, Action<string>? log = null, int maxTransfers = ConversationSync.MaxTransfers)
{
    private const string Key = "conversation-sync:v2", FirstKey = "conversation-sync:v1";
    public const int RetainedRevisions = 8, PageSize = 50, MaxRows = 20_000, MaxTransfers = 8;
    /// <summary>What is written to the store at most: the protected store refuses a value over 16 MB (SqlitePrivateStateStore), and the oldest
    /// revisions are left out to stay under this. 20,000 rows take about 0.5 MB a revision with 40-character ids.</summary>
    public const int MaxStoredBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private List<Snapshot>? snapshots;
    // Oldest first: a full set gives up the transfer made longest ago, never the one just made.
    private readonly OrderedDictionary<string, Transfer> transfers = new(StringComparer.Ordinal);
    private sealed record Snapshot(string Revision, Dictionary<string, UInt128> Hashes);
    private sealed record Transfer(ChatsSyncResult Result, ChatSummary[] Items, string[] Removed, string? Since);
    // Each id once, and each revision as indexes into them with 16 bytes of fingerprint a row: the same rows in eight revisions were eight copies of
    // every id and a 64-character hash, past the store's limit at about 9,000 rows.
    private sealed record Stored(string[] Ids, StoredRevision[] Revisions);
    private sealed record StoredRevision(string Revision, int[] Rows, byte[] Hashes);

    /// <summary>Whether revisions outlive this process (a store was given). Every server composition passes one (LinuxServerChecks).</summary>
    public bool Persistent => store is not null;

    public async Task<ChatsSyncResult> ReadAsync(ChatsSyncParams request, CancellationToken ct = default)
    {
        if (request.Since?.Length > 128 || request.Page?.Length > 128 || request.Recheck?.Count > MaxRows
            || request.Recheck?.Any(id => string.IsNullOrEmpty(id) || id.Length > 4096) == true) throw new ArgumentException("Invalid list position.");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (request.Page is { } page)
            {
                var parts = page.Split('.');
                if (parts.Length != 2 || !transfers.TryGetValue(parts[0], out var transfer) || transfer.Since != request.Since
                    || !int.TryParse(parts[1], out var offset) || offset < 0 || offset >= transfer.Items.Length + transfer.Removed.Length)
                    return new([], [], false, Restart: true);
                return Next(parts[0], transfer, offset);
            }
            var clock = Stopwatch.GetTimestamp();
            snapshots ??= await LoadAsync(ct).ConfigureAwait(false);
            var baseline = snapshots.FirstOrDefault(s => s.Revision == request.Since);
            var rows = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
            var unavailable = new HashSet<string>(StringComparer.Ordinal);
            var positions = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var pages = 0;
            while (true)
            {
                var read = await chats.GetChatsAsync(cursor, ct).ConfigureAwait(false);
                pages++;
                var added = 0;
                foreach (var row in read.Items)
                {
                    if (!rows.ContainsKey(row.Id)) added++;
                    rows[row.Id] = row;
                }
                unavailable.UnionWith(read.UnavailableScopes ?? []);
                if (rows.Count > MaxRows) throw new InvalidOperationException("Conversation list too large.");
                if (!read.HasMore) break;
                var next = read.OldestCursor;
                var stuck = next is null || !positions.Add(next) || pages >= MaxRows;
                if (unavailable.Count > 0)
                {
                    // A provider that did not answer hands back a retry cursor for as long as it does not (PartialChatPages), and the router keeps
                    // paging the others beside it (Google Voice). Go on while pages bring rows, and stop at the first that brings none: the first page
                    // alone left most of the list unread whenever one Voice account or a restarting Google was not there yet.
                    if (stuck || added == 0) break;
                }
                // A truncated list must never be published as a complete revision (nor imply deletions).
                else if (stuck) throw new InvalidOperationException("Incomplete conversation list.");
                cursor = next;
            }
            var hashes = rows.ToDictionary(p => p.Key, p => Fingerprint(p.Value), StringComparer.Ordinal);
            var recheck = (request.Recheck ?? []).ToHashSet(StringComparer.Ordinal);
            var changed = rows.Values.Where(row => baseline is null || recheck.Contains(row.Id)
                || !baseline.Hashes.TryGetValue(row.Id, out var was) || was != hashes[row.Id]).ToArray();
            // A conversation of a service that did not answer is no more gone than it was: only the others' absences are removals, as the client's
            // own whole list kept those scopes (keepScopes).
            bool Unanswered(string id) => unavailable.Any(scope => id.StartsWith(scope, StringComparison.Ordinal));
            var removed = baseline is null ? [] : baseline.Hashes.Keys.Union(recheck).Where(id => !rows.ContainsKey(id) && !Unanswered(id)).ToArray();
            string? revision = null;
            if (unavailable.Count == 0)
            {
                if (baseline is not null && hashes.Count == baseline.Hashes.Count && hashes.All(p => baseline.Hashes.TryGetValue(p.Key, out var was) && was == p.Value)) revision = baseline.Revision;
                else
                {
                    revision = Guid.NewGuid().ToString("N");
                    var kept = snapshots.Append(new Snapshot(revision, hashes)).TakeLast(RetainedRevisions).ToList();
                    // Persisted before handing out the cursor. A failed write still serves the list: the revision then lasts until this process ends,
                    // and a client holding it after a restart is given the whole list again, as with any revision it does not know.
                    await PersistAsync(kept, ct).ConfigureAwait(false);
                    snapshots = kept;
                }
            }
            var result = new ChatsSyncResult([], [], baseline is null, revision, UnavailableScopes: unavailable.ToArray());
            log?.Invoke($"conversation sync reset={result.Reset} scanned={rows.Count} changed={changed.Length} removed={removed.Length} pages={pages} partial={unavailable.Count > 0} ms={(long)Stopwatch.GetElapsedTime(clock).TotalMilliseconds}");
            var transferId = Guid.NewGuid().ToString("N");
            var sending = new Transfer(result, changed, removed, request.Since);
            if (changed.Length + removed.Length > PageSize && maxTransfers > 0)
            {
                while (transfers.Count >= maxTransfers) transfers.RemoveAt(0);
                transfers.Add(transferId, sending);
            }
            return Next(transferId, sending, 0);
        }
        finally { gate.Release(); }
    }

    private ChatsSyncResult Next(string id, Transfer transfer, int offset)
    {
        var items = transfer.Items.Skip(offset).Take(PageSize).ToArray();
        var removed = transfer.Removed.Skip(Math.Max(0, offset - transfer.Items.Length)).Take(PageSize - items.Length).ToArray();
        var nextOffset = offset + items.Length + removed.Length;
        string? next = null;
        // Bounded immutable transfers: retries return the same page as events and other clients change the list. Once its last page is served it is
        // done; a retry of that page is told to restart, from the same revision.
        if (nextOffset < transfer.Items.Length + transfer.Removed.Length) next = id + "." + nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else transfers.Remove(id);
        return transfer.Result with { Items = items, Removed = removed, NextPage = next };
    }

    private static UInt128 Fingerprint(ChatSummary row)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(row, ProtocolJson.Options), hash);
        return BinaryPrimitives.ReadUInt128LittleEndian(hash);
    }

    // A store that cannot be read (a key that no longer opens it, a damaged value) serves the list as if no revision were known: each client is sent
    // the whole list once, rather than the read failing and the client keeping a copy that never updates.
    private async Task<List<Snapshot>> LoadAsync(CancellationToken ct)
    {
        if (store is null) return [];
        try
        {
            // The first format kept whole hashes per revision; nothing of it is reused, and it can be several megabytes.
            await store.DeleteAsync(FirstKey, ct).ConfigureAwait(false);
            var bytes = await store.ReadAsync(Key, ct).ConfigureAwait(false);
            if (bytes is null) return [];
            var stored = JsonSerializer.Deserialize<Stored>(bytes, ProtocolJson.Options);
            if (stored?.Ids is null || stored.Revisions is null) return [];
            var loaded = new List<Snapshot>();
            foreach (var revision in stored.Revisions)
            {
                if (revision?.Revision is null || revision.Rows is null || revision.Hashes is null || revision.Hashes.Length != revision.Rows.Length * 16) return [];
                var hashes = new Dictionary<string, UInt128>(revision.Rows.Length, StringComparer.Ordinal);
                for (var i = 0; i < revision.Rows.Length; i++)
                {
                    var row = revision.Rows[i];
                    if (row < 0 || row >= stored.Ids.Length) return [];
                    hashes[stored.Ids[row]] = BinaryPrimitives.ReadUInt128LittleEndian(revision.Hashes.AsSpan(i * 16, 16));
                }
                loaded.Add(new(revision.Revision, hashes));
            }
            return loaded;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log?.Invoke($"conversation sync store unreadable type={e.GetType().Name}");
            return [];
        }
    }

    private async Task PersistAsync(List<Snapshot> kept, CancellationToken ct)
    {
        if (store is null) return;
        var keep = kept.Count;
        byte[] bytes;
        while (true)
        {
            bytes = Serialize(kept.Skip(kept.Count - keep));
            if (bytes.Length <= MaxStoredBytes || keep == 1) break;
            keep--;
        }
        if (keep < kept.Count) log?.Invoke($"conversation sync store kept={keep} of={kept.Count} bytes={bytes.Length}");
        if (bytes.Length > MaxStoredBytes) { log?.Invoke($"conversation sync store skipped bytes={bytes.Length}"); return; }
        try { await store.WriteAsync(Key, bytes, ct).ConfigureAwait(false); }
        catch (Exception e) when (!ct.IsCancellationRequested) { log?.Invoke($"conversation sync store failed type={e.GetType().Name}"); }
    }

    private static byte[] Serialize(IEnumerable<Snapshot> kept)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var ids = new List<string>();
        var revisions = new List<StoredRevision>();
        foreach (var snapshot in kept)
        {
            var rows = new int[snapshot.Hashes.Count];
            var hashes = new byte[snapshot.Hashes.Count * 16];
            var i = 0;
            foreach (var (id, hash) in snapshot.Hashes)
            {
                if (!index.TryGetValue(id, out var row)) { row = ids.Count; index[id] = row; ids.Add(id); }
                rows[i] = row;
                BinaryPrimitives.WriteUInt128LittleEndian(hashes.AsSpan(i * 16, 16), hash);
                i++;
            }
            revisions.Add(new(snapshot.Revision, rows, hashes));
        }
        return JsonSerializer.SerializeToUtf8Bytes(new Stored([.. ids], [.. revisions]), ProtocolJson.Options);
    }
}
