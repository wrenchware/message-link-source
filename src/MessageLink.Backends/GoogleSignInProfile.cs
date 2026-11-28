using MessageLink.App.Services;

namespace MessageLink.App;

/// <summary>
/// The one-time result of a completed Google browser sign-in, for Voice or Messages. Disposing an untransferred result clears its cookies
/// and removes its persistent WebView2 profile. Call <see cref="TransferProfileOwnership"/> only after the account layer
/// has successfully accepted <see cref="Cookies"/>.
/// </summary>
internal sealed class GoogleSignInResult : IAsyncDisposable
{
    private readonly GoogleSignInProfile profile;
    private int disposed;
    private int ownershipTransferred;

    internal GoogleSignInResult(Dictionary<string, string> cookies, GoogleSignInProfile profile)
    {
        Cookies = cookies;
        this.profile = profile;
    }

    public Dictionary<string, string> Cookies { get; }

    // Every other Google cookie the sign-in browser held, collected only when the sign-in is for a server (GoogleSignInWindow); null otherwise, always.
    public IReadOnlyList<MessageLink.Core.Services.GoogleProfileCookie>? ProfileCookies { get; internal set; }

    // This is an opaque attempt identifier, not a filesystem path or an account identifier.
    public string ProfileId => profile.Id;

    public void TransferProfileOwnership()
    {
        if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException(nameof(GoogleSignInResult));
        Interlocked.Exchange(ref ownershipTransferred, 1);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Cookies.Clear();
        ProfileCookies = null;
        if (Volatile.Read(ref ownershipTransferred) == 0) await profile.DeleteAsync();
    }
}

// Persistent profiles are strictly contained under one service's data (Google Voice's, or since 0.1.290 Google Messages',
// whose sign-in was a throwaway InPrivate window until Google began ending its copied sessions after about two hours).
// Profile ids are opaque GUIDs and are validated again before cleanup so a malformed id can never make deletion escape
// this directory.
internal sealed class GoogleSignInProfile
{
    public const string VoiceService = "GoogleVoice", MessagesService = "GoogleMessages";
    private const string ProfilesDirectory = "WebViewProfiles";
    private readonly string path;
    private readonly string service;

    private GoogleSignInProfile(string id, string path, string service)
    {
        Id = id;
        this.path = path;
        this.service = service;
    }

    public string Id { get; }
    public string Path => path;

    public static GoogleSignInProfile CreateAttempt(string service = VoiceService)
    {
        var root = Root(service);
        Private(root);
        while (true)
        {
            var id = Guid.NewGuid().ToString("N");
            if (!TryGetPath(id, service, out var profilePath)) throw new InvalidOperationException("invalid_profile_path");
            if (Directory.Exists(profilePath)) continue;
            try
            {
                Private(profilePath);
                return new GoogleSignInProfile(id, profilePath, service);
            }
            catch (IOException) when (Directory.Exists(profilePath))
            {
                // An astronomically unlikely id collision is safe to retry; do not reuse someone else's profile.
            }
        }
    }

    // Off Windows a profile is its owner's alone whatever the process's mask (a browser profile holds the account's cookies); on Windows the
    // folder inherits the data folder's access, as it always has.
    private static void Private(string folder)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // Recovery and sign-out callers have only the opaque id saved with their account. Never turn an invalid id into a
    // path, and never operate on a junction substituted for an owned profile.
    internal static bool TryOpen(string id, out GoogleSignInProfile? profile, string service = VoiceService)
    {
        profile = null;
        if (!TryGetPath(id, service, out var profilePath)) return false;
        try
        {
            if (!Directory.Exists(profilePath) || (File.GetAttributes(profilePath) & FileAttributes.ReparsePoint) != 0) return false;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        profile = new GoogleSignInProfile(id, profilePath, service);
        return true;
    }

    // The names of everything in one service's profiles folder, for a host that removes the profiles nothing names any more. Names only; nothing
    // here opens or follows them.
    internal static IReadOnlyList<string> Names(string service)
    {
        var root = Root(service);
        try { return Directory.Exists(root) ? [.. Directory.EnumerateFileSystemEntries(root).Select(entry => System.IO.Path.GetFileName(entry))] : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    internal static Task DeleteAsync(string id, string service = VoiceService) => TryOpen(id, out var profile, service) && profile is not null
        ? profile.DeleteAsync()
        : Task.CompletedTask;

    // Between tries; a check sets it rather than sitting through ten of them.
    internal static TimeSpan DeleteRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public async Task DeleteAsync()
    {
        if (!TryGetPath(Id, service, out var profilePath) || !string.Equals(profilePath, path, StringComparison.OrdinalIgnoreCase)) return;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(profilePath)) return;
                // Do not follow or remove a directory junction substituted at the attempt path.
                if ((File.GetAttributes(profilePath) & FileAttributes.ReparsePoint) != 0) return;
                Directory.Delete(profilePath, true);
                return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(DeleteRetryDelay);
        }
        // Still there after every try (a browser that has not let go of it, a file another program holds): a folder that holds
        // a whole Google sign-in is not left without a word. Fixed words only, never its path or id.
        AppLog.Write($"google profile delete outcome=left service={(service == MessagesService ? "messages" : "voice")}");
    }

    // Where a host keeps the profiles instead of beside the data, chosen once before anything opens one. The Linux server keeps them with its
    // keys (SWITCHBOARD.md 4.3): a profile is a working Google sign-in, unsealed, so it belongs on the volume that holds the secrets, not on the
    // data folder whose contents are useless without them.
    private static string? profilesRoot;

    public static void UseRoot(string folder)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(folder));
        if (Interlocked.CompareExchange(ref profilesRoot, full, null) is { } chosen && chosen != full)
            throw new InvalidOperationException("The profiles folder was already chosen.");
    }

    // Only the two known services name a directory, so no caller can steer a profile outside the app's data.
    private static string Root(string service) => service is VoiceService or MessagesService
        ? System.IO.Path.GetFullPath(Volatile.Read(ref profilesRoot) is { } chosen ? System.IO.Path.Combine(chosen, service) : AppPaths.Combine(service, ProfilesDirectory))
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
        : throw new InvalidOperationException("unknown_profile_service");

    // Whether a folder is one of the service's profiles: the Linux server closes its one Chrome before a Messages profile goes only when Chrome runs on one of them,
    // never under a Google Voice sign-in or renewal that holds it (code review 2026-11-22).
    internal static bool IsUnder(string folder, string service)
    {
        var root = Root(service) + System.IO.Path.DirectorySeparatorChar;
        return System.IO.Path.GetFullPath(folder).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool TryGetPath(string id, string service, out string profilePath)
    {
        profilePath = "";
        if (!Guid.TryParseExact(id, "N", out var guid) || !string.Equals(id, guid.ToString("N"), StringComparison.Ordinal)) return false;
        var root = Root(service);
        var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, id));
        if (!string.Equals(System.IO.Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(System.IO.Path.GetFileName(candidate), id, StringComparison.Ordinal)) return false;
        profilePath = candidate;
        return true;
    }
}
