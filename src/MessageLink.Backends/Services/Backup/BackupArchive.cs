using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MessageLink.App.Services.Messenger;

namespace MessageLink.App.Services.Backup;

/// <summary>Messenger's part of a backup: whose history it is (the helper's one-way store owner key), how many pages
/// it came in, and the helper's own count of what it exported, by kind.</summary>
internal sealed record BackupMessengerSummary(string Account, int Pages, Dictionary<string, int> Counts);

/// <summary>
/// What a backup holds, readable only after it is decrypted. LeftOut says why Messenger is not in it, when it is not:
/// not_connected, not_signed_in, or failed (its history could not be read, so the rest was backed up without it).
/// </summary>
internal sealed record BackupManifest(string Format, int Version, DateTimeOffset Created, string App,
    BackupMessengerSummary? Messenger, string? LeftOut, Dictionary<string, int> Local)
{
    public int MessengerMessages => Messenger?.Counts.GetValueOrDefault("messages") ?? 0;
    public int MessengerConversations => Messenger?.Counts.GetValueOrDefault("threads") ?? 0;
    public int LocalItems => Local.Values.Sum();
}

/// <summary>
/// The payload inside a backup file: a ZIP of manifest.json, local.json (the local stores) and Messenger's history
/// pages exactly as the helper exported them, one entry each, in order.
/// </summary>
internal static class BackupArchive
{
    public const string Format = "messagelink.backup";
    public const int Version = 1;
    public const string ManifestEntry = "manifest.json", LocalEntry = "local.json";
    internal const long MaxManifest = 1 << 20, MaxLocal = 512L << 20, MaxPage = 4 << 20;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string PageEntry(int number) => $"messenger/page-{number:D6}.json";

    /// <summary>
    /// Writes the payload. Pages are taken one at a time as the helper hands them out, and must arrive in order, from
    /// one account, ending with the last; anything else fails the backup rather than writing half a history.
    /// </summary>
    public static BackupManifest Write(Stream payload, LocalStateSnapshot local, IEnumerable<MessengerHistoryPage>? messenger,
        string? leftOut, string app, DateTimeOffset created)
    {
        BackupManifest manifest;
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put(zip, LocalEntry, JsonSerializer.SerializeToUtf8Bytes(local, Json));
            BackupMessengerSummary? summary = null;
            if (messenger is not null)
            {
                string? account = null;
                var number = 0;
                Dictionary<string, int>? counts = null;
                foreach (var page in messenger)
                {
                    if (page.Number != number || account is not null && page.Account != account) throw new InvalidDataException("messenger_sequence");
                    account = page.Account;
                    Put(zip, PageEntry(number++), Encoding.UTF8.GetBytes(page.Raw.GetRawText()));
                    if (page.IsLast) { counts = Counts(page.Raw, "exported"); break; }
                }
                summary = counts is null || account is null ? throw new InvalidDataException("messenger_incomplete") : new(account, number, counts);
            }
            manifest = new(Format, Version, created, app, summary, summary is null ? leftOut : null, LocalStateBackup.Counts(local));
            // Last, because only now are Messenger's counts known. A reader finds it through the ZIP's directory.
            Put(zip, ManifestEntry, JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        }
        return manifest;
    }

    public static int EntryCount(BackupManifest manifest) => 2 + (manifest.Messenger?.Pages ?? 0);

    private static void Put(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        // A fixed time, so the ZIP says nothing about when each part was written beyond what the manifest says.
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static Dictionary<string, int> Counts(JsonElement page, string name)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (page.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object)
            foreach (var count in section.EnumerateObject())
                if (count.Value.ValueKind == JsonValueKind.Number && count.Value.TryGetInt32(out var value)) counts[count.Name] = value;
        return counts;
    }
}

/// <summary>An opened payload: its manifest, then its parts read on demand.</summary>
internal sealed class BackupContents : IDisposable
{
    private readonly ZipArchive zip;
    public BackupManifest Manifest { get; }

    private BackupContents(ZipArchive zip, BackupManifest manifest) { this.zip = zip; Manifest = manifest; }

    /// <summary>Reads the manifest. A payload that authenticated but is not one this build understands is refused
    /// as <see cref="BackupFileFailure.Unsupported"/>; one that is not a backup payload at all as Unreadable.</summary>
    public static BackupContents Open(Stream payload)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new BackupFileException(BackupFileFailure.Unreadable); }
        try
        {
            var manifest = Read<BackupManifest>(zip, BackupArchive.ManifestEntry, BackupArchive.MaxManifest);
            if (manifest is not null && RetiredBackupFormats.Is(manifest.Format)) throw new BackupFileException(BackupFileFailure.Retired);
            if (manifest is null || manifest.Format != BackupArchive.Format || manifest.Local is null) throw new BackupFileException(BackupFileFailure.Unreadable);
            if (manifest.Version != BackupArchive.Version) throw new BackupFileException(BackupFileFailure.Unsupported);
            if (manifest.Messenger is { } messenger && (messenger.Pages < 1 || messenger.Counts is null ||
                messenger.Account is not { Length: 32 } || !messenger.Account.All(char.IsAsciiHexDigitLower)))
                throw new BackupFileException(BackupFileFailure.Unreadable);
            return new(zip, manifest);
        }
        catch { zip.Dispose(); throw; }
    }

    public LocalStateSnapshot ReadLocal() =>
        Read<LocalStateSnapshot>(zip, BackupArchive.LocalEntry, BackupArchive.MaxLocal) ?? throw new BackupFileException(BackupFileFailure.Unreadable);

    /// <summary>Messenger's pages in the order they were exported, each parsed only when it is reached. A page this build does not read is refused here, at
    /// the check, the preview and the restore, rather than once Messenger is signed in again: Retired for one in the format's earlier name, Unreadable otherwise.</summary>
    public IEnumerable<JsonElement> ReadPages()
    {
        for (var number = 0; number < (Manifest.Messenger?.Pages ?? 0); number++)
        {
            var entry = zip.GetEntry(BackupArchive.PageEntry(number));
            if (entry is null || entry.Length > BackupArchive.MaxPage) throw new BackupFileException(BackupFileFailure.Unreadable);
            JsonElement page;
            using (var stream = entry.Open())
            using (var document = JsonDocument.Parse(stream)) page = document.RootElement.Clone();
            if (MessengerHistoryPage.IsRetired(page)) throw new BackupFileException(BackupFileFailure.Retired);
            if (MessengerHistoryPage.Read(page) is null) throw new BackupFileException(BackupFileFailure.Unreadable);
            yield return page;
        }
    }

    /// <summary>The names of every part, for a check that nothing else ever went in.</summary>
    public IReadOnlyList<string> EntryNames => zip.Entries.Select(entry => entry.FullName).ToArray();

    private static T? Read<T>(ZipArchive zip, string name, long limit)
    {
        var entry = zip.GetEntry(name);
        if (entry is null || entry.Length > limit) throw new BackupFileException(BackupFileFailure.Unreadable);
        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<T>(stream, BackupArchive.Json);
        }
        catch (JsonException) { throw new BackupFileException(BackupFileFailure.Unreadable); }
        catch (InvalidDataException) { throw new BackupFileException(BackupFileFailure.Unreadable); }
    }

    public void Dispose() => zip.Dispose();
}
