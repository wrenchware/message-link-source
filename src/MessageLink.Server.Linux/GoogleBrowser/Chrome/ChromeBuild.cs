using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MessageLink.Server.Linux;

/// <summary>
/// Which Chrome this is and how old (SWITCHBOARD.md 5.4, Chrome on the server): the image is meant to always take the newest stable Chrome when it is built, and the server to say when it has been
/// left running on one for too long. The version is what <c>google-chrome --version</c> prints. The age counts from when the image was built (<c>MESSAGES_CHROME_BUILT</c>, written by the Dockerfile from the
/// build argument <c>CHROME_REFRESH</c> that <c>install-unraid.sh</c> sets to the build time, as <c>yyyyMMddTHHmmssZ</c>); an image built by hand without it falls back to the date of Chrome's own program file,
/// which is when Google built it (older than the image, so it flags a stale Chrome a little early rather than late).
/// </summary>
internal sealed record ChromeBuild(string Version, DateTimeOffset? BuiltAt, string Source)
{
    /// <summary>More than this many days and the client is told the server's Chrome is out of date.</summary>
    public const int OutdatedAfterDays = 21;

    public int? AgeDays(DateTimeOffset now) => BuiltAt is { } built ? Math.Max(0, (int)Math.Floor((now - built).TotalDays)) : null;

    public bool OutOfDate(DateTimeOffset now) => AgeDays(now) > OutdatedAfterDays;

    /// <summary>The one log line at start.</summary>
    public string Line(DateTimeOffset now) => $"server google chrome version={Version} age_days={AgeDays(now)?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} source={Source}";

    private static readonly Regex VersionPattern = new(@"(\d+(?:\.\d+){1,3})", RegexOptions.CultureInvariant);

    /// <summary>The version in <c>Google Chrome 154.0.8037.99 </c>, or <c>unknown</c>.</summary>
    public static string ParseVersion(string? output) => output is not null && VersionPattern.Match(output) is { Success: true } match ? match.Value : "unknown";

    /// <summary>The time in <c>20261010T184500Z</c>, or null for anything else.</summary>
    public static DateTimeOffset? ParseBuilt(string? text) =>
        DateTimeOffset.TryParseExact(text?.Trim(), "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    /// <summary>Reads both from the installed Chrome. Never throws; what cannot be read is <c>unknown</c> and a null date.</summary>
    public static async Task<ChromeBuild> ReadAsync(string executable, string? built, Func<string, string?> programFile, CancellationToken cancellationToken)
    {
        var version = ParseVersion(await RunAsync(executable, cancellationToken).ConfigureAwait(false));
        if (ParseBuilt(built) is { } image) return new(version, image, "image");
        if (programFile(executable) is { } path)
        {
            try { return new(version, new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), "file"); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
        return new(version, null, "none");
    }

    /// <summary>Chrome's own program file: its real path (the installed name is a link), or the folder's <c>chrome</c> beside the launcher script.</summary>
    public static string? ProgramFile(string executable)
    {
        try
        {
            var real = File.ResolveLinkTarget(executable, true)?.FullName ?? executable;
            var beside = Path.Combine(Path.GetDirectoryName(real) ?? "", "chrome");
            return File.Exists(beside) ? beside : File.Exists(real) ? real : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<string?> RunAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(executable, "--version") { RedirectStandardOutput = true, RedirectStandardError = false, UseShellExecute = false };
            start.Environment.Clear();
            start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
            start.Environment["HOME"] = Path.GetTempPath();
            using var process = Process.Start(start);
            if (process is null) return null;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var text = await process.StandardOutput.ReadToEndAsync(bounded.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
                return text;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (Exception) { }
                return null;
            }
        }
        catch (Exception failure) when (failure is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return null; }
    }
}
