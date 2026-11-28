using System.Security.Cryptography;
using System.Text;

namespace MessageLink.App.Services.Messenger;

// The app's half of the Messenger diagnostic log, beside the helper's. Counts, states and fixed reasons only: no
// names, message text, urls or raw identifiers. A conversation or message appears as a short one-way tag, enough to
// follow it through both logs and nothing else.
internal static class MessengerLog
{
    private const long Limit = 2 << 20;
    private static readonly object Gate = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Pending = new();
    private static int draining;
    private static string? path;
    private static long written;

    private static string? Path
    {
        get
        {
            if (path is not null) return path.Length == 0 ? null : path;
            try
            {
                var folder = System.IO.Path.GetDirectoryName(MessengerBackend.SessionPath)!;
                Directory.CreateDirectory(folder);
                path = System.IO.Path.Combine(folder, Diagnostics.ProviderLogFile);
                // A run starts its own log rather than growing the last one, and keeps that one beside it.
                LogRotation.StartFresh(path);
                written = 0;
            }
            catch (Exception) { path = ""; return null; }
            return path;
        }
    }

    // Queued here and written on a background thread. Every avatar in the list writes a line, and those lines were
    // being appended to a file on the interface thread while it had someone's typing to keep up with.
    public static void Write(string line)
    {
        if (written > Limit || !Diagnostics.Recording) return;
        Pending.Enqueue($"{DateTimeOffset.Now:HH:mm:ss.fff} {line}\n");
        if (Interlocked.CompareExchange(ref draining, 1, 0) == 0) _ = Task.Run(Drain);
    }

    private static void Drain()
    {
        do
        {
            DrainPending();
            Interlocked.Exchange(ref draining, 0);
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
            while (Pending.TryDequeue(out var text))
            {
                if (Path is not { } file || written > Limit) continue;
                try { File.AppendAllText(file, text); written += text.Length; }
                catch (Exception) { path = ""; }
            }
        }
    }

    // Why a conversation shows a picture or initials: whether one was offered with it, how many sources were tried,
    // and how many bytes came back. Only Messenger conversations are recorded; Google has its own paths.
    public static void Avatar(string? chatId, string? supplied, int attempted, int bytes)
    {
        var mine = chatId is not null && MessengerModels.Owns(chatId);
        if (!mine && supplied?.StartsWith("messenger-media:", StringComparison.Ordinal) != true) return;
        Write($"avatar chat={Tag(chatId)} offered={supplied is { Length: > 0 }} tried={attempted} " +
            (bytes < 0 ? "got=nothing" : $"got={bytes}"));
    }

    // The same six characters the helper uses for the same conversation, so one can be followed across both logs.
    public static string Tag(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "none";
        var native = MessengerModels.Owns(id) ? MessengerModels.Native(id) : id;
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes("messenger-helper:" + native));
        return Convert.ToHexString(sum.AsSpan(0, 3)).ToLowerInvariant();
    }
}
