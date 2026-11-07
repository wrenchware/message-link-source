using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using NativeBeeper.App.Services.Backup;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Backup;

/// <summary>Messenger as the server's backup reaches it: its connection, the account the helper's store belongs to, and the helper's export and import.</summary>
internal interface IServerBackupMessenger
{
    IReadOnlyDictionary<string, IServerBackupMessenger> AdditionalAccounts => new Dictionary<string, IServerBackupMessenger>();
    bool Saved { get; }
    bool Connected { get; }
    string? AccountKey { get; }
    Task<MessengerHistoryPage> ExportPageAsync(string? cursor, CancellationToken cancellationToken);
    Task EndExportAsync(CancellationToken cancellationToken);
    Task<MessengerHistoryImport> ImportPageAsync(JsonElement page, CancellationToken cancellationToken);
    void Imported();
}

/// <summary>The server's Messenger backend as <see cref="IServerBackupMessenger"/>.</summary>
internal sealed class BackendBackupMessenger(MessengerBackend messenger, NativeBeeper.App.Services.ProviderAccounts? accounts = null) : IServerBackupMessenger
{
    public IReadOnlyDictionary<string, IServerBackupMessenger> AdditionalAccounts => accounts?.Added.Where(a => a.Messenger is not null).ToDictionary(a => a.Registration.Key, a => (IServerBackupMessenger)new BackendBackupMessenger(a.Messenger!)) ?? [];
    public bool Saved => messenger.SessionSaved;
    public bool Connected => messenger.IsConnected;
    public string? AccountKey => messenger.HistoryAccountKey;
    public Task<MessengerHistoryPage> ExportPageAsync(string? cursor, CancellationToken cancellationToken) => messenger.ExportHistoryPageAsync(cursor, cancellationToken);
    public Task EndExportAsync(CancellationToken cancellationToken) => messenger.EndHistoryExportAsync(cancellationToken);
    public Task<MessengerHistoryImport> ImportPageAsync(JsonElement page, CancellationToken cancellationToken) => messenger.ImportHistoryPageAsync(page, cancellationToken);
    public void Imported() => messenger.HistoryImported();
}

/// <summary>
/// What a restore leaves to take in once the accounts it belongs to are signed in again (<c>Restore/pending</c>): Messenger's history pages, through the helper's own
/// import, which needs Messenger connected; and a PC backup's choices, which are placed under the key the server files its shared state under, which only a signed-in
/// account gives it. Each is taken in once and then removed; a page already there is skipped by the helper, so taking them in again changes nothing.
/// </summary>
/// <remarks>Logged as <c>server restore pending kind=... outcome=...</c> with counts; never a name or a value.</remarks>
internal static class PendingImports
{
    internal sealed record MessengerPending(string Account, int Pages);

    public static string Folder(ServerBackupPaths paths) => Path.Combine(paths.Restore, ServerRestore.PendingFolder);
    public static string MessengerAccountPath(ServerBackupPaths paths, string? slot = null) => Path.Combine(MessengerFolder(paths, slot), "account.json");
    public static string MessengerPagePath(ServerBackupPaths paths, int number, string? slot = null) => Path.Combine(MessengerFolder(paths, slot), $"page-{number:D6}.protected");
    private static string MessengerFolder(ServerBackupPaths paths, string? slot) => slot is null ? Path.Combine(Folder(paths), "messenger") : NativeBeeper.Core.Messaging.AccountNamespace.ValidKey(slot) ? Path.Combine(Folder(paths), "accounts", slot, "messenger") : throw new ArgumentException("Invalid account slot.");
    public static string PcLocalPath(ServerBackupPaths paths) => Path.Combine(Folder(paths), "pc-local.protected");

    /// <summary>What waits: Messenger's pages (0 for none) and whether a PC's choices do.</summary>
    public static (int MessengerPages, bool PcLocal) Waiting(ServerBackupPaths paths)
    {
        var pages = 0;
        try
        {
            if (File.Exists(MessengerAccountPath(paths)) && JsonSerializer.Deserialize<MessengerPending>(File.ReadAllBytes(MessengerAccountPath(paths))) is { } pending) pages = pending.Pages;
        }
        catch (Exception failure) when (failure is IOException or JsonException or UnauthorizedAccessException) { }
        var accounts = Path.Combine(Folder(paths), "accounts");
        if (Directory.Exists(accounts))
            foreach (var directory in Directory.EnumerateDirectories(accounts))
            {
                var slot = Path.GetFileName(directory);
                if (!NativeBeeper.Core.Messaging.AccountNamespace.ValidKey(slot) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                try
                {
                    if (File.Exists(MessengerAccountPath(paths, slot)) && JsonSerializer.Deserialize<MessengerPending>(File.ReadAllBytes(MessengerAccountPath(paths, slot))) is { Pages: >= 0 and <= 100000 } pending)
                        pages += pending.Pages;
                }
                catch (Exception failure) when (failure is IOException or JsonException or UnauthorizedAccessException) { }
            }
        return (pages, File.Exists(PcLocalPath(paths)));
    }

    // The words already logged for what waits, so a poll every few minutes says each once per run.
    private static readonly ConcurrentDictionary<string, string> Said = new(StringComparer.Ordinal);

    private static void Say(Action<string> log, string kind, string outcome, string extra = "")
    {
        if (Said.TryGetValue(kind, out var was) && was == outcome) return;
        Said[kind] = outcome;
        log($"server restore pending kind={kind} outcome={outcome}{extra}");
    }

    /// <summary>
    /// Takes Messenger's pages in when Messenger is connected as the account they belong to. Returns the outcome word, or null when nothing waits: <c>waiting</c>
    /// (not connected), <c>other_account</c> (another account is signed in; the pages stay), <c>imported</c>, or <c>failed</c> (tried again at the next poll).
    /// </summary>
    public static async Task<string?> TryMessengerAsync(ServerBackupPaths paths, IStateProtector protector, IServerBackupMessenger? messenger, Action<string> log,
        CancellationToken cancellationToken = default, string? slot = null)
    {
        MessengerPending? pending;
        try
        {
            if (!File.Exists(MessengerAccountPath(paths, slot))) return null;
            pending = JsonSerializer.Deserialize<MessengerPending>(await File.ReadAllBytesAsync(MessengerAccountPath(paths, slot), cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException) { pending = null; }
        if (pending is not { Pages: >= 0, Account.Length: > 0 }) { Say(log, "messenger", "unreadable"); return "failed"; }
        if (messenger is not { Connected: true }) { Say(log, "messenger", "waiting"); return "waiting"; }
        if (messenger.AccountKey is { } here && here != pending.Account) { Say(log, "messenger", "other_account"); return "other_account"; }
        int added = 0, skipped = 0, imported = 0;
        try
        {
            for (var number = 0; number < pending.Pages; number++)
            {
                var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(MessengerPagePath(paths, number, slot), cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                JsonElement page;
                using (var document = JsonDocument.Parse(plain)) page = document.RootElement.Clone();
                var result = await messenger.ImportPageAsync(page, cancellationToken).ConfigureAwait(false);
                added += result.Added; skipped += result.Skipped; imported++;
            }
        }
        catch (InvalidOperationException failure) when (failure.Message == "backup_other_account") { Say(log, "messenger", "other_account"); return "other_account"; }
        catch (Exception failure) when (failure is InvalidOperationException or IOException or JsonException or TimeoutException or System.Security.Cryptography.CryptographicException)
        {
            if (imported > 0) messenger.Imported();
            Say(log, "messenger", "failed", $" pages={imported} type={failure.GetType().Name}");
            return "failed";
        }
        if (imported > 0) messenger.Imported();
        Directory.Delete(Path.GetDirectoryName(MessengerAccountPath(paths, slot))!, recursive: true);
        Say(log, "messenger", "imported", $" pages={imported} added={added} skipped={skipped}");
        return "imported";
    }

    /// <summary>
    /// Places a PC backup's choices into the shared state once the server files it under a key (an account is signed in): each row under that key, rekeyed from the
    /// conversation it named where the PC's key was another (as a PC restore places them, LocalStateBackup), added where the space holds nothing and never replacing.
    /// Returns the outcome word, or null when nothing waits: <c>waiting</c> (no key yet), <c>imported</c>, <c>refused</c> (the space is filed under another key and the
    /// rows stay), or <c>failed</c>.
    /// </summary>
    public static async Task<string?> TryPcLocalAsync(ServerBackupPaths paths, IStateProtector protector, Func<CancellationToken, Task<string?>> currentKey,
        Func<MoveBundle, CancellationToken, Task<(string Status, int Added, int Skipped, int Refused)>> import, Action<string> log, CancellationToken cancellationToken = default)
    {
        var path = PcLocalPath(paths);
        if (!File.Exists(path)) return null;
        string? key;
        try { key = await currentKey(cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException) { key = null; }
        if (NotificationIdentity.ProfileOf(key) is not { } profile) { Say(log, "pc_local", "waiting"); return "waiting"; }
        LocalStateSnapshot? snapshot;
        try { snapshot = JsonSerializer.Deserialize<LocalStateSnapshot>(await protector.UnprotectAsync(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false), BackupArchive.Json); }
        catch (Exception failure) when (failure is IOException or JsonException or System.Security.Cryptography.CryptographicException or InvalidDataException) { snapshot = null; }
        if (snapshot is null) { Say(log, "pc_local", "unreadable"); return "failed"; }
        var rows = await PlaceAsync(snapshot, profile, protector, Path.Combine(paths.Restore, "placing"), cancellationToken).ConfigureAwait(false);
        var bundle = MoveBundle.Make(key!, rows.Where(row => row.Value.Length <= SharedLimits.ValueBytes)
            .Select(row => new MoveRow(row.Key, JsonSerializer.Deserialize<JsonElement>(row.Value))));
        var (status, added, skipped, refused) = await import(bundle, cancellationToken).ConfigureAwait(false);
        if (status != StateImportStatuses.Done) { Say(log, "pc_local", "refused", $" status={status}"); return "refused"; }
        File.Delete(path);
        Say(log, "pc_local", "imported", $" rows={rows.Count} added={added} skipped={skipped} refused={refused}");
        return "imported";
    }

    // The PC's rows as the shared state keeps them for `profile`: placed exactly as a PC restore places them (LocalStateBackup, into stores that hold nothing), then
    // read back as rows of the shared categories, as a move carries them (the pin, mute, unread and archive files and the names become rows of their own).
    internal static async Task<List<(string Key, byte[] Value)>> PlaceAsync(LocalStateSnapshot snapshot, string profile, IStateProtector protector, string scratch,
        CancellationToken cancellationToken)
    {
        // Beside the server's own data, never the system's temporary folder: the names are sealed there with the server's key, as everything else it keeps.
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        var state = new MemoryState();
        try
        {
            var stores = new LocalStores(state, protector, Path.Combine(scratch, "prefs"), Path.Combine(scratch, "names"), () => null, _ => (0, 0));
            await LocalStateBackup.RestoreAsync(stores, snapshot with { AppPreferences = null }, profile, cancellationToken).ConfigureAwait(false);
            var rows = new List<(string, byte[])>();
            foreach (var (key, value) in state.Rows)
                if (SharedCategories.Match(key, profile) is { HostOwned: false } && IsJson(value)) rows.Add((key, value));
            foreach (var file in Files(stores.ChatPreferences))
                if (LocalStateBackup.PreferenceFile.Match(Path.GetFileName(file)) is { Success: true } match)
                    rows.Add(($"{SharedCategories.ChatPreference}:{match.Groups[1].Value}.{match.Groups[2].Value}", await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)));
            foreach (var file in Files(stores.ContactNames))
                if (LocalStateBackup.NameFile.Match(Path.GetFileName(file)) is { Success: true } match)
                    rows.Add(($"{SharedCategories.ContactName}:{match.Groups[1].Value}",
                        SharedRows.NameValue(Encoding.UTF8.GetString(await protector.UnprotectAsync(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false)))));
            return rows;
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
    }

    private static IEnumerable<string> Files(string folder) => Directory.Exists(folder) ? Directory.GetFiles(folder) : [];

    private static bool IsJson(byte[] value)
    {
        try { using var _ = JsonDocument.Parse(value); return true; }
        catch (JsonException) { return false; }
    }

    // The placing step's rows of state.db, in memory: nothing here is kept.
    private sealed class MemoryState : IPrivateStateStore
    {
        public readonly SortedDictionary<string, byte[]> Rows = new(StringComparer.Ordinal);
        public Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Rows.GetValueOrDefault(key));
        public Task WriteAsync(string key, byte[] value, CancellationToken cancellationToken = default) { Rows[key] = value; return Task.CompletedTask; }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Rows.Remove(key); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> KeysAsync(string prefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. Rows.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal))]);
        public Task<bool> AddAsync(string key, byte[] value, CancellationToken cancellationToken = default) => Task.FromResult(Rows.TryAdd(key, value));
    }
}
