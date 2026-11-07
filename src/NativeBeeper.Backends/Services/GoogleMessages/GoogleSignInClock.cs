using System.Globalization;

namespace NativeBeeper.App.Services.GoogleMessages;

// When Google last made this computer's Google-account sign-in: a browser sign-in, or a refresh from the kept profile in
// which Google issued new cookies. Google refuses every sign-in this app has made about two hours after that — Google
// Messages and Google Voice alike, running or closed, eleven times in a row on 2026-09-24/25 — and the RotateCookies call
// the helper made until 0.1.307 never moved it. Knowing the age lets a launch refresh first instead of connecting with
// cookies Google is about to refuse, and dates every refusal in the log. Only a time is kept, never anything of the
// sign-in; a missing or unreadable file means unknown, which changes nothing.
internal sealed class GoogleAccountClock(string dataRoot)
{
    // Past this, a connect is presumed to be refused and the sign-in is refreshed before it; Google has refused at 2h00m.
    public readonly TimeSpan Stale = TimeSpan.FromMinutes(100);
    private readonly object Sync = new();
    private bool loaded;
    private DateTimeOffset? made;

    private string FilePath => Path.Combine(dataRoot, "GoogleMessages", "sign-in-made");

    public DateTimeOffset? MadeAt
    {
        get
        {
            lock (Sync)
            {
                if (loaded) return made;
                loaded = true;
                try { made = Parse(File.ReadAllText(FilePath), DateTimeOffset.UtcNow); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { made = null; }
                return made;
            }
        }
    }

    // The file's one line. A time ahead of the clock is a clock that moved, not a sign-in from the future.
    internal DateTimeOffset? Parse(string text, DateTimeOffset now) =>
        DateTimeOffset.TryParseExact(text.Trim(), "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) &&
        at <= now.AddMinutes(5) ? at : null;

    // Whole minutes since the sign-in was made, or null when that is not known.
    public int? AgeMinutes(DateTimeOffset now) => MadeAt is { } at && now >= at ? (int)(now - at).TotalMinutes : null;

    // A pairing with no Google-account sign-in (a QR pairing) has no age: the time of the sign-in it replaced would make the keeper look for an expiry that
    // cannot come. Known as nothing, which changes nothing anywhere it is read.
    public Task ClearAsync()
    {
        lock (Sync) { made = null; loaded = true; }
        try { File.Delete(FilePath); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { GoogleMessagesLog.Write("sign_in clock=not_cleared"); }
        return Task.CompletedTask;
    }

    public async Task RecordAsync(DateTimeOffset at)
    {
        lock (Sync) { made = at; loaded = true; }
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await NativeBeeper.Storage.DurableFile.WriteAsync(temporary, System.Text.Encoding.UTF8.GetBytes(at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
            File.Move(temporary, FilePath, true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // The time stays known for this run; only the next launch loses it, and unknown changes nothing.
            GoogleMessagesLog.Write("sign_in clock=not_saved");
        }
        finally { try { File.Delete(temporary); } catch (Exception) { } }
    }
}

// Whether Google issued a new sign-in, read from the Google-account cookies alone. The short-lived SIDCC family changes on
// every answer and the Messages host's OSID with every page, so neither says anything about the sign-in's age.
internal sealed class GoogleAccountCookies
{
    private readonly string[] Names = ["SID", "HSID", "SSID", "APISID", "SAPISID", "__Secure-1PSID", "__Secure-3PSID", "__Secure-1PSIDTS", "__Secure-3PSIDTS"];
    private readonly object Sync = new();
    // One-way digests of what the browser last handed over, this run. The saved session is the helper's copy, which the
    // library changes from Google's answers without the browser having been given anything, so the browser is compared
    // with itself when it can be.
    private Dictionary<string, string>? handed;

    public void Remember(IReadOnlyDictionary<string, string> cookies)
    {
        var digests = Names.Where(name => cookies.TryGetValue(name, out var value) && value.Length > 0).ToDictionary(name => name, name => Digest(cookies[name]));
        lock (Sync) handed = digests;
    }

    // Test seam: forget what this run was handed.
    internal void Forget() { lock (Sync) handed = null; }

    // Which of them differ from what the browser last handed over, or, before it has handed anything this run, from the
    // saved session: names only, compared in memory, never logged or kept by value.
    public string[] Changed(System.Text.Json.JsonElement session, IReadOnlyDictionary<string, string> cookies)
    {
        Dictionary<string, string>? last;
        lock (Sync) last = handed;
        if (last is not null)
            return Names.Where(name => cookies.TryGetValue(name, out var fresh) && fresh.Length > 0 &&
                (!last.TryGetValue(name, out var before) || before != Digest(fresh))).ToArray();
        var saved = session.ValueKind == System.Text.Json.JsonValueKind.Object && session.TryGetProperty("cookies", out var map) &&
            map.ValueKind == System.Text.Json.JsonValueKind.Object ? map : default;
        string? Saved(string name) => saved.ValueKind == System.Text.Json.JsonValueKind.Object && saved.TryGetProperty(name, out var value) &&
            value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() : null;
        return Names.Where(name => cookies.TryGetValue(name, out var fresh) && fresh.Length > 0 && !string.Equals(Saved(name), fresh, StringComparison.Ordinal)).ToArray();
    }

    private string Digest(string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}

internal static class GoogleSignInClock
{
    internal static GoogleAccountClock Default { get; } = new(AppPaths.DataRoot);
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(100);
    public static DateTimeOffset? MadeAt => Default.MadeAt;
    internal static DateTimeOffset? Parse(string text, DateTimeOffset now) => Default.Parse(text, now);
    public static int? AgeMinutes(DateTimeOffset now) => Default.AgeMinutes(now);
    public static Task ClearAsync() => Default.ClearAsync();
    public static Task RecordAsync(DateTimeOffset at) => Default.RecordAsync(at);
}
internal static class GoogleSignInCookies
{
    internal static GoogleAccountCookies Default { get; } = new();
    public static void Remember(IReadOnlyDictionary<string, string> cookies) => Default.Remember(cookies);
    internal static void Forget() => Default.Forget();
    public static string[] Changed(System.Text.Json.JsonElement session, IReadOnlyDictionary<string, string> cookies) => Default.Changed(session, cookies);
}
