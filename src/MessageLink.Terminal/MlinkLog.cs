using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace MessageLink.Terminal;

/// <summary>
/// mlink.log in mlink's own folder: how the program behaved, in fixed words, counts and one-way tags. Nothing here records a token, a conversation, a
/// person or anything typed. A run replaces the previous run's file (kept as mlink.previous.log), and the file stops growing at its cap. Lines are
/// written on a thread of their own, so the loop that draws the terminal never waits for the disk. Any number of mlinks run side by side
/// (MlinkContext): the first holds mlink.log, and one started while it runs keeps mlink.&lt;process id&gt;.log instead (the newest few, for a day).
/// </summary>
public sealed class MlinkLog : IDisposable
{
    public const string FileName = "mlink.log";
    public const string PreviousFileName = "mlink.previous.log";
    private const long Limit = 1 << 20;
    // No other writer while a run holds its file. A second run used to move it aside (Linux) or empty it and write over its lines, leaving runs of NULs
    // (Windows). Readers are still let in; on Linux the lock is an advisory one, which readers never ask for.
    private static readonly FileShare Held = OperatingSystem.IsWindows() ? FileShare.Read : FileShare.None;

    private readonly BlockingCollection<string> pending = new(4096);
    private readonly Thread writer;
    private readonly FileStream? file;

    // Logs of runs started beside another (mlink.<process id>.log) that are kept: the newest few, none older than a day.
    private const int RunLogsKept = 5;
    private static readonly TimeSpan RunLogAge = TimeSpan.FromDays(1);

    public MlinkLog(string folder)
    {
        file = Claim(folder);
        Sweep(folder);
        writer = new Thread(Drain) { IsBackground = true, Name = "mlink log" };
        writer.Start();
    }

    private static FileStream? Claim(string folder)
    {
        var path = Path.Combine(folder, FileName);
        var own = Path.Combine(folder, $"mlink.{Environment.ProcessId}.log");
        try
        {
            if (File.Exists(path))
            {
                // Held by a run still going: this one keeps a file of its own.
                try { using (new FileStream(path, FileMode.Open, FileAccess.Write, Held)) { } }
                catch (IOException) { return Open(own); }
                try { File.Move(path, Path.Combine(folder, PreviousFileName), true); }
                catch (IOException) { }
            }
            try { return Open(path); }
            catch (IOException) { return Open(own); }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        static FileStream Open(string at) => new(at, FileMode.Create, FileAccess.Write, Held);
    }

    // Each run started beside another left its own file for good. One a run still holds is never removed: it is opened the
    // way a run holds it first, which fails while that run goes on (Windows would refuse the delete as well, Linux would not).
    private static void Sweep(string folder)
    {
        try
        {
            var own = $"mlink.{Environment.ProcessId}.log";
            var now = DateTime.UtcNow;
            var runs = new DirectoryInfo(folder).EnumerateFiles("mlink.*.log")
                .Where(log => log.Name != own && log.Name.Length > "mlink..log".Length &&
                    int.TryParse(log.Name["mlink.".Length..^".log".Length], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                .OrderByDescending(log => log.LastWriteTimeUtc).ToArray();
            for (var index = 0; index < runs.Length; index++)
            {
                if (index < RunLogsKept && now - runs[index].LastWriteTimeUtc < RunLogAge) continue;
                try
                {
                    using (new FileStream(runs[index].FullName, FileMode.Open, FileAccess.Write, Held)) { }
                    runs[index].Delete();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Write(string line)
    {
        var stamped = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + " " + line;
        // A full queue drops the line rather than holding up the caller: this is a diagnostic, never worth a stall.
        try { pending.TryAdd(stamped); }
        catch (InvalidOperationException) { }
    }

    private void Drain()
    {
        long written = 0;
        try
        {
            if (file is null) throw new IOException("No log file could be opened.");
            using var text = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };
            foreach (var line in pending.GetConsumingEnumerable())
            {
                if (written > Limit) continue;
                text.WriteLine(line);
                written += line.Length + 1;
            }
        }
        // The log is never worth stopping the program for: it goes quiet instead.
        catch (Exception) { foreach (var _ in pending.GetConsumingEnumerable()) { } }
    }

    public void Dispose()
    {
        pending.CompleteAdding();
        writer.Join(TimeSpan.FromSeconds(2));
    }
}
