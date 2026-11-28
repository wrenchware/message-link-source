using System.Security.Cryptography;
using System.Text;

namespace MessageLink.App.Services.Instagram;

// The app's Instagram diagnostic log, beside the other services' own: how the saved session was restored, what the
// realtime connection said, what the poll asked of Instagram, and how each helper ended and was resumed. Counts, fixed
// words and the helper's fixed codes only: no names, usernames, message text, cookies or raw identifiers. A conversation
// appears as a short one-way tag, enough to follow it down the file and nothing else. Instagram's helper writes no log
// of its own, so this is the whole account of it.
//
// These lines used to go to the application's own log among the interface's, where a day of responsiveness samples
// pushed the one line that explained a disconnect off the end, and where they could only be found by prefix.
internal static class InstagramLog
{
    private const long Limit = 1 << 20;
    // The lines that say what happened to the connection keep a reserve past the cap, the application log's pattern:
    // a run long enough to fill it with history and receipt counts is exactly the run whose helper stop, account check
    // or failed restore someone will come looking for.
    private const long ReservedLimit = Limit + (256 << 10);
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, long Cap)> Pending = new();
    private static readonly object Gate = new();
    private static int draining;
    private static string? path;
    private static long written;

    public static string FilePath => AppPaths.Combine("Instagram", Diagnostics.ProviderLogFile);

    private static string? Path
    {
        get
        {
            if (path is not null) return path.Length == 0 ? null : path;
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                path = FilePath;
                // A run starts its own log rather than growing the last one, and keeps that one beside it.
                LogRotation.StartFresh(path);
                written = 0;
            }
            catch (Exception) { path = ""; return null; }
            return path;
        }
    }

    // Lines about the connection itself rather than about what it carried. Kept past the cap (above).
    private static readonly string[] ReservedPrefixes =
    [
        "instagram connect", "instagram connection ", "instagram restore ", "instagram helper ", "instagram poll stopped",
        "instagram realtime state=", "instagram session not saved",
    ];
    internal static bool Reserved(string line) => ReservedPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal));

    // Queued here and written on a background thread, as the other logs are since each was found appending a file on
    // the interface thread: the Settings connection lines are written there, and the poll and realtime lines on threads
    // the interface may be waiting behind the same lock with.
    public static void Write(string line)
    {
        var cap = Reserved(line) ? ReservedLimit : Limit;
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

    // Writes what is queued now, on the calling thread, for a process about to end: the background drain may never run.
    internal static void Flush()
    {
        try { DrainPending(); } catch (Exception) { }
    }

    private static void DrainPending()
    {
        lock (Gate)
        {
            while (Pending.TryDequeue(out var entry))
            {
                if (Path is not { } file || written > entry.Cap) continue;
                try { File.AppendAllText(file, entry.Text, Encoding.UTF8); written += entry.Text.Length; }
                catch (Exception) { path = ""; }
            }
        }
    }

    // Six characters standing for one conversation, the same whether it is named by the app's id or Instagram's own.
    // One-way: the thread id cannot be recovered from it, and it means nothing outside this file.
    public static string Tag(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "none";
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes("instagram-app:" + Thread(id)));
        return Convert.ToHexString(sum.AsSpan(0, 3)).ToLowerInvariant();
    }

    // The app names a conversation "instagram:<account>:chat:<thread>", with the thread encoded as InstagramIdentity
    // encodes it (base64url, which has no colon). Decoded, it is the thread id the helper hands over, so both forms tag
    // alike. Anything else is tagged as it stands.
    private static string Thread(string id)
    {
        const string marker = ":chat:";
        var at = InstagramIdentity.OwnsAny(id) ? id.LastIndexOf(marker, StringComparison.Ordinal) : -1;
        if (at < 0) return id;
        var encoded = id[(at + marker.Length)..].Replace('-', '+').Replace('_', '/');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='))); }
        catch (FormatException) { return id; }
    }

    // The helper names three folders. Anything else is written as one word rather than as whatever arrived.
    public static string Folder(string? folder) => folder is "inbox" or "spam" or "pending" ? folder : "other";

    // The lines below take something that did not originate here — an exception, a helper's standard error, a folder, a
    // conversation id — so each reduces it to fixed words in one place the harness can hold to that.

    public static string Connection(string phase, string code) => $"instagram connection phase={Word(phase)} code={Word(code)}";

    // The conversation's tag goes last, so each line still begins as it did in the application log and a search written
    // for it then still finds it.
    public static string MarkReadSkipped(string chatId, string? folder) => $"instagram markread skipped folder={Folder(folder)} chat={Tag(chatId)}";

    public static string MarkRead(string chatId, bool validated, bool answered, string source, long? shortMs = null) =>
        $"instagram markread validated={validated} answered={answered} at={Word(source)}" + (shortMs is { } ms ? $" short_ms={ms}" : "") + $" chat={Tag(chatId)}";

    // A read Instagram did not take, or one held back because the last for the conversation failed moments ago.
    public static string MarkReadFailed(string chatId, string reason) => $"instagram markread failed reason={Word(reason)} chat={Tag(chatId)}";

    public static string History(string chatId, int returned, bool older, int positions, int withReaders) =>
        $"instagram history returned={returned} older={older} positions={positions} withReaders={withReaders} chat={Tag(chatId)}";

    public static string ArrivalResolved(string chatId, string from) => $"instagram arrival resolved from={Word(from)} chat={Tag(chatId)}";

    // A text or reply about to be sent: its length, and how long before it this account last sent typing there (TypingPings),
    // the measurement for whether a typing ping delays a send (2026-10-04). Counts and a duration; never the words.
    public static string Send(string chatId, int length, bool reply, long? sinceTypingMs) =>
        $"instagram send length={length} reply={reply} since_typing_ms={sinceTypingMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"} chat={Tag(chatId)}";

    // How it ended in fixed words only: the helper's standard error is runtime output that can quote anything.
    public static string HelperStopped(TimeSpan uptime, bool reached, string? lastError, string? declined) =>
        $"instagram helper stopped after {(int)uptime.TotalSeconds}s ready={reached} {InstagramHelperExit.Describe(lastError)} " +
        $"recover={(declined is null ? "yes" : Word(declined))}";

    // Every word passed to these is already one of the app's own, one of the helper's fixed codes or an exception's type
    // name; this is the backstop for a future caller that passes something else.
    private static string Word(string? value) =>
        value is { Length: > 0 and <= 48 } text && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? text : "other";
}
