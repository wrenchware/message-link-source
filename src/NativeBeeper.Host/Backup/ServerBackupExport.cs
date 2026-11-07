using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NativeBeeper.App.Services.Backup;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;
using NativeBeeper.Storage;

namespace NativeBeeper.Host.Backup;

/// <summary>
/// Reads a server's stores into a backup payload (SWITCHBOARD.md stage 10, "What a server backup holds"). Every value its server's key sealed is unsealed into the
/// backup, which is itself sealed under the backup's password; the restoring server seals each again with its own key. Each store is read in one transaction
/// (each store's own <c>ExportRaw</c>), the history's under its write-ahead log's snapshot, Messenger's through the helper's own export.
/// </summary>
/// <remarks>
/// Left out, on purpose: every service's sign-in (the sessions, Google's browser profiles, Instagram's guard, Voice's cookies), Messenger's encrypted-device
/// database, the tailnet door's identity, the outbox (<c>outbox:</c> rows) and the journal, and the caches and logs. A send in flight is never repeated by a restore,
/// and a sign-in restored beside the one still in use would be two clients on one device.
/// </remarks>
internal static class ServerBackupExport
{
    /// <summary>The shared categories a backup never carries: sends whose outcome is unknown are never replayed elsewhere.</summary>
    internal static readonly string[] SharedLeftOut = [Protocol.SharedCategories.Outbox + ":"];

    public static ServerBackupManifest Write(Stream payload, ServerBackupPaths paths, IStateProtector protector, BackupSettingsPart settings,
        IEnumerable<MessengerHistoryPage>? messenger, string? leftOut, string app, DateTimeOffset created, CancellationToken cancellationToken, IReadOnlyDictionary<string, IServerBackupMessenger>? additionalMessenger = null)
    {
        byte[] Open(byte[] sealedBytes) => protector.UnprotectAsync(sealedBytes).GetAwaiter().GetResult();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var pages = new Dictionary<string, int>(StringComparer.Ordinal);
        string? epoch = null;
        ServerBackupManifest manifest;
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Done<T>(string part, PartWriter<T> writer)
            {
                writer.Flush();
                counts[part] = writer.Count;
                pages[part] = writer.Pages;
            }

            // The server's identity first: what lets every device come back to it.
            var identity = Identity(paths, protector, settings);
            ServerBackupArchive.Put(zip, ServerBackupArchive.IdentityEntry, JsonSerializer.SerializeToUtf8Bytes(identity, ServerBackupArchive.Json));
            counts["devices"] = identity.Devices.Count;

            // state.db: the rows a PC backup carries (LocalStateBackup's allowlist), and nothing else of it.
            var state = new PartWriter<PlainRow>(zip, ServerBackupArchive.Parts.State);
            foreach (var (key, sealedBytes) in SqlitePrivateStateStore.ReadSealed(paths.State, [.. LocalStateBackup.Categories.Keys.Select(category => category + ":")]))
            {
                var value = Open(sealedBytes);
                state.Add(new(key, value), value.Length);
            }
            Done(ServerBackupArchive.Parts.State, state);

            // The shared state, live rows only, without the outbox; and the ids it has retired for good.
            var (rows, retired) = SharedStateStore.ReadSealed(paths.Shared, cancellationToken);
            var shared = new PartWriter<PlainRow>(zip, ServerBackupArchive.Parts.Shared);
            foreach (var row in rows)
            {
                if (SharedLeftOut.Any(prefix => row.Key.StartsWith(prefix, StringComparison.Ordinal))) continue;
                var value = Open(row.Sealed);
                shared.Add(new(row.Key, value, row.Since), value.Length);
            }
            Done(ServerBackupArchive.Parts.Shared, shared);
            var retiredRows = new PartWriter<RetiredRow>(zip, ServerBackupArchive.Parts.Retired);
            foreach (var (id, at) in retired) retiredRows.Add(new(id, at), id.Length);
            Done(ServerBackupArchive.Parts.Retired, retiredRows);

            // The whole history: its rows as kept, and its key unsealed, so the restoring server seals that key with its own.
            var chats = new PartWriter<HistoryRawChat>(zip, ServerBackupArchive.Parts.HistoryChats);
            var messages = new PartWriter<HistoryRawMessage>(zip, ServerBackupArchive.Parts.HistoryMessages);
            var listed = new PartWriter<HistoryRawRow>(zip, ServerBackupArchive.Parts.HistoryRows);
            var head = HistoryDatabase.ExportRaw(paths.History, chat => chats.Add(chat, chat.Payload.Length), message => messages.Add(message, message.Payload.Length),
                row => listed.Add(row, row.Payload.Length), cancellationToken);
            HistoryPart? history = null;
            if (head is { } found)
            {
                history = new HistoryPart(Open(found.SealedKey), found.Epoch, found.Seq);
                epoch = found.Epoch;
            }
            ServerBackupArchive.Put(zip, ServerBackupArchive.HistoryEntry, JsonSerializer.SerializeToUtf8Bytes(history, ServerBackupArchive.Json));
            Done(ServerBackupArchive.Parts.HistoryChats, chats);
            Done(ServerBackupArchive.Parts.HistoryMessages, messages);
            Done(ServerBackupArchive.Parts.HistoryRows, listed);

            // The search archive and the saved photos keep a rollback journal, so each is copied whole first (StoreSnapshot) and the copy read: a writer waits only for
            // the copy, never for the compressing and encrypting.
            if (Directory.Exists(paths.Snapshots)) Directory.Delete(paths.Snapshots, recursive: true);
            // The Google search archive, the same way: its records as kept, each profile's key unsealed.
            var keys = new List<BackupArchiveKey>();
            var records = new PartWriter<ArchiveRawRecord>(zip, ServerBackupArchive.Parts.Archive);
            MessageArchive.ExportRaw(paths.Archive, (profile, sealedKey) => keys.Add(new(profile, Open(sealedKey))), record => records.Add(record, record.Payload.Length),
                paths.Snapshots, cancellationToken);
            ServerBackupArchive.Put(zip, ServerBackupArchive.ArchiveEntry, JsonSerializer.SerializeToUtf8Bytes(keys, ServerBackupArchive.Json));
            Done(ServerBackupArchive.Parts.Archive, records);

            // Saved photos, unsealed (their owners too).
            var photos = new PartWriter<BackupPhoto>(zip, ServerBackupArchive.Parts.Photos);
            var references = new PartWriter<BackupPhotoRef>(zip, ServerBackupArchive.Parts.PhotoRefs);
            SavedPhotoStore.ExportRaw(paths.Photos, (key, sealedBytes, kept) =>
            {
                var bytes = Open(sealedBytes);
                photos.Add(new(key, bytes, kept), bytes.Length);
            }, (reference, photo, sealedOwner) => references.Add(new(reference, photo, Encoding.UTF8.GetString(Open(sealedOwner))), 128), paths.Snapshots, cancellationToken);
            Done(ServerBackupArchive.Parts.Photos, photos);
            Done(ServerBackupArchive.Parts.PhotoRefs, references);

            // Messenger's history last: the helper's pages, as a PC backup holds them.
            BackupMessengerSummary? summary = null;
            if (messenger is not null)
            {
                string? account = null;
                var number = 0;
                Dictionary<string, int>? exported = null;
                foreach (var page in messenger)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (page.Number != number || account is not null && page.Account != account) throw new InvalidDataException("messenger_sequence");
                    account = page.Account;
                    ServerBackupArchive.Put(zip, BackupArchive.PageEntry(number++), Encoding.UTF8.GetBytes(page.Raw.GetRawText()));
                    if (page.IsLast) { exported = Exported(page.Raw); break; }
                }
                summary = exported is null || account is null ? throw new InvalidDataException("messenger_incomplete") : new(account, number, exported);
            }
            var added = ServerBackupAccounts.Write(zip, paths, protector, additionalMessenger, counts, pages, cancellationToken);
            if (added.LeftOut is not null && leftOut != "failed") leftOut = added.LeftOut;
            manifest = new(ServerBackupArchive.Format, added.Accounts is null ? 1 : ServerBackupArchive.Version, Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
                created, app, epoch, counts, pages, summary, leftOut is "not_connected" or "failed" ? leftOut : summary is null ? leftOut : null) { Accounts = added.Accounts };
            // Last, as a PC backup's: only now are the counts known. A reader finds it through the ZIP's directory.
            ServerBackupArchive.Put(zip, ServerBackupArchive.ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, ServerBackupArchive.Json));
        }
        return manifest;
    }

    /// <summary>The server's identity as it is now, unsealed (D8): certificate, devices, push, the Firebase files, the join pause, the space key and the backup's settings.</summary>
    internal static ServerIdentityPart Identity(ServerBackupPaths paths, IStateProtector protector, BackupSettingsPart settings)
    {
        byte[]? Opened(string path) => File.Exists(path) ? protector.UnprotectAsync(File.ReadAllBytes(path)).GetAwaiter().GetResult() : null;
        string? Text(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
        var devices = new List<BackupDevice>();
        long generation = 0;
        if (Opened(paths.Devices) is { } list)
        {
            using var document = JsonDocument.Parse(list);
            var root = document.RootElement;
            // A list from before generations is a bare array.
            var items = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("devices");
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("generation", out var written)) generation = written.GetInt64();
            devices.AddRange(items.Deserialize<List<BackupDevice>>(ServerBackupArchive.Json) ?? []);
        }
        JsonElement? push = Opened(paths.Push) is { } registrations ? JsonSerializer.Deserialize<JsonElement>(registrations) : null;
        string? space = null;
        if (Opened(paths.Space) is { } spaceFile)
        {
            using var document = JsonDocument.Parse(spaceFile);
            space = document.RootElement.TryGetProperty("LocalKey", out var key) ? key.GetString() : null;
        }
        // The project's files where the server reads them: the keys folder's, or the data folder's push folder when the keys folder has none.
        var project = File.Exists(Path.Combine(paths.PushProject, ServerBackupPaths.FirebaseSettings)) ? paths.PushProject : Path.Combine(paths.Data, "push");
        string? settingsFile = Text(Path.Combine(project, ServerBackupPaths.FirebaseSettings)), accountFile = Text(Path.Combine(project, ServerBackupPaths.FirebaseAccount));
        if (settingsFile is null || accountFile is null) settingsFile = accountFile = null;
        // A Windows server keeps the certificate's key sealed to its user (TlsKeySealed); the backup holds it unsealed, as it holds every other secret.
        var tlsKey = paths.TlsKeySealed && Opened(paths.TlsKey) is { } openedKey ? Encoding.UTF8.GetString(openedKey) : paths.TlsKeySealed ? null : Text(paths.TlsKey);
        return new(Text(paths.TlsCertificate), tlsKey, generation, devices, push, settingsFile, accountFile, File.Exists(paths.JoinPause), space, settings,
            Text(paths.PagePassword));
    }

    private static Dictionary<string, int> Exported(JsonElement page)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (page.TryGetProperty("exported", out var section) && section.ValueKind == JsonValueKind.Object)
            foreach (var count in section.EnumerateObject())
                if (count.Value.ValueKind == JsonValueKind.Number && count.Value.TryGetInt32(out var value)) counts[count.Name] = value;
        return counts;
    }
}
