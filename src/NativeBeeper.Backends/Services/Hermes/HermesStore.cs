using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Hermes;

/// <summary>The address and the profiles, each with its own key. Saved sealed; the keys never leave the server.</summary>
public sealed record HermesSettings(string? Address, IReadOnlyList<HermesProfileSetting> Profiles)
{
    public static HermesSettings Empty { get; } = new(null, []);
    public bool Configured => HermesClient.ParseBase(Address) is not null && Profiles.Count > 0;
}
/// <summary>A profile: its name in Hermes, its key, and the thread's name when the owner gave it one (null: <see cref="HermesBackend.DefaultTitle"/>).</summary>
public sealed record HermesProfileSetting(string Name, string Key, string? Title = null);

/// <summary>One message of a thread as the server keeps it: Hermes holds no history this side can read back.</summary>
public sealed record HermesStoredMessage(string Id, DateTimeOffset At, bool Outgoing, string? Text,
    // "message" (default), "note" (a line of ours: Hermes unreachable, a new conversation), "scheduled" (a repeat's prompt).
    string Kind = "message",
    // Outgoing: "sent", "failed". Incoming: "streaming" while the reply is still filling in, "done", "interrupted".
    string State = "sent",
    IReadOnlyList<HermesStoredImage>? Images = null,
    // A report (IAgentReports): "report", posted with the profile's report token outside a turn; null for everything else. The key and the digest of the
    // cleaned text are kept for reports only, so a retry within a day is recognised after a restart.
    string? Origin = null, string? ReportKey = null, string? ReportDigest = null,
    // A report's place in the thread's own sequence (HermesThread.NextReportSeq): the next turn carries the unanswered ones by it, never by time.
    long ReportSeq = 0,
    // Kind "approval": the command Hermes holds and how the request stands. What it takes to answer it (the run and request ids) is kept in memory only,
    // since Hermes stops waiting when the turn's stream ends; a request still pending at load is closed.
    NativeBeeper.Core.Messaging.AgentApproval? Approval = null);
public sealed record HermesStoredImage(string Id, string MimeType, long Size, double? Width = null, double? Height = null);

/// <summary>The report tokens: a row a profile with reports on. Only a token's SHA-256 (hex) is kept, with its one-way log tag.</summary>
public sealed record HermesReportTokens(IReadOnlyList<HermesReportToken> Rows)
{
    public static HermesReportTokens Empty { get; } = new([]);
}
/// <summary>One token: the profile whose thread it writes into, the kind of agent behind it ("hermes" today), its hash, tag and when it was made, and the
/// address its code names (host and port; absent in older files), which a new code starts from. A token set up in one command also keeps the setup code's
/// hash and id (so a setup whose answer was lost can finish, even after a restart, and the page can say it is done) and the address the setup came from.</summary>
public sealed record HermesReportToken(string Profile, string Source, string Hash, string Tag, DateTimeOffset Created, string? Address = null,
    string? SetupHash = null, string? SetupId = null, string? SetupFrom = null);

/// <summary>A thread: its Hermes session and its messages.</summary>
public sealed record HermesThread(string Profile, string SessionId, IReadOnlyList<HermesStoredMessage> Messages, string? Title = null,
    DateTimeOffset? ReadUpTo = null,
    // The last report sequence handed out, and the highest one a turn Hermes took has carried (or a new conversation passed): the reports above it and
    // at most the snapshot's go with the owner's next message. Absent in older files, so both read as 0.
    long NextReportSeq = 0, long ReportsAckedThrough = 0,
    // After Hermes couldn't continue a session and the thread moved to a new one (HermesBackend.Restart): the recent messages, as the JSON line the next
    // turn carries so the new session knows what came before. Cleared once Hermes takes a turn that carried it, or by New conversation.
    string? Handoff = null);

/// <summary>
/// Everything Hermes keeps between runs, under the data folder's <c>Hermes</c> folder, each file sealed by the host's protector:
/// the settings (address, profiles and their keys), one file a thread (its session id and messages), and the pictures sent to it.
/// Message bodies are private, so nothing here is written in the clear.
/// </summary>
public sealed class HermesStore(string root, IStateProtector protector)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly SemaphoreSlim gate = new(1, 1);

    private string SettingsPath => Path.Combine(root, "settings.protected");
    private string ThreadPath(string profile) => Path.Combine(root, "threads", profile + ".protected");
    private string MediaPath(string id) => Path.Combine(root, "media", id + ".protected");

    public async Task<HermesSettings> ReadSettingsAsync() => await ReadAsync<HermesSettings>(SettingsPath) ?? HermesSettings.Empty;
    public Task SaveSettingsAsync(HermesSettings settings) => WriteAsync(SettingsPath, settings);

    // The report tokens' hashes, one a profile with reports on. Not in backups (the Hermes folder isn't): after a restore reports are turned on again.
    private string ReportsPath => Path.Combine(root, "reports.protected");
    public async Task<HermesReportTokens> ReadReportTokensAsync() => await ReadAsync<HermesReportTokens>(ReportsPath) ?? HermesReportTokens.Empty;
    public Task SaveReportTokensAsync(HermesReportTokens tokens) => WriteAsync(ReportsPath, tokens);

    public async Task<HermesThread?> ReadThreadAsync(string profile) =>
        HermesClient.ValidProfile(profile) ? await ReadAsync<HermesThread>(ThreadPath(profile)) : null;
    public Task SaveThreadAsync(HermesThread thread) =>
        HermesClient.ValidProfile(thread.Profile) ? WriteAsync(ThreadPath(thread.Profile), thread) : throw new ArgumentException("Invalid profile.");
    public void DeleteThread(string profile)
    {
        if (HermesClient.ValidProfile(profile)) try { File.Delete(ThreadPath(profile)); } catch (IOException) { }
    }

    /// <summary>A media id is ours only: 32 lowercase hex characters, the only thing that ever names a file here.</summary>
    public static bool ValidMediaId(string? id) => id is { Length: 32 } && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    public static string NewMediaId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public async Task SaveMediaAsync(string id, byte[] bytes)
    {
        if (!ValidMediaId(id)) throw new ArgumentException("Invalid media id.");
        Directory.CreateDirectory(Path.GetDirectoryName(MediaPath(id))!);
        await ReplaceAsync(MediaPath(id), await protector.ProtectAsync(bytes));
    }

    public async Task<byte[]?> ReadMediaAsync(string id)
    {
        if (!ValidMediaId(id) || !File.Exists(MediaPath(id))) return null;
        try { return await protector.UnprotectAsync(await File.ReadAllBytesAsync(MediaPath(id))); }
        catch (Exception exception) when (exception is IOException or CryptographicException or UnauthorizedAccessException) { return null; }
    }

    private async Task<T?> ReadAsync<T>(string path) where T : class
    {
        await gate.WaitAsync();
        try
        {
            if (!File.Exists(path)) return null;
            var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(path));
            return JsonSerializer.Deserialize<T>(plain, Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException or CryptographicException or UnauthorizedAccessException) { return null; }
        finally { gate.Release(); }
    }

    private async Task WriteAsync<T>(string path, T value)
    {
        var sealedBytes = await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(value, Json));
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await ReplaceAsync(path, sealedBytes);
        }
        finally { gate.Release(); }
    }

    private static async Task ReplaceAsync(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
}
