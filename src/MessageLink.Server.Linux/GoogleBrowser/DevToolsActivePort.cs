using System.Text.RegularExpressions;

namespace MessageLink.Server.Linux;

/// <summary>
/// The file a browser started with <c>--remote-debugging-port=0</c> writes into its profile once DevTools is listening: the port it chose on the first line,
/// the browser's WebSocket path (<c>/devtools/browser/&lt;id&gt;</c>, a random id) on the second. The profile is new for the run, so a file there is this browser's own. Read here and used
/// to connect to 127.0.0.1 only; neither is logged. The path is not protection: <c>GET /json/version</c> on the port gives it to any process in the same network namespace, and the
/// browser only refuses clients that send an Origin. What keeps the port from others is the namespace itself (<see cref="HostNetwork"/>) and the session being short.
/// </summary>
internal static partial class DevToolsActivePort
{
    public const string FileName = "DevToolsActivePort";

    [GeneratedRegex("^/devtools/browser/[0-9A-Za-z-]{1,64}$")]
    private static partial Regex BrowserPath();

    /// <summary>The port and path in the file's text, or null while it is not all there yet or is not what a browser writes.</summary>
    public static (int Port, string Path)? Parse(string? text)
    {
        if (text is null) return null;
        var lines = text.Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length < 2) return null;
        if (!int.TryParse(lines[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535) return null;
        return BrowserPath().IsMatch(lines[1]) ? (port, lines[1]) : null;
    }

    /// <summary>
    /// Waits for the file in <paramref name="profileFolder"/> to hold a port and a path. Stops at once when the browser has exited (<paramref name="exited"/>), at the budget, or when
    /// cancelled: all three are failures, as a browser that never offers DevTools is no browser to sign in with.
    /// </summary>
    public static async Task<(int Port, string Path)> WaitAsync(string profileFolder, Task exited, TimeSpan budget, CancellationToken cancellationToken)
    {
        var file = Path.Combine(profileFolder, FileName);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Read(file) is { } found) return found;
            if (exited.IsCompleted) throw new IOException("browser_exited");
            if (clock.Elapsed >= budget) throw new TimeoutException("browser_slow");
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static (int Port, string Path)? Read(string file)
    {
        try { return File.Exists(file) ? Parse(File.ReadAllText(file)) : null; }
        // Being written, or not yet readable: the next look tries again.
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return null; }
    }
}
