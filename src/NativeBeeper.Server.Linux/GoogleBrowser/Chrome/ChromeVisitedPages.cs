using System.Text;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// Which pages Chrome has opened in a profile, read from the session files it writes for restoring its tabs (<c>Default/Sessions/Session_*</c>, <c>Default/Current Session</c> before Chrome 100),
/// never through the browser, which nothing may attach to. Each navigation is recorded there with its address as plain text, a page the policy refused included, a few seconds after it opened.
/// Only a match is answered: no address is read out, kept or logged.
/// </summary>
internal static class ChromeVisitedPages
{
    /// <summary>
    /// Google's identity and age checks. Google sent a Google Voice sign-in there (2026-11-11, an account with no Voice number yet: <c>/verification/selfie/precollection</c>, for Voice). The policy
    /// does not open it, and a selfie needs a camera the server has none of, so the person does it in their own browser before signing in here.
    /// </summary>
    public const string GoogleVerification = "https://myaccount.google.com/verification";
    public const string GoogleVerificationWord = "google_verification";

    // The session files are small (a sign-in's few pages); anything larger than this is not read.
    private const long MostRead = 16L * 1024 * 1024;

    /// <summary>Whether a page whose address starts with <paramref name="prefix"/> has been opened in the profile at <paramref name="profileFolder"/>.</summary>
    public static bool Opened(string profileFolder, string prefix)
    {
        var wanted = Encoding.ASCII.GetBytes(prefix);
        foreach (var file in SessionFiles(profileFolder))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MostRead) continue;
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                if (bytes.AsSpan().IndexOf(wanted) >= 0) return true;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    private static IEnumerable<string> SessionFiles(string profileFolder)
    {
        var profile = Path.Combine(profileFolder, "Default");
        var sessions = Path.Combine(profile, "Sessions");
        string[] found = [];
        try { if (Directory.Exists(sessions)) found = Directory.GetFiles(sessions, "Session_*"); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        foreach (var file in found) yield return file;
        var current = Path.Combine(profile, "Current Session");
        if (File.Exists(current)) yield return current;
    }
}
