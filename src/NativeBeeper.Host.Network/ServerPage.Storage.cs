using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace NativeBeeper.Host.Network;

// The home page's Storage card: what the server keeps on disk, by kind. The sizes come from ServerStorage's last measurement, never one made for the request.
internal sealed partial class ServerPage
{
    private ServerStorage? storage;

    /// <summary>Where the server keeps its data and its keys (the Linux host keeps Messenger's history and the browser profiles with the keys); set once by the host.</summary>
    internal void UseStorage(params string[] folders) => storage = new ServerStorage(folders, time, log);

    private void StorageCard(StringBuilder body)
    {
        if (storage is null) return;
        body.Append("<section id=\"storage\"><h2>Storage</h2>");
        if (storage.Current() is not { } sizes) body.Append("<p class=\"quiet\">Measuring…</p>");
        else
        {
            body.Append("<ul class=\"rows\">");
            foreach (var (name, bytes) in sizes.Kinds)
                body.Append("<li><span class=\"grow\">").Append(Html(name)).Append("</span><span class=\"state\">").Append(Html(ServerStorage.Size(bytes))).Append("</span></li>");
            body.Append("<li><span class=\"grow\"><strong>Total</strong></span><span class=\"state\"><strong>").Append(Html(ServerStorage.Size(sizes.Total))).Append("</strong></span></li></ul>");
        }
        body.Append("</section>");
    }
}

/// <summary>What the server keeps in its data and keys folders, by kind (the host cannot see the Windows app's StorageUsage, so its rules are repeated here).</summary>
internal sealed class ServerStorage(IReadOnlyList<string> folders, TimeProvider time, Action<string> log)
{
    /// <summary>How long a measurement is shown before another is made in the background.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    internal sealed record Sizes(IReadOnlyList<(string Name, long Bytes)> Kinds, long Total);

    private static readonly (string Key, string Name)[] Order =
    [
        ("history", "Conversations"),
        ("state", "Settings and drafts"),
        ("archive", "Google Messages search archive"),
        ("messenger", "Messenger history"),
        ("media", "Pictures and videos"),
        ("saved", "Saved photos"),
        ("profiles", "Sign-in browser profiles"),
        ("logs", "Logs"),
        ("other", "Everything else"),
    ];

    private readonly object gate = new();
    private Sizes? last;
    private DateTimeOffset measuredAt;
    private bool running;

    /// <summary>The last measurement, or null before the first. Starts another on a background task when it is older than <see cref="Fresh"/> (or absent) and none is running.</summary>
    public Sizes? Current()
    {
        lock (gate)
        {
            if (!running && (last is null || time.GetUtcNow() - measuredAt >= Fresh))
            {
                running = true;
                _ = Task.Run(Run);
            }
            return last;
        }
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        Sizes? sizes = null;
        try { sizes = Measure(folders); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        finally
        {
            lock (gate)
            {
                running = false;
                measuredAt = time.GetUtcNow();
                // A measurement that failed outright shows as an empty folder, so the card never stays on "Measuring…".
                last = sizes ?? new Sizes([], 0);
            }
        }
        log($"server storage measured total_kb={(sizes?.Total ?? 0) / 1024} ms={clock.ElapsedMilliseconds}");
    }

    internal static Sizes Measure(IReadOnlyList<string> folders)
    {
        var bytes = Order.ToDictionary(kind => kind.Key, _ => 0L, StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var folder in folders)
        {
            if (string.IsNullOrEmpty(folder)) continue;
            try { roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))); }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException or IOException) { }
        }
        // A folder that is, or lies inside, another one given is counted once.
        var distinct = roots.Where((root, index) => !roots.Where((other, at) => at != index && (Same(other, root) ? at < index : Inside(other, root))).Any()).ToArray();
        // A link is never followed: what it points at is not kept here.
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, ReturnSpecialDirectories = false };
        foreach (var root in distinct)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    long length;
                    try { length = file.Length; }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { continue; }
                    bytes[Classify(Path.GetRelativePath(root, file.FullName))] += length;
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { /* What was listed before the folder went away still counts. */ }
        }
        return new(Order.Where(kind => bytes[kind.Key] > 0).Select(kind => (kind.Name, bytes[kind.Key])).ToArray(), bytes.Values.Sum());
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string parent, string child) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Which kind a file is, by its path relative to the folder it was found in.</summary>
    internal static string Classify(string relative)
    {
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "other";
        var name = parts[^1];
        // The browser profiles: the Linux host's Google and SignIn folders in its keys, the Windows server's WebView2 profiles, and the file naming Google Messages' current one.
        if (parts.Length >= 2 && (Is(parts[0], "Google") || Is(parts[0], "SignIn")) || parts.Skip(1).Any(part => Is(part, "WebViewProfiles")) || Is(name, "browser-profile")) return "profiles";
        if (IsDatabase(name, "history.db")) return "history";
        if (IsDatabase(name, "state.db") || IsDatabase(name, "shared.db")) return "state";
        if (IsDatabase(name, "archive.db")) return "archive";
        if (IsDatabase(name, "messages.db")) return "messenger";
        if (IsDatabase(name, "media.db") || IsDatabase(name, "video-stills.db")) return "media";
        if (IsDatabase(name, "saved-photos.db")) return "saved";
        if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".log.previous", StringComparison.OrdinalIgnoreCase)) return "logs";
        return "other";
    }

    // The database and what SQLite keeps beside it under its name while it works: -journal, -wal and -shm.
    private static bool IsDatabase(string name, string database) => name.StartsWith(database, StringComparison.OrdinalIgnoreCase);

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A size in plain units: KB under a megabyte (never less than 1), then MB and GB with one decimal.</summary>
    internal static string Size(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        var invariant = CultureInfo.InvariantCulture;
        return bytes >= Gb ? (bytes / Gb).ToString("0.0", invariant) + " GB"
            : bytes >= Mb ? (bytes / Mb).ToString("0.0", invariant) + " MB"
            : Math.Max(1, (long)Math.Ceiling(bytes / Kb)).ToString(invariant) + " KB";
    }
}
