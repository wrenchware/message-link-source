using System.Collections.Concurrent;
using System.Text;

namespace NativeBeeper.App.Services;

// The application's own diagnostic log, beside each provider's: how the interface itself behaved, in durations and
// counts. Nothing here records a conversation, a person or anything typed. A run replaces the previous file, and at its
// cap the file is rolled in place (Roll): the run's first lines stay, the oldest of the rest go and the newest are kept,
// so the space it takes is bounded by the cap rather than by how long the app has been in use.
//
// Lines are queued and written on a background thread. This log exists to find work that holds up the interface
// thread, and a file append on that thread is exactly such work: an instrument that disturbs what it measures is
// worse than none.
internal static class AppLog
{
    private const long Limit = 1 << 20;
    // What a roll keeps: the run's start (launch steps, the first sync) and its latest lines. A log that simply stopped at
    // the cap went silent two days before a 61-hour run was read (2026-10-22), and hid the lines of a merged-read bug.
    // A roll comes every few hours of active use and costs one rewrite of under a megabyte on the drain's thread.
    internal const long HeadBytes = 128 << 10, TailBytes = 512 << 10;
    // The caps below matter only once a roll has failed (the file could not be rewritten): the log then stops as it did
    // before rolling. A diagnostic waiting for something rare (the history layout watch) keeps a small reserve past the
    // cap, since the periodic responsiveness lines fill a megabyte in under two hours of active use.
    private const long ReservedLimit = Limit + (256 << 10);
    // The record of a crash gets a reserve of its own past both. A crash in the long sessions where the paste crash was
    // seen would otherwise find the file already full and leave nothing at all.
    private const long CrashLimit = ReservedLimit + (64 << 10);
    private static readonly ConcurrentQueue<(string Text, long Cap)> Pending = new();
    private static readonly object Gate = new();
    private static int draining;
    private static string? path;
    private static long written;
    // Where the kept start ends, fixed by the first roll so every later one keeps the same lines; rolling is off once one failed.
    private static long head = -1;
    private static bool rolling = true;

    private static string fileName = "app.log", openingLine = "app started";

    // A process that is not the window (MessagesServer.exe) keeps its own file, chosen before its first line.
    public static void UseFile(string name, string opening) { fileName = name; openingLine = opening; }

    public static string FilePath => AppPaths.Combine(fileName);

    // Also given every line, timestamped and as written, on the drain's thread: the Linux server's standard output, which is
    // where a container's log is read. Set before the first line.
    public static Action<string>? Mirror { get; set; }

    private static string? Path
    {
        get
        {
            if (path is not null) return path.Length == 0 ? null : path;
            try
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                path = FilePath;
                LogRotation.StartFresh(path, openingLine);
                written = 0;
                head = -1;
                rolling = true;
            }
            catch (Exception) { path = ""; return null; }
            return path;
        }
    }

    public static void Write(string line) => Write(line, reserved: false);

    public static void Write(string line, bool reserved) => Enqueue(line, reserved ? ReservedLimit : Limit);

    // For a process about to end: queued with its own reserve and written before this returns, on the calling thread.
    // WinUI ends the process the moment an unhandled exception's handler returns, and a line left to the background
    // drain was, in all likelihood, never written.
    public static void WriteNow(string line)
    {
        Enqueue(line, CrashLimit);
        Flush();
    }

    // Writes whatever is queued, now, on the calling thread: the last lines of a run, which the process ending would
    // otherwise take with it. The same loop and lock as the background drain, so order and caps are unchanged.
    public static void Flush()
    {
        try { DrainPending(); } catch (Exception) { }
    }

    private static void Enqueue(string line, long cap)
    {
        if (written > cap || !Diagnostics.Recording) return;
        Pending.Enqueue(($"{DateTimeOffset.Now:HH:mm:ss.fff} {line}\n", cap));
        if (Interlocked.CompareExchange(ref draining, 1, 0) == 0) _ = Task.Run(Drain);
    }

    private static void Drain()
    {
        do
        {
            DrainPending();
            Interlocked.Exchange(ref draining, 0);
            // A line queued between the last dequeue and releasing the flag would otherwise wait for the next write.
        } while (!Pending.IsEmpty && Interlocked.CompareExchange(ref draining, 1, 0) == 0);
    }

    private static void DrainPending()
    {
        lock (Gate)
        {
            while (Pending.TryDequeue(out var entry))
            {
                if (Mirror is { } mirror) try { mirror(entry.Text); } catch (Exception) { }
                if (Path is not { } file) continue;
                // A crash's line is never held up by a rewrite: it has its own reserve.
                if (rolling && entry.Cap != CrashLimit && written + entry.Text.Length > Limit)
                {
                    try { written = Roll(file, ref head, HeadBytes, TailBytes); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { rolling = false; }
                }
                if (written > entry.Cap) continue;
                try { File.AppendAllText(file, entry.Text, Encoding.UTF8); written += entry.Text.Length; }
                catch (Exception) { path = ""; }
            }
        }
    }

    // Rewrites the file as its first lines (up to headBytes, ending at a line break, and the same lines on every later
    // roll), one line saying it rolled, and its last lines (up to tailBytes, starting at a line). Returns the new length.
    // Shared like every reader here: the viewer and the console's /logs may have it open, and both start over at a file
    // shorter than where they last read (DiagnosticLogs.ReadFromAsync), as the server's log copies do (ServerLogCopies).
    internal static long Roll(string file, ref long head, long headBytes, long tailBytes)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        if (head < 0 || head > bytes.Length)
            head = bytes.Length == 0 ? 0 : Array.LastIndexOf(bytes, (byte)'\n', (int)Math.Min(headBytes, bytes.Length) - 1) + 1;
        var from = Math.Max(head, bytes.Length - tailBytes);
        // Mid-file lands mid-line; the kept part starts after the next line break. A newline byte never occurs inside a
        // UTF-8 sequence, so this never splits a character.
        if (from > head && bytes[from - 1] != (byte)'\n')
            from = Array.IndexOf(bytes, (byte)'\n', (int)from) is var next and >= 0 ? next + 1 : bytes.Length;
        var marker = Encoding.UTF8.GetBytes(
            $"{DateTimeOffset.Now:HH:mm:ss.fff} log rolled kept_kb={(head + bytes.Length - from) / 1024} dropped_kb={(from - head) / 1024}\n");
        stream.Position = head;
        stream.Write(marker);
        stream.Write(bytes, (int)from, bytes.Length - (int)from);
        stream.SetLength(stream.Position);
        return stream.Length;
    }
}
