using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;
using NativeBeeper.Storage;

namespace NativeBeeper.Server.Linux;

/// <summary>The host's hooks into the providers it runs: what it needs to stop, start and ask about them. The Linux server fills them from its composition; the harness fakes them.</summary>
internal interface IAccountRunner
{
    /// <summary>Whether the server is signed in to the provider's service now.</summary>
    bool Connected(string provider);

    /// <summary>For Messenger: whether its encrypted connection is up on the device it has (a reconnect only ever reuses one). True for a provider with no such connection.</summary>
    bool EncryptedReady(string provider);

    /// <summary>Closes the provider's helper cleanly and keeps every file. Idempotent.</summary>
    Task StopAsync(string provider, CancellationToken cancellationToken);

    /// <summary>The provider's first login after a move, once: Messenger with its existing tries, Instagram a single resume. Paced by the server's restore ledger.</summary>
    Task<AccountStart> StartAsync(string provider, CancellationToken cancellationToken);
}

/// <summary><c>connected</c>, <c>not_connected</c> (with a code word) or <c>paced</c>.</summary>
internal sealed record AccountStart(string Outcome, string? Reason = null);

/// <summary>
/// The Linux server's end of an account move (SWITCHBOARD.md stage 6 slice 13, "accounts move with the PC"). It keeps one record a provider and runs a provider only when that record says
/// <c>none</c> (nothing here, or the server's own sign-in) or <c>active</c>. Everything staged waits in places nothing runs from, and the files are put where the server runs them from only at
/// activation, all named providers together, with the record flipped after. Nothing here is logged but fixed words and counts: a piece is cookies or device keys, and a history page is
/// message text.
/// <list type="bullet">
/// <item><b>Staging.</b> A session is sealed with the server's key where it will live, the device database is written into the helper's private root under a folder of the move
/// (<c>.incoming-&lt;move&gt;</c>), and history pages are imported into a store inside that same folder by a helper that cannot sign in, which seals them with the server's own helper key. Activation renames the
/// account folder into the live root.</item>
/// <item><b>Crash.</b> An <c>activating</c> record is finished at the next start (every step is idempotent); an <c>installing</c> one is kept while its pieces and digests agree and wiped otherwise, or when it is
/// older than <see cref="StaleStaging"/>.</item>
/// <item><b>Hold.</b> A move back records the hold first (a plain file, so an offline command can clear it) and only then closes the helpers. A held provider is refused by <see cref="Allows"/>, which the host's account
/// ports and its restore ask.</item>
/// </list>
/// </summary>
internal sealed class AccountMoveHost : IAccountMove, IAccountGate, IAsyncDisposable
{
    /// <summary>A staging nobody finished is wiped after this long.</summary>
    public static readonly TimeSpan StaleStaging = TimeSpan.FromHours(48);

    /// <summary>How long an activation waits for the servers' logins before it answers <c>pending</c> (they carry on).</summary>
    public TimeSpan LoginWait { get; set; } = TimeSpan.FromSeconds(90);

    private static readonly string[] Sections = ["contacts", "threads", "mappings", "deletions", "messages", "media", "shares", "attachments", "reactions", "receipts"];

    private readonly AccountMovePaths paths;
    private readonly IStateProtector protector;
    private readonly IAccountRunner runner;
    private readonly IOfflineHistoryStarter history;
    private readonly Func<UploadStore> uploads;
    private readonly TimeProvider time;
    private readonly Action<string> log;
    private readonly AccountMoveRecords records;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> lifecycle = MoveProviders.All.ToDictionary(provider => provider, _ => new SemaphoreSlim(1, 1), StringComparer.Ordinal);
    // What Allows answers, replaced whole whenever a record changes: read without the gate, and always one consistent view.
    private volatile Dictionary<string, bool> allowed = new() { [MoveProviders.Messenger] = false, [MoveProviders.Instagram] = false };
    // Held providers whose helper the server has confirmed stopped (read and written under the gate). A hold is a record on disk; this is the other half of "held": nothing of the account runs.
    private readonly HashSet<string> stopped = new(StringComparer.Ordinal);
    private volatile HashSet<string> removableHeld = new(StringComparer.Ordinal);
    private IOfflineHistory? staging;
    private string? stagingFor;
    private IOfflineHistory? reading;
    private readonly Dictionary<string, (long Length, DateTime Written, string Sha256)> digests = new(StringComparer.Ordinal);

    public AccountMoveHost(AccountMovePaths paths, IStateProtector protector, IAccountRunner runner, IOfflineHistoryStarter history, Func<UploadStore> uploads, TimeProvider? time = null, Action<string>? log = null)
    {
        this.paths = paths; this.protector = protector; this.runner = runner; this.history = history; this.uploads = uploads;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
        records = new AccountMoveRecords(paths, protector, this.log);
    }

    /// <summary>Whether the provider may run now: nothing is staged or held for it, or it is active. A record that cannot be read allows nothing.</summary>
    public bool Allows(string provider) => allowed.TryGetValue(provider, out var yes) && yes;

    public SemaphoreSlim? LifecycleGate(string provider) => lifecycle.GetValueOrDefault(provider);

    internal bool CanRemoveHeld(string provider) => removableHeld.Contains(provider);

    // Lock order is provider locks (in fixed order), then the move-record lock. Restores never acquire the record lock while holding a backend lock, and logins start after these locks go.
    private async Task<IDisposable> LockAccountsAsync(IEnumerable<string> providers, CancellationToken cancellationToken)
    {
        var taken = new List<SemaphoreSlim>();
        try
        {
            var named = providers.ToHashSet(StringComparer.Ordinal);
            foreach (var provider in MoveProviders.All.Where(named.Contains))
            {
                var account = lifecycle[provider];
                await account.WaitAsync(cancellationToken).ConfigureAwait(false);
                taken.Add(account);
            }
            return new AccountLocks(taken);
        }
        catch { new AccountLocks(taken).Dispose(); throw; }
    }

    private sealed class AccountLocks(List<SemaphoreSlim> taken) : IDisposable
    {
        public void Dispose() { for (var index = taken.Count - 1; index >= 0; index--) taken[index].Release(); taken.Clear(); }
    }

    private long Now => time.GetUtcNow().ToUnixTimeMilliseconds();

    // ---- Start ----

    /// <summary>
    /// Reads the records and settles what a stop left: an activation is finished, a staging that cannot be trusted is wiped, and staged folders no record names are removed. Call it
    /// once, before the first restore.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await records.LoadAsync().ConfigureAwait(false);
            if (!records.Readable) { Refresh(); return; }
            // A server that has just started runs nothing yet, and the gate keeps a held account from starting: a hold read from disk is a stopped one.
            stopped.Clear();
            foreach (var provider in records.Held.Keys) stopped.Add(provider);
            var changed = false;
            foreach (var provider in MoveProviders.All)
            {
                var record = Record(provider);
                switch (record.State)
                {
                    case MoveStates.Activating:
                        try
                        {
                            Place(provider, record);
                            records.Providers[provider] = record with { State = MoveStates.Active, At = Now };
                            log($"account move recovered provider={provider} outcome=activated");
                        }
                        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
                        {
                            log($"account move recovered provider={provider} outcome=still_activating error={failure.GetType().Name}");
                        }
                        changed = true;
                        break;
                    case MoveStates.Installing or MoveStates.Installed:
                        if (Now - record.At > (long)StaleStaging.TotalMilliseconds || !StagingConsistent(provider, record))
                        {
                            WipeStaging(provider, record.MoveId);
                            records.Providers[provider] = ProviderRecord.None;
                            log($"account move staged wiped provider={provider} reason={(Now - record.At > (long)StaleStaging.TotalMilliseconds ? "stale" : "inconsistent")}");
                            changed = true;
                        }
                        break;
                }
            }
            SweepOrphans();
            if (changed) await records.SaveProvidersAsync().ConfigureAwait(false);
            Refresh();
        }
        finally { gate.Release(); }
    }

    // ---- Status ----

    public async Task<AccountMoveStatus> StatusAsync(AccountMoveStatusParams request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StatusLockedAsync().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<AccountMoveStatus> StatusLockedAsync()
    {
        var providers = new List<AccountMoveProvider>();
        var normalized = false;
        foreach (var provider in MoveProviders.All)
        {
            var (state, record) = Effective(provider);
            // An account that moved here and was signed out here since is not the move's any more.
            if (state == MoveStates.None && record.State == MoveStates.Active)
            {
                records.Providers[provider] = ProviderRecord.None;
                normalized = true;
            }
            var held = records.Held.TryGetValue(provider, out var entry) ? entry : null;
            var sections = new Dictionary<string, int>(record.Held.Sections ?? [], StringComparer.Ordinal);
            // Ready is more than connected: a moved Messenger that has encrypted conversations on (its flag came with it) is ready only when its encrypted connection is up on the device it came with.
            var connected = runner.Connected(provider);
            var ready = connected && state == MoveStates.Active && (provider != MoveProviders.Messenger || record.PieceList.All(piece => piece.Name != MovePieces.Flag) || runner.EncryptedReady(provider));
            providers.Add(new AccountMoveProvider(provider, state, held?.MoveId ?? record.MoveId, connected,
                [.. record.PieceList.Select(piece => new MovePiece(piece.Name, piece.Bytes, piece.Sha256))],
                new MoveHistory(record.Held.Next, record.Held.Refused, sections, record.Held.Complete), held is { Released: false }, record.Key, ready, held is not null && stopped.Contains(provider),
                DeviceSchemaOf(provider, state, record, held), await SessionVersionOfAsync(provider, state, held).ConfigureAwait(false)));
        }
        if (normalized) { await records.SaveProvidersAsync().ConfigureAwait(false); Refresh(); }
        return new AccountMoveStatus(providers);
    }

    // The schema the Messenger device database a move back would take home records (protocol 19), for a Messenger the server runs or holds with its copies still here. 2026-10-12: this
    // server updates nightly from main and the PC only when released, so its helper may have upgraded the store past what the PC's opens; the PC compares before it holds anything.
    // Read-only and unpooled, so the running helper's file is neither created nor changed.
    private int? DeviceSchemaOf(string provider, string state, ProviderRecord record, HeldEntry? held)
    {
        if (provider != MoveProviders.Messenger || record.Key is null || state is not (MoveStates.Active or MoveStates.Held) || held is { Released: true }) return null;
        return MessengerDeviceSchema.Read(Path.Combine(paths.LiveMessengerAccount(record.Key), "e2ee.db"));
    }

    // The same for Instagram: the format of the saved session envelope a move back would take home, whose newer helper may write a format the PC's does not resume. Read from the sealed
    // envelope and not kept; its plaintext is zeroed.
    private async Task<int?> SessionVersionOfAsync(string provider, string state, HeldEntry? held)
    {
        if (provider != MoveProviders.Instagram || state is not (MoveStates.Active or MoveStates.Held) || held is { Released: true }) return null;
        try
        {
            var plain = await new InstagramSessionStore(paths.LiveInstagramRoot, protector).LoadSessionAsync().ConfigureAwait(false);
            if (plain is null) return null;
            try { return InstagramEnvelope.VersionOf(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException or InvalidOperationException) { return null; }
    }

    // The state the wire shows and the record behind it: a hold wins, then an unreadable record, then the record, and a server's own sign-in is told apart from nothing.
    private (string State, ProviderRecord Record) Effective(string provider)
    {
        if (!records.Readable) return (MoveStates.Unreadable, ProviderRecord.None);
        if (records.Held.ContainsKey(provider)) return (MoveStates.Held, Record(provider));
        var record = Record(provider);
        var live = LiveSessionExists(provider);
        return record.State switch
        {
            MoveStates.None => (live ? MoveStates.Own : MoveStates.None, record),
            MoveStates.Active when !live => (MoveStates.None, record),
            var other => (other, record),
        };
    }

    private ProviderRecord Record(string provider) => records.Providers.TryGetValue(provider, out var record) ? record : ProviderRecord.None;

    private bool LiveSessionExists(string provider) =>
        File.Exists(provider == MoveProviders.Messenger ? paths.LiveMessengerSession : paths.LiveInstagramPointer);

    private void Refresh()
    {
        var next = new Dictionary<string, bool>();
        foreach (var provider in MoveProviders.All)
        {
            if (!records.Readable || records.Held.ContainsKey(provider)) { next[provider] = false; continue; }
            next[provider] = Record(provider).State is MoveStates.None or MoveStates.Active;
        }
        removableHeld = MoveProviders.All.Where(provider => records.Readable
            && records.Held.TryGetValue(provider, out var held) && held.Released && stopped.Contains(provider)
            && Record(provider).State is MoveStates.Active or MoveStates.None
            && AccountMovePaths.IsAccountKey(provider, Record(provider).Key)).ToHashSet(StringComparer.Ordinal);
        allowed = next;
    }

    // ---- Staging ----

    public async Task<MovePiece> StageAsync(AccountMoveStageParams request, CancellationToken cancellationToken = default)
    {
        if (!MoveProviders.IsKnown(request.Provider) || !AccountMovePaths.IsMoveId(request.MoveId) || !MovePieces.Of(request.Provider).Contains(request.Piece)
            || !AccountMovePaths.IsAccountKey(request.Provider, request.AccountKey) || request.Sha256 is not { Length: 64 } || !request.Sha256.All(char.IsAsciiHexDigitLower))
            throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
        using var account = await LockAccountsAsync([request.Provider], cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireReadable();
            var (state, record) = Effective(request.Provider);
            var continuing = record is { State: MoveStates.Installing } && record.MoveId == request.MoveId;
            if (continuing && record.Key != request.AccountKey) throw new InvalidOperationException(AccountMoveWords.Mismatch);
            if (!continuing)
            {
                // A staging of another move that has not been activated is replaced; a server's own sign-in, an account that is running and a hold whose copies are still
                // here are not touched.
                var replaceable = state is MoveStates.None or MoveStates.Installing or MoveStates.Installed
                    || state == MoveStates.Held && records.Held.TryGetValue(request.Provider, out var held) && held.Released;
                if (!replaceable) throw new InvalidOperationException(AccountMoveWords.NotAllowed);
            }

            // The piece is read and judged before anything of the server's is changed: a request that is not what it says must not wipe a staging, or take a hold away.
            byte[] bytes;
            using (var claim = uploads().Claim([request.Upload]))
            {
                var file = claim.FileAt(0);
                if (!string.Equals(file.MimeType, AccountMoveUpload.MimeType, StringComparison.OrdinalIgnoreCase) || file.Length > AccountMoveUpload.MaxPieceBytes)
                    throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                await using var stream = await file.OpenRead(cancellationToken).ConfigureAwait(false);
                bytes = new byte[file.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), request.Sha256, StringComparison.Ordinal))
                    throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                ValidatePiece(request, bytes);
                if (!continuing)
                {
                    await DisposeStagingHelperAsync().ConfigureAwait(false);
                    WipeStaging(request.Provider, record.MoveId);
                    record = new ProviderRecord(MoveStates.Installing, request.MoveId, request.AccountKey, [], new StoredHistory(), Now);
                }
                await WritePieceAsync(request, bytes, cancellationToken).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }

            var piece = new StoredPiece(request.Piece, request.Upload.Length, request.Sha256);
            var pieces = record.PieceList.Where(existing => existing.Name != request.Piece).Append(piece).ToList();
            records.Providers[request.Provider] = record with { State = MoveStates.Installing, Pieces = pieces, At = Now };
            await records.SaveProvidersAsync().ConfigureAwait(false);
            // The hold is given up only now that a piece of the new move is safely written: a disk that filled up before this must not leave the account with no hold.
            if (records.Held.TryGetValue(request.Provider, out var givenUp))
            {
                records.Held.Remove(request.Provider);
                try { await records.SaveHeldAsync().ConfigureAwait(false); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    // Not saved is not given up: the hold stays in memory exactly as it is on disk.
                    records.Held[request.Provider] = givenUp;
                    Refresh();
                    log($"account move hold kept error={failure.GetType().Name}");
                    throw new InvalidOperationException(AccountMoveWords.Files);
                }
                stopped.Remove(request.Provider);
            }
            Refresh();
            log($"account move staged provider={request.Provider} piece={request.Piece} bytes={piece.Bytes}");
            return new MovePiece(piece.Name, piece.Bytes, piece.Sha256);
        }
        finally { gate.Release(); }
    }

    // Whether the bytes are the kind of piece they are said to be, and of the account they are said to be of. Reads nothing and changes nothing.
    private static void ValidatePiece(AccountMoveStageParams request, byte[] bytes)
    {
        switch (request.Provider, request.Piece)
        {
            case (MoveProviders.Messenger, MovePieces.Session):
                if (bytes.Length is 0 or > 1024 * 1024 || ViewerOf(bytes) is not { } viewer || MessengerKeptAccount.StoreKey(viewer) != request.AccountKey)
                    throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                break;
            case (MoveProviders.Messenger, MovePieces.Flag):
                if (!bytes.AsSpan().SequenceEqual("on"u8)) throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                break;
            case (MoveProviders.Messenger, MovePieces.Device):
                // A database file, byte for byte: SQLite's own header at least. Whether it holds a device is looked at when the move is verified.
                if (bytes.Length < 4096 || !bytes.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8)) throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                break;
            case (MoveProviders.Instagram, MovePieces.Envelope):
                if (bytes.Length is 0 or > 1024 * 1024) throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                // The envelope names its own account: it must be the one the PC says.
                string? named = null;
                try
                {
                    using var document = JsonDocument.Parse(bytes);
                    named = document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("accountKey", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                }
                catch (JsonException) { }
                if (!InstagramSessionStore.IsAccountKey(named)) throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                if (named != request.AccountKey) throw new InvalidOperationException(AccountMoveWords.Mismatch);
                break;
            case (MoveProviders.Instagram, MovePieces.Guard):
                // Its limits in the backend's own form, and no hold: an account Instagram asked to check does not move (its first login here would be the resume the PC refuses).
                if (bytes.Length is 0 or > 64 * 1024 || InstagramGuardFile.Read(new GuardRead(bytes, true), DateTimeOffset.UtcNow) is not { Strict: false, Hold: null })
                    throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                break;
            default:
                throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
        }
    }

    private async Task WritePieceAsync(AccountMoveStageParams request, byte[] bytes, CancellationToken cancellationToken)
    {
        var id = request.MoveId;
        switch (request.Provider, request.Piece)
        {
            case (MoveProviders.Messenger, MovePieces.Session):
                await AccountMoveRecords.WriteAsync(paths.StagedMessengerSession(id), await protector.ProtectAsync(bytes).ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case (MoveProviders.Messenger, MovePieces.Flag):
                await AccountMoveRecords.WriteAsync(paths.StagedMessengerFlag(id), bytes).ConfigureAwait(false);
                break;
            case (MoveProviders.Messenger, MovePieces.Device):
                await AccountMoveRecords.WriteAsync(Path.Combine(paths.IncomingMessengerAccount(id, request.AccountKey), "e2ee.db"), bytes).ConfigureAwait(false);
                break;
            case (MoveProviders.Instagram, MovePieces.Guard):
                // Sealed under the server's key, as the backend keeps it, in the move's own folder.
                await new InstagramSessionStore(paths.StagedInstagramRoot(id), protector).SaveGuardAsync(bytes).ConfigureAwait(false);
                break;
            default:
                // Instagram's envelope: sealed under the server's key where the store keeps it, and its pointer, in the move's own folder.
                await new InstagramSessionStore(paths.StagedInstagramRoot(id), protector).SaveSessionAsync(bytes).ConfigureAwait(false);
                break;
        }
    }

    // The Facebook id in a Messenger session ({"cookies": {"c_user": ...}}), or null. Never logged.
    internal static string? ViewerOf(ReadOnlyMemory<byte> session) => MessengerSessionShape.ViewerOf(session);

    public async Task<MoveHistoryPutResult> PutHistoryAsync(MoveHistoryPutParams request, CancellationToken cancellationToken = default)
    {
        if (request.Provider != MoveProviders.Messenger || request.Page < 0 || request.Data.ValueKind != JsonValueKind.Object || request.Data.GetRawText().Length > AccountMoveUpload.MaxPageBytes)
            throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireReadable();
            var (_, record) = Effective(MoveProviders.Messenger);
            if (record is not { State: MoveStates.Installing } || record.MoveId != request.MoveId || record.Key is null || record.PieceList.All(piece => piece.Name != MovePieces.Session))
                throw new InvalidOperationException(AccountMoveWords.NotAllowed);
            var held = record.Held;
            if (request.Page < held.Next) return new MoveHistoryPutResult(request.Page, 0, 0, 0, true, held.Next);
            if (request.Page > held.Next) throw new InvalidOperationException(AccountMoveWords.PageOrder);
            if (MessengerHistoryPage.Read(request.Data) is not { } page || page.Account != record.Key) throw new InvalidOperationException(AccountMoveWords.Mismatch);

            var helper = await StagingHelperAsync(record, cancellationToken).ConfigureAwait(false);
            JsonElement answer;
            try { answer = await helper.ImportPageAsync(request.Data, cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                await DisposeStagingHelperAsync().ConfigureAwait(false);
                log($"account move history failed page={request.Page} error={failure.GetType().Name}");
                throw new InvalidOperationException(AccountMoveWords.HistoryFailed);
            }
            var sections = new Dictionary<string, int>(held.Sections ?? [], StringComparer.Ordinal);
            int added = 0, skipped = 0, refused = 0;
            if (answer.TryGetProperty("sections", out var counted) && counted.ValueKind == JsonValueKind.Object)
                foreach (var section in Sections)
                    if (counted.TryGetProperty(section, out var counts) && counts.ValueKind == JsonValueKind.Object)
                    {
                        var a = Count(counts, "added"); var s = Count(counts, "skipped"); var r = Count(counts, "refused");
                        added += a; skipped += s; refused += r;
                        sections[section] = sections.GetValueOrDefault(section) + a + s;
                    }
            records.Providers[MoveProviders.Messenger] = record with
            {
                History = new StoredHistory(held.Next + 1, held.Refused + refused, sections, string.IsNullOrEmpty(page.Next)), At = Now,
            };
            await records.SaveProvidersAsync().ConfigureAwait(false);
            log($"account move history page={request.Page} added={added} skipped={skipped} refused={refused}");
            return new MoveHistoryPutResult(request.Page, added, skipped, refused, false, held.Next + 1);
        }
        finally { gate.Release(); }
    }

    private static int Count(JsonElement counts, string name) => counts.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    // The helper that imports a move's pages, started on the move's own private root and opened for the account the staged session names; kept between pages.
    private async Task<IOfflineHistory> StagingHelperAsync(ProviderRecord record, CancellationToken cancellationToken)
    {
        if (staging is not null && stagingFor == record.MoveId) return staging;
        await DisposeStagingHelperAsync().ConfigureAwait(false);
        var sealedSession = await File.ReadAllBytesAsync(paths.StagedMessengerSession(record.MoveId!), cancellationToken).ConfigureAwait(false);
        var plain = await protector.UnprotectAsync(sealedSession).ConfigureAwait(false);
        string viewer;
        try { viewer = ViewerOf(plain) ?? throw new InvalidOperationException(AccountMoveWords.PieceInvalid); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var started = await history.StartAsync(paths.IncomingMessengerRoot(record.MoveId!), null, cancellationToken).ConfigureAwait(false);
        try { await started.OpenAsync(viewer, create: true, cancellationToken).ConfigureAwait(false); }
        catch { await started.DisposeAsync().ConfigureAwait(false); throw; }
        staging = started;
        stagingFor = record.MoveId;
        return started;
    }

    private async Task DisposeStagingHelperAsync()
    {
        var helper = staging;
        staging = null;
        stagingFor = null;
        if (helper is not null) try { await helper.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }

    // ---- Verify ----

    public async Task<AccountMoveVerified> VerifyAsync(AccountMoveVerifyParams request, CancellationToken cancellationToken = default)
    {
        if (!MoveProviders.IsKnown(request.Provider) || !AccountMovePaths.IsMoveId(request.MoveId)) throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireReadable();
            var (_, record) = Effective(request.Provider);
            if (record.State is not (MoveStates.Installing or MoveStates.Installed)) throw new InvalidOperationException(AccountMoveWords.NotInstalled);
            if (record.MoveId != request.MoveId) throw new InvalidOperationException(AccountMoveWords.OtherMove);
            await DisposeStagingHelperAsync().ConfigureAwait(false);
            var differs = new List<string>();
            var said = request.Pieces.ToDictionary(piece => piece.Name, StringComparer.Ordinal);
            var held = record.PieceList.ToDictionary(piece => piece.Name, StringComparer.Ordinal);
            var required = request.Provider == MoveProviders.Messenger ? new[] { MovePieces.Session } : [MovePieces.Envelope];
            foreach (var name in required.Where(name => !held.ContainsKey(name))) differs.Add(name);
            // Encrypted conversations on without the keys that go with them would leave the server unable to restore. Keys without the flag are carried as they are (a PC whose flag was removed keeps its device).
            if (request.Provider == MoveProviders.Messenger && held.ContainsKey(MovePieces.Flag) && !held.ContainsKey(MovePieces.Device)) differs.Add(MovePieces.Device);
            foreach (var name in said.Keys.Union(held.Keys).Order(StringComparer.Ordinal))
                if (!said.TryGetValue(name, out var mine) || !held.TryGetValue(name, out var theirs) || mine.Sha256 != theirs.Sha256 || mine.Bytes != theirs.Bytes) differs.Add(name);
            if (request.Provider == MoveProviders.Messenger)
            {
                // What the database holds, read back after the pages are in: the importer's own counts say what it was handed, and a row it did not keep must not pass for one it did.
                var sections = await ReadBackAsync(record, cancellationToken).ConfigureAwait(false);
                foreach (var section in Sections)
                    if (request.Expected.GetValueOrDefault(section) != sections.GetValueOrDefault(section)) differs.Add("history." + section);
                if (record.Held.Refused > 0) differs.Add("history.refused");
                if (record.Held.Next > 0 && !record.Held.Complete) differs.Add("history.incomplete");
                if (held.ContainsKey(MovePieces.Device) && record.Key is { } key && !DeviceHoldsAccount(paths.IncomingMessengerAccount(request.MoveId, key), Viewer(record))) differs.Add(AccountMoveWords.DeviceMissing);
            }
            var distinct = differs.Distinct().ToList();
            if (distinct.Count == 0)
            {
                records.Providers[request.Provider] = record with { State = MoveStates.Installed, At = Now };
                await records.SaveProvidersAsync().ConfigureAwait(false);
                Refresh();
            }
            log($"account move verify provider={request.Provider} outcome={(distinct.Count == 0 ? "match" : "differs")} differing={distinct.Count}");
            return new AccountMoveVerified(distinct.Count == 0, distinct, (await StatusLockedAsync().ConfigureAwait(false)).Of(request.Provider)!);
        }
        finally { gate.Release(); }
    }

    // The staged history counted in the store itself (a fresh helper on the move's folder, after the import's helper has been closed). Nothing was staged: nothing to count. A store that cannot be counted is a history that is not proven, so every section differs.
    private async Task<Dictionary<string, int>> ReadBackAsync(ProviderRecord record, CancellationToken cancellationToken)
    {
        var counted = new Dictionary<string, int>(StringComparer.Ordinal);
        if (record.Held.Next == 0) return counted;
        try
        {
            var helper = await StagingHelperAsync(record, cancellationToken).ConfigureAwait(false);
            var answer = await helper.CountAsync(cancellationToken).ConfigureAwait(false);
            foreach (var section in Sections)
                if (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty(section, out var value) && value.TryGetInt32(out var number)) counted[section] = number;
                else counted[section] = -1;
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            log($"account move count failed error={failure.GetType().Name}");
            foreach (var section in Sections) counted[section] = -1;
        }
        finally { await DisposeStagingHelperAsync().ConfigureAwait(false); }
        return counted;
    }

    // The Facebook id of the staged session (for the device check): read from the sealed copy and gone from memory again.
    private string? Viewer(ProviderRecord record)
    {
        try
        {
            var plain = protector.UnprotectAsync(File.ReadAllBytes(paths.StagedMessengerSession(record.MoveId!))).GetAwaiter().GetResult();
            try { return ViewerOf(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException) { return null; }
    }

    /// <summary>Whether a device database is sound and holds a device of this account: opened read-only and left unpooled, so no handle outlives the call.</summary>
    internal static bool DeviceHoldsAccount(string accountFolder, string? viewer)
    {
        if (viewer is null) return false;
        var path = Path.Combine(accountFolder, "e2ee.db");
        if (!File.Exists(path)) return false;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            if ((check.ExecuteScalar() as string) != "ok") return false;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM whatsmeow_device WHERE jid LIKE $a OR jid LIKE $b OR jid LIKE $c";
            command.Parameters.AddWithValue("$a", viewer + ".%");
            command.Parameters.AddWithValue("$b", viewer + ":%");
            command.Parameters.AddWithValue("$c", viewer + "@%");
            return Convert.ToInt64(command.ExecuteScalar()) > 0;
        }
        catch (SqliteException) { return false; }
    }

    // ---- Activate ----

    public async Task<AccountMoveActivated> ActivateAsync(AccountMoveActivateParams request, CancellationToken cancellationToken = default)
    {
        var named = request.Providers.Distinct(StringComparer.Ordinal).ToList();
        if (named.Count == 0 || named.Any(provider => !MoveProviders.IsKnown(provider)) || !AccountMovePaths.IsMoveId(request.MoveId)) throw new InvalidOperationException(AccountMoveWords.NotAllowed);
        var accounts = await LockAccountsAsync(named, cancellationToken).ConfigureAwait(false);
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { accounts.Dispose(); throw; }
        var activated = new List<string>();
        try
        {
            RequireReadable();
            var records0 = named.ToDictionary(provider => provider, provider => Effective(provider).Record);
            var fresh = false;
            foreach (var provider in named)
            {
                var record = records0[provider];
                if (record.MoveId != request.MoveId) throw new InvalidOperationException(AccountMoveWords.OtherMove);
                if (Effective(provider).State == MoveStates.Held) throw new InvalidOperationException(AccountMoveWords.NotAllowed);
                if (record.State is MoveStates.Installed or MoveStates.Activating) fresh = true;
                else if (record.State != MoveStates.Active) throw new InvalidOperationException(AccountMoveWords.NotInstalled);
            }
            if (fresh)
            {
                // All or nothing: every named provider must be ready, and nothing in the way, before one is touched.
                foreach (var provider in named)
                    if (records0[provider].State is not (MoveStates.Installed or MoveStates.Activating or MoveStates.Active)) throw new InvalidOperationException(AccountMoveWords.NotInstalled);
                foreach (var provider in named.Where(provider => records0[provider].State == MoveStates.Installed))
                    if (Blocked(provider)) throw new InvalidOperationException(AccountMoveWords.AccountPresent);
                await DisposeStagingHelperAsync().ConfigureAwait(false);
                foreach (var provider in named.Where(provider => records0[provider].State == MoveStates.Installed))
                    records.Providers[provider] = records0[provider] with { State = MoveStates.Activating, At = Now };
                await records.SaveProvidersAsync().ConfigureAwait(false);
                Refresh();
                foreach (var provider in named.Where(provider => Record(provider).State == MoveStates.Activating)) Place(provider, Record(provider));
                foreach (var provider in named.Where(provider => Record(provider).State == MoveStates.Activating))
                {
                    records.Providers[provider] = Record(provider) with { State = MoveStates.Active, At = Now };
                    activated.Add(provider);
                }
                await records.SaveProvidersAsync().ConfigureAwait(false);
                Refresh();
                log($"account move activated providers={string.Join(',', activated)}");
            }
        }
        catch (IOException) { log("account move activation failed reason=files"); throw new InvalidOperationException(AccountMoveWords.Files); }
        catch (UnauthorizedAccessException) { log("account move activation failed reason=files"); throw new InvalidOperationException(AccountMoveWords.Files); }
        finally { gate.Release(); accounts.Dispose(); }

        // Outside the gate: a login takes as long as Facebook and Instagram take, and the PC asks for the status meanwhile. Only a provider this call activated is logged in; a repeat
        // of a finished activation answers where the providers stand.
        var starts = named.ToDictionary(provider => provider, provider => activated.Contains(provider) ? runner.StartAsync(provider, CancellationToken.None) : null);
        var pending = starts.Values.OfType<Task<AccountStart>>().ToList();
        if (pending.Count > 0) await Task.WhenAny(Task.WhenAll(pending), Task.Delay(LoginWait, time, cancellationToken)).ConfigureAwait(false);
        // Where each stands after the logins, read under the gate again: the records are written by the gate's other holders, never read outside it.
        Dictionary<string, string> states;
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { states = named.ToDictionary(provider => provider, provider => Effective(provider).State); }
        finally { gate.Release(); }
        var answers = new List<AccountMoveActivation>();
        foreach (var provider in named)
        {
            var state = states[provider];
            if (starts[provider] is not { } start) answers.Add(new(provider, state, runner.Connected(provider) ? "connected" : "not_connected"));
            else if (!start.IsCompleted) answers.Add(new(provider, state, "pending"));
            else if (start.IsFaulted) answers.Add(new(provider, state, "not_connected", "login_failed"));
            else answers.Add(new(provider, state, start.Result.Outcome, start.Result.Reason));
            log($"account move login provider={provider} outcome={answers[^1].Outcome}");
        }
        return new AccountMoveActivated(answers);
    }

    // A sign-in of the server's own where the account would go, which a move never replaces. An account folder with no session beside it is what a sign-out leaves
    // (Messenger keeps the device keys), and Place sets it aside: refusing it sent the person to a shell (seen live on 2026-10-12).
    private bool Blocked(string provider) => LiveSessionExists(provider);

    // Moves a signed-out sign-in's files out of the way, kept; a folder holding only the moved-away note goes. <paramref name="alongside"/>, a file of that sign-in
    // kept outside its folder, goes with it.
    private void SetAside(string provider, string target, string aside, string? alongside = null)
    {
        if (!Directory.Exists(target)) return;
        if (!Directory.EnumerateFileSystemEntries(target).Any(entry => Path.GetFileName(entry) != AccountMovePaths.SentinelFile)) { Directory.Delete(target, true); return; }
        // Into the folder before the folder moves, so a stop between the two leaves it where a rerun takes it along (2026-10-12, code review: Messenger's
        // encrypted.on stayed behind, and a move without a flag then ran with the old sign-in's).
        if (alongside is not null && File.Exists(alongside)) File.Move(alongside, Path.Combine(target, Path.GetFileName(alongside)), overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
        Directory.Move(target, Directory.Exists(aside) ? aside + "-" + Guid.NewGuid().ToString("N")[..8] : aside);
        log($"account move set aside provider={provider} reason=signed_out_sign_in");
    }

    // Puts a staged provider where the server runs it from. Every step can run again: a thing already moved is left, and the pointer or the session, which is what makes
    // the account findable, goes last.
    private void Place(string provider, ProviderRecord record)
    {
        var id = record.MoveId!;
        var key = record.Key!;
        if (provider == MoveProviders.Messenger)
        {
            var target = paths.LiveMessengerAccount(key);
            var source = paths.IncomingMessengerAccount(id, key);
            if (Directory.Exists(source))
            {
                SetAside(provider, target, paths.SetAsideMessenger(key, id), paths.LiveMessengerFlag);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(source, target);
            }
            if (Directory.Exists(target) && !File.Exists(Path.Combine(target, AccountMovePaths.MovedInFile))) File.WriteAllText(Path.Combine(target, AccountMovePaths.MovedInFile), "{}");
            // A move with no flag piece owns no live flag, so one still here is an earlier sign-in's, whether or not that sign-in's folder is the one coming in: kept
            // aside where SetAside would have taken it. Only the folder case was handled, and an account moved in beside another's leftover flag ran encrypted
            // (2026-10-12, code review, round 2). Overwriting there keeps a rerun after a copy that was not yet deleted harmless.
            if (record.PieceList.All(piece => piece.Name != MovePieces.Flag) && File.Exists(paths.LiveMessengerFlag))
            {
                var aside = paths.SetAsideMessenger(key, id);
                Directory.CreateDirectory(aside);
                File.Move(paths.LiveMessengerFlag, Path.Combine(aside, Path.GetFileName(paths.LiveMessengerFlag)), overwrite: true);
                log($"account move set aside provider={provider} reason=flag_of_another_sign_in");
            }
            if (File.Exists(paths.StagedMessengerFlag(id)))
            {
                Directory.CreateDirectory(paths.LiveMessengerFolder);
                File.Move(paths.StagedMessengerFlag(id), paths.LiveMessengerFlag, overwrite: true);
            }
            if (File.Exists(paths.StagedMessengerSession(id)))
            {
                Directory.CreateDirectory(paths.LiveMessengerFolder);
                File.Move(paths.StagedMessengerSession(id), paths.LiveMessengerSession, overwrite: true);
            }
        }
        else
        {
            var staged = Path.Combine(paths.StagedInstagramRoot(id), "accounts", key);
            var target = paths.LiveInstagramAccount(key);
            if (Directory.Exists(staged))
            {
                SetAside(provider, target, paths.SetAsideInstagram(key, id));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(staged, target);
            }
            // The limits before the pointer, so the account is never findable here without them.
            if (File.Exists(paths.StagedInstagramGuard(id)))
            {
                Directory.CreateDirectory(paths.LiveInstagramRoot);
                File.Move(paths.StagedInstagramGuard(id), paths.LiveInstagramGuard, overwrite: true);
            }
            var pointer = Path.Combine(paths.StagedInstagramRoot(id), "active.protected");
            if (File.Exists(pointer))
            {
                Directory.CreateDirectory(paths.LiveInstagramRoot);
                File.Move(pointer, paths.LiveInstagramPointer, overwrite: true);
            }
        }
        WipeStaging(provider, id);
    }

    // ---- Abort ----

    public async Task<AccountMoveStatus> AbortAsync(AccountMoveAbortParams request, CancellationToken cancellationToken = default)
    {
        if (!AccountMovePaths.IsMoveId(request.MoveId)) throw new InvalidOperationException(AccountMoveWords.NotAllowed);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireReadable();
            await DisposeStagingHelperAsync().ConfigureAwait(false);
            var changed = false;
            foreach (var provider in MoveProviders.All)
            {
                var record = Record(provider);
                if (record.MoveId != request.MoveId || record.State is not (MoveStates.Installing or MoveStates.Installed or MoveStates.Activating)) continue;
                // A half-finished activation may have put files where the server runs them from: they are this move's, and go.
                if (record.State == MoveStates.Activating) RemoveLive(provider, record.Key, marker: false);
                WipeStaging(provider, record.MoveId);
                records.Providers[provider] = ProviderRecord.None;
                changed = true;
                log($"account move aborted provider={provider}");
            }
            if (changed) { await records.SaveProvidersAsync().ConfigureAwait(false); Refresh(); }
            return await StatusLockedAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // ---- Hold, read, release (a move back) ----

    public async Task<AccountMoveStatus> HoldAsync(AccountMoveHoldParams request, CancellationToken cancellationToken = default)
    {
        var accounts = await LockAccountsAsync(request.Providers.Count == 0 ? MoveProviders.All : request.Providers, cancellationToken).ConfigureAwait(false);
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { accounts.Dispose(); throw; }
        List<string> restart = [];
        AccountMoveStatus? result;
        try
        {
            RequireReadable();
            var named = request.Providers.Count == 0 ? [.. MoveProviders.All.Where(provider => Effective(provider).State is MoveStates.Active or MoveStates.Held)] : request.Providers.Distinct(StringComparer.Ordinal).ToList();
            if (named.Any(provider => !MoveProviders.IsKnown(provider)) || !AccountMovePaths.IsMoveId(request.MoveId)) throw new InvalidOperationException(AccountMoveWords.NotAllowed);
            foreach (var provider in named)
            {
                if (Effective(provider).State is not (MoveStates.Active or MoveStates.Held)) throw new InvalidOperationException(AccountMoveWords.NotAllowed);
                // Only the move that put the account here may take it back: a hold names its id, and so does everything that reads or releases what it holds.
                var heldId = records.Held.TryGetValue(provider, out var already) ? already.MoveId : Record(provider).MoveId;
                if (heldId != request.MoveId) throw new InvalidOperationException(AccountMoveWords.OtherMove);
            }
            if (request.ClientProtocol < ProtocolVersion.DeviceSchema && TooNewForLegacy(await StatusLockedAsync().ConfigureAwait(false), named, stoppedCopy: false))
                throw new InvalidOperationException(AccountMoveWords.TooNewForClient);
            // Recorded first, and plainly: from here nothing starts this account, whatever happens to the helper. The record is on disk before the hold exists in memory, so a save that
            // fails is a hold that was never made: nothing changes, nothing is stopped, and the answer is a failure.
            var added = named.Where(provider => !records.Held.ContainsKey(provider)).ToList();
            if (added.Count > 0)
            {
                foreach (var provider in added) records.Held[provider] = new HeldEntry(Record(provider).MoveId, Now, Released: false);
                try { await records.SaveHeldAsync().ConfigureAwait(false); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    foreach (var provider in added) records.Held.Remove(provider);
                    log($"account move hold not saved error={failure.GetType().Name}");
                    throw new InvalidOperationException(AccountMoveWords.Files);
                }
            }
            Refresh();
            var unconfirmed = 0;
            foreach (var provider in named)
            {
                var closed = false;
                try { await runner.StopAsync(provider, cancellationToken).ConfigureAwait(false); closed = !runner.Connected(provider); }
                catch (Exception failure) when (failure is not OperationCanceledException) { log($"account move hold stop failed provider={provider} error={failure.GetType().Name}"); }
                if (closed) stopped.Add(provider);
                else { stopped.Remove(provider); unconfirmed++; }
                log($"account move held provider={provider} stopped={closed}");
            }
            // Held on disk and denied by the gate, but a helper that may still be running is not a hold anything may read from: the caller is told, and asks again.
            if (unconfirmed > 0) throw new InvalidOperationException(AccountMoveWords.NotStopped);
            result = await StatusLockedAsync().ConfigureAwait(false);
            // The running helper could upgrade between admission's schema read and its stop. Only this stopped snapshot is safe to hand to a legacy client.
            if (request.ClientProtocol < ProtocolVersion.DeviceSchema && TooNewForLegacy(result, named, stoppedCopy: true))
            {
                var lifted = added.ToDictionary(provider => provider, provider => records.Held[provider], StringComparer.Ordinal);
                foreach (var provider in added) records.Held.Remove(provider);
                try { if (added.Count > 0) await records.SaveHeldAsync().ConfigureAwait(false); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    foreach (var (provider, entry) in lifted) records.Held[provider] = entry;
                    log($"account move legacy resume not saved error={failure.GetType().Name}");
                    throw new InvalidOperationException(AccountMoveWords.Files);
                }
                foreach (var provider in added) stopped.Remove(provider);
                Refresh();
                restart = added;
                // Existing holds belong to an earlier move-back attempt and are never lifted here.
                result = null;
            }
        }
        finally { gate.Release(); accounts.Dispose(); }
        if (result is not null) return result;
        foreach (var provider in restart) _ = RestoreRejectedHoldAsync(provider);
        throw new InvalidOperationException(AccountMoveWords.TooNewForClient);
    }

    private bool TooNewForLegacy(AccountMoveStatus status, IReadOnlyList<string> named, bool stoppedCopy)
    {
        foreach (var entry in status.Providers.Where(entry => named.Contains(entry.Provider, StringComparer.Ordinal)))
        {
            if (entry.Provider == MoveProviders.Messenger && (entry.DeviceSchema > LegacyMoveFormats.DeviceSchema
                || stoppedCopy && entry.DeviceSchema is null && entry.AccountKey is { } key && File.Exists(Path.Combine(paths.LiveMessengerAccount(key), "e2ee.db")))) return true;
            if (entry.Provider == MoveProviders.Instagram && entry.SessionVersion is { } version && version != LegacyMoveFormats.SessionVersion) return true;
        }
        return false;
    }

    private async Task RestoreRejectedHoldAsync(string provider)
    {
        try { await runner.StartAsync(provider, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception failure) { log($"account move legacy resume failed provider={provider} error={failure.GetType().Name}"); }
    }

    public async Task<AccountMoveChunk> ReadAsync(AccountMoveReadParams request, CancellationToken cancellationToken = default)
    {
        if (!MoveProviders.IsKnown(request.Provider) || !MovePieces.Of(request.Provider).Contains(request.Piece) || request.Offset < 0 || request.Count is <= 0 or > AccountMoveUpload.MaxReadBytes)
            throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (record, key) = RequireHeld(request.Provider, request.MoveId);
            if (request.ClientProtocol < ProtocolVersion.DeviceSchema && TooNewForLegacy(await StatusLockedAsync().ConfigureAwait(false), [request.Provider], stoppedCopy: true))
                throw new InvalidOperationException(AccountMoveWords.TooNewForClient);
            switch (request.Provider, request.Piece)
            {
                case (MoveProviders.Messenger, MovePieces.Device):
                {
                    var path = Path.Combine(paths.LiveMessengerAccount(key), "e2ee.db");
                    if (!File.Exists(path)) throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                    // A journal or write-ahead log with content is a database that was cut off mid-write: not a copy to take.
                    foreach (var suffix in new[] { "-journal", "-wal" })
                        if (File.Exists(path + suffix) && new FileInfo(path + suffix).Length > 0) throw new InvalidOperationException(AccountMoveWords.DeviceBusy);
                    var info = new FileInfo(path);
                    var sha = DigestOfFile(path, info);
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    return await ChunkAsync(stream, info.Length, request, sha, cancellationToken).ConfigureAwait(false);
                }
                case (MoveProviders.Messenger, MovePieces.Flag):
                {
                    if (!File.Exists(paths.LiveMessengerFlag)) throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                    return Chunk("on"u8.ToArray(), request);
                }
                case (MoveProviders.Messenger, MovePieces.Session):
                {
                    var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(paths.LiveMessengerSession, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                    try { return Chunk(plain, request); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                case (MoveProviders.Instagram, MovePieces.Guard):
                {
                    // As the backend reads it (one the server cannot read is the strictest: held) and written again in its own fixed form. None: the PC keeps its own.
                    var read = await new InstagramSessionStore(paths.LiveInstagramRoot, protector).ReadGuardAsync().ConfigureAwait(false);
                    if (!read.Present) throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                    var limits = InstagramGuardFile.Read(read, time.GetUtcNow());
                    return Chunk(InstagramGuardFile.Write(limits.Hold, limits.Reads), request);
                }
                default:
                {
                    var plain = await new InstagramSessionStore(paths.LiveInstagramRoot, protector).LoadSessionAsync().ConfigureAwait(false) ?? throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
                    try { return Chunk(plain, request); }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
            }
        }
        catch (FileNotFoundException) { throw new InvalidOperationException(AccountMoveWords.PieceInvalid); }
        catch (DirectoryNotFoundException) { throw new InvalidOperationException(AccountMoveWords.PieceInvalid); }
        finally { gate.Release(); }
    }

    private string DigestOfFile(string path, FileInfo info)
    {
        if (digests.TryGetValue(path, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc) return known.Sha256;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        digests[path] = (info.Length, info.LastWriteTimeUtc, sha);
        return sha;
    }

    private static AccountMoveChunk Chunk(byte[] whole, AccountMoveReadParams request)
    {
        var from = (int)Math.Min(request.Offset, whole.Length);
        var count = Math.Min(request.Count, whole.Length - from);
        return new AccountMoveChunk(Convert.ToBase64String(whole, from, count), whole.Length, Convert.ToHexStringLower(SHA256.HashData(whole)));
    }

    private static async Task<AccountMoveChunk> ChunkAsync(FileStream stream, long total, AccountMoveReadParams request, string sha, CancellationToken cancellationToken)
    {
        var count = (int)Math.Max(0, Math.Min(request.Count, total - request.Offset));
        var buffer = new byte[count];
        stream.Position = Math.Min(request.Offset, total);
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return new AccountMoveChunk(Convert.ToBase64String(buffer), total, sha);
    }

    public async Task<AccountMoveHistoryPage> GetHistoryAsync(AccountMoveHistoryGetParams request, CancellationToken cancellationToken = default)
    {
        if (request.Provider != MoveProviders.Messenger) throw new InvalidOperationException(AccountMoveWords.PieceInvalid);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, key) = RequireHeld(MoveProviders.Messenger, request.MoveId);
            if (request.ClientProtocol < ProtocolVersion.DeviceSchema && TooNewForLegacy(await StatusLockedAsync().ConfigureAwait(false), [request.Provider], stoppedCopy: true))
                throw new InvalidOperationException(AccountMoveWords.TooNewForClient);
            if (string.IsNullOrEmpty(request.Cursor))
            {
                await DisposeReadingHelperAsync().ConfigureAwait(false);
                var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(paths.LiveMessengerSession, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                string viewer;
                try { viewer = ViewerOf(plain) ?? throw new InvalidOperationException(AccountMoveWords.PieceInvalid); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (!File.Exists(Path.Combine(paths.LiveMessengerAccount(key), "messages.db")))
                    return new AccountMoveHistoryPage(JsonSerializer.SerializeToElement(new { none = true }));
                var started = await history.StartAsync(paths.LiveMessengerPrivateRoot, null, cancellationToken).ConfigureAwait(false);
                try { await started.OpenAsync(viewer, create: false, cancellationToken).ConfigureAwait(false); }
                catch { await started.DisposeAsync().ConfigureAwait(false); throw; }
                reading = started;
            }
            else if (reading is null) throw new InvalidOperationException(AccountMoveWords.PageOrder);
            JsonElement page;
            try { page = await reading!.ExportPageAsync(request.Cursor, cancellationToken).ConfigureAwait(false); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                await DisposeReadingHelperAsync().ConfigureAwait(false);
                log($"account move history read failed error={failure.GetType().Name}");
                throw new InvalidOperationException(AccountMoveWords.HistoryFailed);
            }
            if (page.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String && next.GetString() == "") await DisposeReadingHelperAsync().ConfigureAwait(false);
            return new AccountMoveHistoryPage(page.Clone());
        }
        finally { gate.Release(); }
    }

    private async Task DisposeReadingHelperAsync()
    {
        var helper = reading;
        reading = null;
        if (helper is not null) try { await helper.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
    }

    public async Task<AccountMoveStatus> ReleaseAsync(AccountMoveReleaseParams request, CancellationToken cancellationToken = default)
    {
        using var accounts = await LockAccountsAsync(request.Providers.Count == 0 ? MoveProviders.All : request.Providers, cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireReadable();
            var named = request.Providers.Count == 0 ? [.. MoveProviders.All.Where(provider => records.Held.TryGetValue(provider, out var entry) && !entry.Released)] : request.Providers.Distinct(StringComparer.Ordinal).ToList();
            if (named.Any(provider => !MoveProviders.IsKnown(provider) || !records.Held.ContainsKey(provider))) throw new InvalidOperationException(AccountMoveWords.NotHeld);
            // Only the move that holds an account may delete the server's copy of it: the copy may be the only one there is.
            if (!AccountMovePaths.IsMoveId(request.MoveId) || named.Any(provider => records.Held[provider].MoveId != request.MoveId)) throw new InvalidOperationException(AccountMoveWords.OtherMove);
            if (request.ClientProtocol < ProtocolVersion.DeviceSchema && TooNewForLegacy(await StatusLockedAsync().ConfigureAwait(false), named, stoppedCopy: true))
                throw new InvalidOperationException(AccountMoveWords.TooNewForClient);
            await DisposeReadingHelperAsync().ConfigureAwait(false);
            var previous = named.ToDictionary(provider => provider, provider => records.Held[provider], StringComparer.Ordinal);
            try
            {
                foreach (var provider in named)
                {
                    var entry = records.Held[provider];
                    if (entry.Released) continue;
                    RemoveLive(provider, Record(provider).Key, marker: true);
                    records.Held[provider] = entry with { Released = true };
                    log($"account move released provider={provider}");
                }
                await records.SaveHeldAsync().ConfigureAwait(false);
            }
            catch
            {
                // Released means durable completion, including to administration. A failed file cleanup or hold save must not make an unfinished copy-back removable in memory.
                foreach (var (provider, entry) in previous) records.Held[provider] = entry;
                Refresh();
                throw;
            }
            Refresh();
            return await StatusLockedAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// A move back that cannot finish gives the accounts back (protocol 19, 2026-10-12): the PC found the device database it downloaded too new for its helper. Each named provider must be held
    /// by this move with its copies still here; the hold is lifted on disk first (a save that fails lifts nothing), then each is logged in once, as an activation's are. A provider already
    /// running for this move again is answered as it stands, so a lost answer is simply asked again.
    /// </summary>
    public async Task<AccountMoveStatus> ResumeAsync(AccountMoveResumeParams request, CancellationToken cancellationToken = default)
    {
        var named = request.Providers.Distinct(StringComparer.Ordinal).ToList();
        if (named.Count == 0 || named.Any(provider => !MoveProviders.IsKnown(provider)) || !AccountMovePaths.IsMoveId(request.MoveId)) throw new InvalidOperationException(AccountMoveWords.NotAllowed);
        var accounts = await LockAccountsAsync(named, cancellationToken).ConfigureAwait(false);
        try { await gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { accounts.Dispose(); throw; }
        var resumed = new List<string>();
        try
        {
            RequireReadable();
            foreach (var provider in named)
            {
                var record = Record(provider);
                if (records.Held.TryGetValue(provider, out var entry))
                {
                    if (entry.MoveId != request.MoveId) throw new InvalidOperationException(AccountMoveWords.OtherMove);
                    // Released, the copies are gone: there is nothing here to run. A hold of an account that never ran here is not this method's to lift.
                    if (entry.Released || record.State != MoveStates.Active || record.Key is null) throw new InvalidOperationException(AccountMoveWords.NotHeld);
                    resumed.Add(provider);
                }
                else if (record.MoveId != request.MoveId || record.State != MoveStates.Active) throw new InvalidOperationException(AccountMoveWords.NotHeld);
            }
            if (resumed.Count > 0)
            {
                // The move back's history reader is closed before the account's own helper may open the same store.
                await DisposeReadingHelperAsync().ConfigureAwait(false);
                var lifted = resumed.ToDictionary(provider => provider, provider => records.Held[provider], StringComparer.Ordinal);
                foreach (var provider in resumed) records.Held.Remove(provider);
                try { await records.SaveHeldAsync().ConfigureAwait(false); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    foreach (var (provider, entry) in lifted) records.Held[provider] = entry;
                    log($"account move resume not saved error={failure.GetType().Name}");
                    throw new InvalidOperationException(AccountMoveWords.Files);
                }
                foreach (var provider in resumed) stopped.Remove(provider);
                Refresh();
                foreach (var provider in resumed) log($"account move resumed provider={provider}");
            }
        }
        finally { gate.Release(); accounts.Dispose(); }

        // Outside the gate, as an activation's logins are: the PC may ask the status meanwhile.
        var starts = resumed.Select(provider => (provider, start: runner.StartAsync(provider, CancellationToken.None))).ToList();
        if (starts.Count > 0) await Task.WhenAny(Task.WhenAll(starts.Select(item => item.start)), Task.Delay(LoginWait, time, cancellationToken)).ConfigureAwait(false);
        foreach (var (provider, start) in starts)
            log($"account move login provider={provider} outcome={(!start.IsCompleted ? "pending" : start.IsFaulted ? "not_connected" : start.Result.Outcome)}");
        return await StatusAsync(new AccountMoveStatusParams(request.MoveId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes only a completed move-back's server copy. The caller owns this provider's lifecycle lock and supplies its inner, ungated local sign-out; taking that lock again would
    /// deadlock. An unreleased hold still serves a PC's copy-back, so it is never removable. Null means there is no hold and ordinary administration may continue.
    /// </summary>
    internal async Task<AccountRemoved?> RemoveReleasedHeldLockedAsync(string provider, Func<string, Task> signOut, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!records.Readable) throw new InvalidOperationException(AccountRemoveWords.Moving);
            if (!records.Held.TryGetValue(provider, out var held)) return null;
            var record = Record(provider);
            if (!held.Released || !stopped.Contains(provider) || runner.Connected(provider)
                || record.State is not (MoveStates.Active or MoveStates.None) || !AccountMovePaths.IsAccountKey(provider, record.Key))
                throw new InvalidOperationException(AccountRemoveWords.Moving);

            await DisposeReadingHelperAsync().ConfigureAwait(false);
            var key = record.Key!;
            if (provider == MoveProviders.Messenger)
            {
                // Release normally left only a marker, but never destroy device/history files if an older or interrupted cleanup left any behind. The record names the identity,
                // not a session already deleted by release or the backend's possibly stale chat list. Every retry takes only whatever is still in this same live folder.
                var folder = paths.LiveMessengerAccount(key);
                if (File.Exists(paths.LiveMessengerFlag))
                {
                    Directory.CreateDirectory(folder);
                    File.Move(paths.LiveMessengerFlag, Path.Combine(folder, "encrypted.on"), overwrite: true);
                }
                if (Directory.Exists(folder))
                {
                    var aside = paths.SetAsideMessenger(key, "removed-" + held.At);
                    Directory.CreateDirectory(Path.GetDirectoryName(aside)!);
                    Directory.Move(folder, Directory.Exists(aside) ? aside + "-" + Guid.NewGuid().ToString("N") : aside);
                }
            }
            await signOut(key).ConfigureAwait(false);
            RemoveLive(provider, key, marker: false);

            // Two files cannot be replaced together. Keep the hold until the reset is durable, and keep its identity in the reset record so a crash or failed second save can be retried.
            // A failed first save changes neither the in-memory record nor the gate; a failed second save keeps the same hold both on disk and in memory. No retry resumes a helper.
            records.Providers[provider] = new ProviderRecord(MoveStates.None, record.MoveId, key, At: Now);
            try { await records.SaveProvidersAsync().ConfigureAwait(false); }
            catch { records.Providers[provider] = record; throw; }
            records.Held.Remove(provider);
            try { await records.SaveHeldAsync().ConfigureAwait(false); }
            catch { records.Held[provider] = held; throw; }
            stopped.Remove(provider);
            Refresh();
            log($"account move removed provider={provider} outcome=done");
            return new AccountRemoved(true);
        }
        finally { gate.Release(); }
    }

    // ---- Files ----

    private void RequireReadable()
    {
        if (!records.Readable) throw new InvalidOperationException(AccountMoveWords.NoRecord);
    }

    private (ProviderRecord Record, string Key) RequireHeld(string provider, string moveId)
    {
        RequireReadable();
        if (!records.Held.TryGetValue(provider, out var entry) || entry.Released) throw new InvalidOperationException(AccountMoveWords.NotHeld);
        if (entry.MoveId != moveId) throw new InvalidOperationException(AccountMoveWords.OtherMove);
        // Nothing is read from an account whose helper may still be running: it could change what is read.
        if (!stopped.Contains(provider)) throw new InvalidOperationException(AccountMoveWords.NotStopped);
        var record = Record(provider);
        // The key of the account is read from what is here when the record has none (a hold of a server that moved before this was kept).
        var key = record.Key ?? throw new InvalidOperationException(AccountMoveWords.NotHeld);
        return (record, key);
    }

    private void WipeStaging(string provider, string? moveId)
    {
        if (moveId is null || !AccountMovePaths.IsMoveId(moveId)) return;
        try
        {
            var folder = provider == MoveProviders.Messenger ? Path.Combine(paths.Staging(moveId), "Messenger") : paths.StagedInstagramRoot(moveId);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (provider == MoveProviders.Messenger && Directory.Exists(paths.IncomingMessengerRoot(moveId))) Directory.Delete(paths.IncomingMessengerRoot(moveId), true);
            var parent = paths.Staging(moveId);
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { log($"account move wipe failed provider={provider} error={failure.GetType().Name}"); }
    }

    // What the server runs a provider from, deleted; an account folder keeps only the sentinel that says the account lives elsewhere.
    private void RemoveLive(string provider, string? key, bool marker)
    {
        try
        {
            if (provider == MoveProviders.Messenger)
            {
                foreach (var file in new[] { paths.LiveMessengerSession, paths.LiveMessengerSession + ".tmp", paths.LiveMessengerFlag, paths.LiveMessengerChats, paths.LiveMessengerChats + ".tmp" })
                    if (File.Exists(file)) File.Delete(file);
                if (key is not null) EmptyAccountFolder(paths.LiveMessengerAccount(key), marker);
            }
            else
            {
                if (File.Exists(paths.LiveInstagramPointer)) File.Delete(paths.LiveInstagramPointer);
                // The account's limits went with it (a move back) or came with the activation being undone.
                if (File.Exists(paths.LiveInstagramGuard)) File.Delete(paths.LiveInstagramGuard);
                if (key is not null) EmptyAccountFolder(paths.LiveInstagramAccount(key), marker);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { log($"account move remove failed provider={provider} error={failure.GetType().Name}"); throw; }
    }

    private static void EmptyAccountFolder(string folder, bool marker)
    {
        if (Directory.Exists(folder))
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
        // A released account keeps its folder with only the marker; an activation undone leaves no folder at all.
        if (!marker)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            return;
        }
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, AccountMovePaths.SentinelFile), "{}");
    }

    // Whether what was staged is what the record says: every piece is there, and a device file is its recorded size.
    private bool StagingConsistent(string provider, ProviderRecord record)
    {
        if (record.MoveId is null || record.Key is null) return false;
        foreach (var piece in record.PieceList)
        {
            var present = (provider, piece.Name) switch
            {
                (MoveProviders.Messenger, MovePieces.Session) => File.Exists(paths.StagedMessengerSession(record.MoveId)),
                (MoveProviders.Messenger, MovePieces.Flag) => File.Exists(paths.StagedMessengerFlag(record.MoveId)),
                (MoveProviders.Messenger, MovePieces.Device) => File.Exists(Path.Combine(paths.IncomingMessengerAccount(record.MoveId, record.Key), "e2ee.db"))
                    && new FileInfo(Path.Combine(paths.IncomingMessengerAccount(record.MoveId, record.Key), "e2ee.db")).Length == piece.Bytes,
                (MoveProviders.Instagram, MovePieces.Envelope) => File.Exists(Path.Combine(paths.StagedInstagramRoot(record.MoveId), "active.protected")),
                (MoveProviders.Instagram, MovePieces.Guard) => File.Exists(paths.StagedInstagramGuard(record.MoveId)),
                _ => false,
            };
            if (!present) return false;
        }
        return true;
    }

    // Folders no record names: a move that was abandoned between creating them and writing the record.
    private void SweepOrphans()
    {
        var named = records.Providers.Values.Select(record => record.MoveId).Where(id => id is not null).ToHashSet(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(paths.StagingRoot))
                foreach (var folder in Directory.EnumerateDirectories(paths.StagingRoot).Where(folder => !named.Contains(Path.GetFileName(folder))))
                    Directory.Delete(folder, true);
            if (Directory.Exists(paths.LiveMessengerPrivateRoot))
                foreach (var folder in Directory.EnumerateDirectories(paths.LiveMessengerPrivateRoot, ".incoming-*").Where(folder => !named.Contains(Path.GetFileName(folder)[".incoming-".Length..])))
                    Directory.Delete(folder, true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { log($"account move sweep failed error={failure.GetType().Name}"); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeStagingHelperAsync().ConfigureAwait(false);
        await DisposeReadingHelperAsync().ConfigureAwait(false);
    }
}
