using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.Backup;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Storage;

namespace NativeBeeper.Host.Backup;

internal sealed record BackupAccountPart(ProviderAccounts.Registration Account, List<BackupArchiveKey> ArchiveKeys,
    BackupMessengerSummary? Messenger, int MessengerPages);

// Only the registry's immutable bindings and history are exported. No file-tree copy: session, browser, guard and
// encrypted-device stores must never slip into an archive because a provider added another file beside its history.
internal static class ServerBackupAccounts
{
    internal static string Root(ServerBackupPaths paths, string slot) => Path.Combine(paths.Data, "ProviderAccounts", slot);
    internal static string Registry(ServerBackupPaths paths) => Path.Combine(paths.Data, "ProviderAccounts", "registry.protected");

    internal static bool HasSessions(ServerBackupPaths paths, bool includeGoogle = true)
    {
        var root = Path.Combine(paths.Data, "ProviderAccounts");
        if (!Directory.Exists(root)) return false;
        // Names and existence only: restore never opens session contents to decide whether they must be signed out.
        foreach (var slot in Directory.EnumerateDirectories(root))
        {
            if (!AccountNamespace.ValidKey(Path.GetFileName(slot))) continue;
            if ((File.GetAttributes(slot) & FileAttributes.ReparsePoint) != 0) return true;
            if (File.Exists(Path.Combine(slot, "Messenger", "session.protected")) || File.Exists(Path.Combine(slot, "Instagram", "active.protected"))
                || includeGoogle && File.Exists(Path.Combine(slot, "GoogleMessagesSpike", "session.protected"))) return true;
        }
        return false;
    }

    internal static void Validate(ServerBackupManifest manifest)
    {
        var accounts = manifest.Accounts ?? [];
        if (accounts.Count > 256 || accounts.Any(a => a is null || a.Account is null || a.ArchiveKeys is null) || accounts.Select(a => a.Account.Key).Distinct().Count() != accounts.Count
            || accounts.Any(a => a.Account.Pending || !AccountNamespace.ValidKey(a.Account.Key) || a.Account.Service is not ("google" or "messenger" or "instagram")
                || a.Account.Identity is { } id && (id.Length != 64 || !id.All(char.IsAsciiHexDigitLower))
                || a.MessengerPages < 0 || a.MessengerPages > 100000
                || a.Messenger is { } m && (m.Pages < 1 || m.Counts is null || m.Pages != a.MessengerPages || m.Account is not { Length: 32 } || !m.Account.All(char.IsAsciiHexDigitLower) || a.Account.Service != "messenger" || a.Account.Identity is not { } identity || m.Account != identity[..32])))
            throw new BackupFileException(BackupFileFailure.Unreadable);
    }

    internal static (List<BackupAccountPart>? Accounts, string? LeftOut) Write(ZipArchive zip, ServerBackupPaths paths,
        IStateProtector protector, IReadOnlyDictionary<string, IServerBackupMessenger>? messenger,
        Dictionary<string, int> counts, Dictionary<string, int> pages, CancellationToken token)
    {
        if (!File.Exists(Registry(paths))) return (null, null);
        byte[] Open(byte[] bytes) => protector.UnprotectAsync(bytes).GetAwaiter().GetResult();
        var plain = Open(File.ReadAllBytes(Registry(paths)));
        ProviderAccounts.Registration[] registrations;
        try { registrations = JsonSerializer.Deserialize<ProviderAccounts.Registration[]>(plain) ?? throw new InvalidDataException("account_registry"); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        var result = registrations.Where(r => !r.Pending).Select(r => new BackupAccountPart(r, [], null, 0)).ToList();
        // Validate path components before opening any account store.
        Validate(new("", 2, "", default, "", null, [], [], null, null) { Accounts = result });
        string? leftOut = null;
        for (var index = 0; index < result.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var part = result[index];
            var slot = part.Account.Key;
            var archivePart = ServerBackupArchive.AccountPart(slot);
            var records = new PartWriter<ArchiveRawRecord>(zip, archivePart);
            MessageArchive.ExportRaw(Path.Combine(Root(paths, slot), "archive.db"),
                (profile, key) => part.ArchiveKeys.Add(new(profile, Open(key))), record => records.Add(record, record.Payload.Length), paths.Snapshots, token);
            records.Flush();
            counts[archivePart] = records.Count;
            pages[archivePart] = records.Pages;
            if (part.Account.Service != "messenger" || part.Account.Removed) continue;
            if (messenger?.GetValueOrDefault(slot) is not { Connected: true } source)
            {
                if (part.Account.Identity is not null && leftOut != "failed") leftOut = "not_connected";
                continue;
            }
            var number = 0;
            string? account = null, cursor = null;
            BackupMessengerSummary? summary = null;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var page = source.ExportPageAsync(cursor, token).GetAwaiter().GetResult();
                    if (page.Number != number || part.Account.Identity is { } bound && page.Account != bound[..32] || account is not null && account != page.Account) throw new InvalidDataException("messenger_sequence");
                    account = page.Account;
                    ServerBackupArchive.Put(zip, ServerBackupArchive.AccountMessengerPage(slot, number++), Encoding.UTF8.GetBytes(page.Raw.GetRawText()));
                    if (page.IsLast)
                    {
                        var exported = new Dictionary<string, int>();
                        if (!page.Raw.TryGetProperty("exported", out var values)) throw new InvalidDataException("messenger_incomplete");
                        if (values.ValueKind == JsonValueKind.Object)
                            foreach (var value in values.EnumerateObject()) exported[value.Name] = value.Value.GetInt32();
                        summary = new(account, number, exported);
                        break;
                    }
                    if (string.IsNullOrEmpty(page.Next) || page.Next == cursor) throw new InvalidDataException("messenger_cursor");
                    cursor = page.Next;
                }
            }
            catch (Exception) when (!token.IsCancellationRequested) { leftOut = "failed"; }
            finally { try { source.EndExportAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch (Exception) { } }
            result[index] = part with { Messenger = summary, MessengerPages = number };
        }
        return (result, leftOut);
    }
}
