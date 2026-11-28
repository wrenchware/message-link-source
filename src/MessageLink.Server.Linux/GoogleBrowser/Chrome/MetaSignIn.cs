using MessageLink.App.Services.Messenger;
using MessageLink.Instagram.Spike;
using MessageLink.Protocol;

namespace MessageLink.Server.Linux;

/// <summary>
/// What a Messenger or Instagram sign-in in the server's Chrome opens and keeps: the same login page and the same cookie rules as the PC's own sign-in window
/// (<see cref="MessengerSignInRules"/>, <see cref="InstagramSignInRules"/>), read from Chrome's cookie file by <see cref="ChromeCookieStore"/> instead of from the window's cookie manager.
/// <para>
/// The hosts read are the ones the PC's window asks its cookie manager for: Messenger's cookies from <c>facebook.com</c> (the window asks for <c>https://www.facebook.com/</c>, and its rules take only
/// the domain's own cookies), Instagram's first-party cookies from <c>instagram.com</c> and <c>www.instagram.com</c>.
/// </para>
/// </summary>
internal sealed record MetaSignInTarget(string Service, string LoginUrl, ChromiumRenewalTarget Cookies, string[] Required)
{
    public static readonly MetaSignInTarget Messenger = new(RemoteSignInServices.Messenger, MessengerSignInRules.LoginUrl,
        new ChromiumRenewalTarget(RemoteSignInServices.Messenger, MessengerSignInRules.LoginUrl, MessengerSignInRules.IsLoginNavigation, _ => false, [MessengerSignInRules.CookieUrl],
            MessengerSignInRules.ReadCookie, cookies => MessengerSignInRules.RequiredCookies.All(cookies.ContainsKey),
            name => MessengerSignInRules.CookieNames.Contains(name, StringComparer.Ordinal) ? "facebook.com" : null, "")
        { StoreHosts = ["facebook.com"] },
        MessengerSignInRules.RequiredCookies);

    public static readonly MetaSignInTarget Instagram = new(RemoteSignInServices.Instagram, InstagramSignInRules.LoginUrl,
        new ChromiumRenewalTarget(RemoteSignInServices.Instagram, InstagramSignInRules.LoginUrl, InstagramSignInRules.IsLoginNavigation, _ => false, [InstagramSignInRules.CookieUrl],
            InstagramSignInRules.ReadCookie, cookies => InstagramSignInRules.RequiredCookies.All(cookies.ContainsKey),
            // Every name is considered; InstagramSignInRules.ReadCookie decides which it keeps.
            _ => "instagram.com", "")
        { StoreHosts = ["instagram.com", "www.instagram.com"] },
        InstagramSignInRules.RequiredCookies);

    /// <summary>The target of a service word, or null for a service this sign-in does not serve.</summary>
    public static MetaSignInTarget? For(string? service) => service switch
    {
        RemoteSignInServices.Messenger => Messenger,
        RemoteSignInServices.Instagram => Instagram,
        _ => null,
    };
}

/// <summary>
/// A Chrome profile made for one Messenger or Instagram sign-in and deleted when it ends, like the PC's InPrivate window. The folders sit beside the Google profiles on the keys
/// volume (<c>&lt;keys&gt;/SignIn/&lt;id&gt;</c>), private to the server's account, since for the minutes one exists it holds a whole sign-in. Ids are opaque GUIDs, checked again before a delete, and a
/// link is never followed. Whatever a stopped server left is removed at the next start (<see cref="Sweep"/>): nothing in this folder is ever kept.
/// </summary>
internal sealed class ThrowawayChromeProfile
{
    public const string FolderName = "SignIn";

    private ThrowawayChromeProfile(string root, string id)
    {
        Id = id;
        Path = System.IO.Path.Combine(root, id);
    }

    public string Id { get; }
    public string Path { get; }

    /// <summary>Retries of a delete Chrome still holds files in; the harness shortens the wait between them.</summary>
    internal static TimeSpan DeleteRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public static ThrowawayChromeProfile Create(string root)
    {
        root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        Private(root);
        while (true)
        {
            var profile = new ThrowawayChromeProfile(root, Guid.NewGuid().ToString("N"));
            if (Directory.Exists(profile.Path)) continue;
            Private(profile.Path);
            return profile;
        }
    }

    private static void Private(string folder)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else
        {
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Chrome's own preferences for the new profile, written before it starts: no saved passwords or autofill (the policy file says the same for every profile), and session cookies kept on disk as a
    /// browser that restores its last session keeps them (<c>session.restore_on_startup</c> 1), since a service may set a sign-in cookie for the session only and the file is all that is read.
    /// Nothing here changes what the browser says it is.
    /// </summary>
    public static void PreparePreferences(string profileFolder)
    {
        var folder = System.IO.Path.Combine(profileFolder, "Default");
        Private(folder);
        File.WriteAllText(System.IO.Path.Combine(folder, "Preferences"), PreferencesJson);
    }

    internal const string PreferencesJson = "{\"credentials_enable_service\":false,\"credentials_enable_autosignin\":false,\"profile\":{\"password_manager_enabled\":false},"
        + "\"autofill\":{\"profile_enabled\":false,\"credit_card_enabled\":false},\"session\":{\"restore_on_startup\":1}}";

    /// <summary>Deletes the profile and the temporary folder Chrome kept beside it (its shared memory, which held the pages it drew). A delete that cannot finish is said in fixed words.</summary>
    public async Task DeleteAsync(Action<string> log)
    {
        var whole = Delete(Path) & Delete(ChromeLauncher.TemporaryFolder(Path));
        for (var attempt = 0; !whole && attempt < 10; attempt++)
        {
            await Task.Delay(DeleteRetryDelay).ConfigureAwait(false);
            whole = Delete(Path) & Delete(ChromeLauncher.TemporaryFolder(Path));
        }
        if (!whole) log("server sign-in profile delete outcome=left");
    }

    // True when the folder is gone (or was never there). Only a real folder named by an opaque id under the root is deleted; a link is left alone.
    private static bool Delete(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return true;
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return true;
            Directory.Delete(folder, true);
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>At start: every profile a stopped server left in <paramref name="root"/> is deleted (none is ever kept). Counts only.</summary>
    public static void Sweep(string root, Action<string> log)
    {
        int removed = 0, left = 0;
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var entry in Directory.EnumerateDirectories(root))
            {
                var name = System.IO.Path.GetFileName(entry);
                if (!Guid.TryParseExact(name, "N", out var id) || id.ToString("N") != name) continue;
                var profile = new ThrowawayChromeProfile(root, name);
                if (Delete(profile.Path) && Delete(ChromeLauncher.TemporaryFolder(profile.Path))) removed++;
                else left++;
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        if (removed + left > 0) log($"server sign-in profiles removed={removed} left={left}");
    }
}
