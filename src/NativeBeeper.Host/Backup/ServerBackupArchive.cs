using System.IO.Compression;
using System.Text.Json;
using NativeBeeper.App.Services.Backup;

namespace NativeBeeper.Host.Backup;

/// <summary>
/// What a server backup holds, readable only after it is decrypted (SWITCHBOARD.md stage 10, "What a server backup holds"). <paramref name="Id"/> names this backup,
/// <paramref name="Epoch"/> the server's history epoch when it was made, <paramref name="Counts"/> the items of each part and <paramref name="Pages"/> how many pages
/// each paged part came in. <paramref name="Messenger"/> and <paramref name="LeftOut"/> are as in a PC backup (<see cref="BackupManifest"/>).
/// </summary>
internal sealed record ServerBackupManifest(string Format, int Version, string Id, DateTimeOffset Created, string App, string? Epoch,
    Dictionary<string, int> Counts, Dictionary<string, int> Pages, BackupMessengerSummary? Messenger, string? LeftOut)
{
    public List<BackupAccountPart>? Accounts { get; init; }
    public int MessengerPageCount => (Messenger?.Pages ?? 0) + (Accounts?.Sum(a => a.MessengerPages) ?? 0);
    public bool IncompleteMessenger => LeftOut is "not_connected" or "failed";
    public IEnumerable<BackupMessengerSummary> CompleteMessenger =>
        (Messenger is { } legacy ? new[] { legacy } : []).Concat((Accounts ?? []).Where(a => a.Messenger is not null).Select(a => a.Messenger!));
    // A partial archive never supersedes any last complete anchor, even for accounts it did manage to export.
    public Dictionary<string, string> KeptAfter(IReadOnlyDictionary<string, string>? previous, string name)
    {
        var kept = new Dictionary<string, string>(previous ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (!IncompleteMessenger) foreach (var summary in CompleteMessenger) kept[summary.Account] = name;
        return kept;
    }
    // Both destinations use the same protected names. Older partial copies remain bounded by BackupFolder.Keep.
    public HashSet<string> RetentionNames(BackupSettings current, string name)
    {
        var kept = KeptAfter(current.MessengerKept, name).Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        kept.Add(name);
        if (IncompleteMessenger && current.CompleteArchive is { } complete) kept.Add(complete);
        return kept;
    }
    public int Count(string part) => Counts.GetValueOrDefault(part);
    public int PagesOf(string part) => Pages.GetValueOrDefault(part);
}

/// <summary>A row of <c>state.db</c> (one of <see cref="LocalStateBackup.Categories"/>) or of the shared state, its value unsealed.</summary>
internal sealed record PlainRow(string Key, byte[] Value, int Since = 0);
/// <summary>A device as <c>devices.json</c> keeps it: its token's SHA-256 only.</summary>
internal sealed record BackupDevice(string Id, string Name, DateTimeOffset Created, DateTimeOffset? LastSeen, string Hash);
/// <summary>A saved photo, its bytes unsealed.</summary>
internal sealed record BackupPhoto(string Key, byte[] Bytes, long Kept);
/// <summary>A reference to a saved photo, with the conversation it came from (its owner) unsealed.</summary>
internal sealed record BackupPhotoRef(string Ref, string Photo, string Owner);
/// <summary>A profile of the search archive and its key, unsealed.</summary>
internal sealed record BackupArchiveKey(string Profile, byte[] Key);

/// <summary>
/// The server's identity as a backup carries it (D8): its certificate and key, its devices (with the generation the list had), each phone's push address, the
/// Firebase project's two files, whether requests to join are paused, the key its shared state is filed under, the backup's own settings, and the hash of the
/// password its page's first run chose (10.4; absent from a backup made before it, or of a server whose password is in its environment). Never a sign-in.
/// </summary>
internal sealed record ServerIdentityPart(string? Certificate, string? CertificateKey, long DevicesGeneration, List<BackupDevice> Devices, JsonElement? Push,
    string? FirebaseSettings, string? FirebaseAccount, bool JoinPaused, string? SpaceKey, BackupSettingsPart? Backup, string? PagePassword = null);

/// <summary>The backup's own settings worth keeping across a restore: on or off, and the tag its file names carry, so the restored server goes on pruning its own.</summary>
internal sealed record BackupSettingsPart(bool Enabled, string? Install, Dictionary<string, string>? MessengerKept);

/// <summary>The whole history's key (unsealed) and where its numbers stood.</summary>
internal sealed record HistoryPart(byte[] Key, string Epoch, long Seq);

/// <summary>
/// The payload inside a server backup: a ZIP of manifest.json (last), identity.json, history.json, archive.json and the paged parts, each page one entry of JSON
/// lines' worth of rows (<see cref="Parts"/>), and Messenger's history pages exactly as the helper exported them.
/// </summary>
internal static class ServerBackupArchive
{
    public const string Format = "nativebeeper.server-backup";
    public const int Version = 2;
    public const string ManifestEntry = "manifest.json", IdentityEntry = "identity.json", HistoryEntry = "history.json", ArchiveEntry = "archive.json";

    /// <summary>The paged parts, by the name their pages are filed under.</summary>
    public static class Parts
    {
        public const string State = "state", Shared = "shared", Retired = "retired", HistoryChats = "history-chats", HistoryMessages = "history-messages",
            HistoryRows = "history-rows", Archive = "archive", Photos = "photos", PhotoRefs = "photo-refs", Messenger = "messenger";
        public static IReadOnlyList<string> All { get; } = [State, Shared, Retired, HistoryChats, HistoryMessages, HistoryRows, Archive, Photos, PhotoRefs];
    }

    internal const long MaxSmall = 4 << 20, MaxPage = 32 << 20;
    // A page is cut once its rows reach this, so a reader never holds more than about this much of one part at a time.
    internal const int PageBytes = 4 << 20;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static string AccountPart(string key) => "accounts/" + key + "/archive";
    internal static string AccountMessengerPage(string key, int number) => "accounts/" + key + "/" + BackupArchive.PageEntry(number);
    public static string PageEntry(string part, int number) => $"{part}/page-{number:D6}.json";

    public static void Put(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        // A fixed time, as a PC backup's: the ZIP says nothing about when each part was written beyond what the manifest says.
        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(content);
    }

    /// <summary>The names of every entry a backup with <paramref name="manifest"/> is written with, and nothing else.</summary>
    public static HashSet<string> ExpectedEntries(ServerBackupManifest manifest)
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { ManifestEntry, IdentityEntry, HistoryEntry, ArchiveEntry };
        foreach (var part in Parts.All)
            for (var number = 0; number < manifest.PagesOf(part); number++) names.Add(PageEntry(part, number));
        for (var number = 0; number < (manifest.Messenger?.Pages ?? 0); number++) names.Add(BackupArchive.PageEntry(number));
        foreach (var account in manifest.Accounts ?? [])
        {
            var part = AccountPart(account.Account.Key);
            for (var n = 0; n < manifest.PagesOf(part); n++) names.Add(PageEntry(part, n));
            for (var n = 0; n < account.MessengerPages; n++) names.Add(AccountMessengerPage(account.Account.Key, n));
        }
        return names;
    }
}

/// <summary>Writes one paged part: rows go into a page until it reaches <see cref="ServerBackupArchive.PageBytes"/>, then the page is one entry.</summary>
internal sealed class PartWriter<T>(ZipArchive zip, string part)
{
    private readonly List<T> rows = [];
    private long bytes;
    public int Pages { get; private set; }
    public int Count { get; private set; }

    public void Add(T row, long size)
    {
        rows.Add(row);
        bytes += size + 64;
        Count++;
        if (bytes >= ServerBackupArchive.PageBytes) Flush();
    }

    public void Flush()
    {
        if (rows.Count == 0) return;
        ServerBackupArchive.Put(zip, ServerBackupArchive.PageEntry(part, Pages++), JsonSerializer.SerializeToUtf8Bytes(rows, ServerBackupArchive.Json));
        rows.Clear();
        bytes = 0;
    }
}

/// <summary>An opened server backup payload: its manifest, then its parts read on demand.</summary>
internal sealed class ServerBackupContents : IDisposable
{
    private readonly ZipArchive zip;
    public ServerBackupManifest Manifest { get; }

    private ServerBackupContents(ZipArchive zip, ServerBackupManifest manifest) { this.zip = zip; Manifest = manifest; }

    /// <summary>What the payload says it is, by its manifest's format: a server backup, a PC backup (<see cref="BackupArchive.Format"/>) or null for neither.</summary>
    public static string? FormatOf(Stream payload)
    {
        payload.Position = 0;
        try
        {
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry(ServerBackupArchive.ManifestEntry);
            if (entry is null || entry.Length > ServerBackupArchive.MaxSmall) return null;
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String
                ? format.GetString() : null;
        }
        catch (Exception failure) when (failure is InvalidDataException or JsonException) { return null; }
        finally { payload.Position = 0; }
    }

    /// <summary>Reads the manifest. A payload this build cannot read is refused as <see cref="BackupFileFailure.Unsupported"/>, anything else wrong as Unreadable.</summary>
    public static ServerBackupContents Open(Stream payload)
    {
        payload.Position = 0;
        ZipArchive zip;
        try { zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new BackupFileException(BackupFileFailure.Unreadable); }
        try
        {
            var manifest = Read<ServerBackupManifest>(zip, ServerBackupArchive.ManifestEntry, ServerBackupArchive.MaxSmall);
            if (manifest?.Format != ServerBackupArchive.Format || manifest.Counts is null || manifest.Pages is null || manifest.Id is not { Length: > 0 })
                throw new BackupFileException(BackupFileFailure.Unreadable);
            if (manifest.Version is < 1 or > ServerBackupArchive.Version) throw new BackupFileException(BackupFileFailure.Unsupported);
            if (manifest.Messenger is { } messenger && (messenger.Pages < 1 || messenger.Counts is null ||
                messenger.Account is not { Length: 32 } || !messenger.Account.All(char.IsAsciiHexDigitLower)))
                throw new BackupFileException(BackupFileFailure.Unreadable);
            ServerBackupAccounts.Validate(manifest);
            return new(zip, manifest);
        }
        catch { zip.Dispose(); throw; }
    }

    public ServerIdentityPart ReadIdentity() => Read<ServerIdentityPart>(zip, ServerBackupArchive.IdentityEntry, ServerBackupArchive.MaxSmall) ?? throw Unreadable();
    public HistoryPart? ReadHistory() => Read<HistoryPart?>(zip, ServerBackupArchive.HistoryEntry, ServerBackupArchive.MaxSmall);
    public List<BackupArchiveKey> ReadArchiveKeys() => Read<List<BackupArchiveKey>>(zip, ServerBackupArchive.ArchiveEntry, ServerBackupArchive.MaxSmall) ?? throw Unreadable();

    /// <summary>A paged part's rows in the order they were written, a page parsed only when it is reached.</summary>
    public IEnumerable<T> ReadPart<T>(string part)
    {
        for (var number = 0; number < Manifest.PagesOf(part); number++)
            foreach (var row in Read<List<T>>(zip, ServerBackupArchive.PageEntry(part, number), ServerBackupArchive.MaxPage) ?? throw Unreadable())
                yield return row ?? throw Unreadable();
    }

    /// <summary>Messenger's pages in the order they were exported.</summary>
    public IEnumerable<JsonElement> ReadMessengerPages(BackupAccountPart? account = null)
    {
        var summary = account is null ? Manifest.Messenger : account.Messenger;
        var total = account?.MessengerPages ?? Manifest.Messenger?.Pages ?? 0;
        string? export = null;
        for (var number = 0; number < total; number++)
        {
            var entry = zip.GetEntry(account is null ? BackupArchive.PageEntry(number) : ServerBackupArchive.AccountMessengerPage(account.Account.Key, number));
            if (entry is null || entry.Length > BackupArchive.MaxPage) throw Unreadable();
            JsonElement page;
            using (var stream = entry.Open())
            using (var document = JsonDocument.Parse(stream)) page = document.RootElement.Clone();
            var read = NativeBeeper.App.Services.Messenger.MessengerHistoryPage.Read(page);
            var identity = account?.Account.Identity is { } bound ? bound[..32] : summary?.Account;
            if (read is null || read.Number != number || identity is not null && read.Account != identity || export is not null && read.Export != export
                || summary is not null && read.IsLast != (number == total - 1)) throw Unreadable();
            export = read.Export;
            yield return page;
        }
    }

    /// <summary>
    /// Reads every part once, as a restore would, and checks nothing else went in and every count holds. Throws <see cref="BackupFileException"/> (Unreadable) when not.
    /// </summary>
    public void Verify(CancellationToken cancellationToken)
    {
        if (zip.Entries.Count != ServerBackupArchive.ExpectedEntries(Manifest).Count || !zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal).SetEquals(ServerBackupArchive.ExpectedEntries(Manifest))) throw Unreadable();
        _ = ReadIdentity();
        _ = ReadHistory();
        _ = ReadArchiveKeys();
        void Count<T>(string part)
        {
            var n = 0;
            foreach (var _ in ReadPart<T>(part)) { n++; cancellationToken.ThrowIfCancellationRequested(); }
            if (n != Manifest.Count(part)) throw Unreadable();
        }
        Count<PlainRow>(ServerBackupArchive.Parts.State);
        Count<PlainRow>(ServerBackupArchive.Parts.Shared);
        Count<RetiredRow>(ServerBackupArchive.Parts.Retired);
        Count<NativeBeeper.Storage.HistoryRawChat>(ServerBackupArchive.Parts.HistoryChats);
        Count<NativeBeeper.Storage.HistoryRawMessage>(ServerBackupArchive.Parts.HistoryMessages);
        Count<NativeBeeper.Storage.HistoryRawRow>(ServerBackupArchive.Parts.HistoryRows);
        Count<NativeBeeper.Storage.ArchiveRawRecord>(ServerBackupArchive.Parts.Archive);
        Count<BackupPhoto>(ServerBackupArchive.Parts.Photos);
        Count<BackupPhotoRef>(ServerBackupArchive.Parts.PhotoRefs);
        var pages = 0;
        foreach (var _ in ReadMessengerPages()) { pages++; cancellationToken.ThrowIfCancellationRequested(); }
        if (pages != (Manifest.Messenger?.Pages ?? 0)) throw Unreadable();
        foreach (var account in Manifest.Accounts ?? [])
        {
            Count<NativeBeeper.Storage.ArchiveRawRecord>(ServerBackupArchive.AccountPart(account.Account.Key));
            foreach (var _ in ReadMessengerPages(account)) cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static BackupFileException Unreadable() => new(BackupFileFailure.Unreadable);

    private static T? Read<T>(ZipArchive zip, string name, long limit)
    {
        var entry = zip.GetEntry(name);
        if (entry is null || entry.Length > limit) throw Unreadable();
        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<T>(stream, ServerBackupArchive.Json);
        }
        catch (JsonException) { throw Unreadable(); }
        catch (InvalidDataException) { throw Unreadable(); }
    }

    public void Dispose() => zip.Dispose();
}

/// <summary>A retired id's digest and when it was retired: it stays retired through a restore, so a client's create of a message that already went is still refused.</summary>
internal sealed record RetiredRow(string Id, long At);
