using System.Security.Cryptography;
using System.Text;
using NativeBeeper.Core.Backend;

namespace NativeBeeper.App.Services.GoogleMessages;

// The app-side Google Messages diagnostic log. It contains only fixed operation names, counts, timings, states and
// one-way tags. Never pass message text, names, numbers, URLs, session material or raw provider identifiers here.
internal static class GoogleMessagesLog
{
    private const long Limit = 2 << 20;
    private static readonly HashSet<string> SafeReasons = new(StringComparer.Ordinal)
    {
        "access_denied", "media_unavailable", "pairing_required", "missing_cookies", "invalid_session",
        "pair_start_failed", "pair_finish_failed", "connect_failed", "config_failed", "history_failed",
        "unsupported_method", "authentication_required", "rate_limited", "remote_unavailable",
        "phone_unavailable", "connection_closed", "network_unavailable", "unexpected_response", "wrong_account",
        "login_required", "login_failed", "challenge_required", "checkpoint_required", "consent_required",
        "session_expired", "session_already_started", "invalid_input", "not_connected", "send_failed",
        "send_rejected", "unknown_chat", "unknown_message", "delete_failed", "delete_rejected",
        "delete_unconfirmed", "chat_unavailable", "auth_required", "voice_unavailable", "unknown_method",
        "request_cancelled", "update_failed", "pairing_lost", "read_failed", "contacts_failed", "typing_failed",
        "delete_everyone_unsupported",
    };
    private static readonly object Gate = new();
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(DateTimeOffset At, string Line)> Pending = new();
    private static int draining;
    private static string? path, historyPath;
    private static long written;

    // The connection, sign-in, pairing and cookie lines again, dated, in a file kept for seven days across launches.
    // app.log keeps one previous run with times of day only, so by the time a refusal was looked at the run that explained
    // it was usually gone, and one refusal was read as a two-hour closure when it was the same refusal from a launch
    // earlier (2026-09-24 audit). Only lines already written to app.log, with the same content rules, are kept here.
    public static string HistoryFile => AppPaths.Combine("GoogleMessages", Diagnostics.SignInHistoryFile);
    public static readonly TimeSpan HistoryKept = TimeSpan.FromDays(7);
    public const long HistoryLimit = 1 << 20;
    private static readonly string[] HistoryPrefixes =
    [
        "app started", "connect ", "connection state=", "sign_in ", "browser ", "cookies ", "session save", "pairing lost",
        "logout marker", "listen failed", "realtime ", "helper stopped", "stopping", "stopped", "library level=error",
    ];
    // Also given each of those lines, timestamped, as it is written: the Linux server's standard output, which is where a container's log is read
    // (SWITCHBOARD.md 5.4: the renewal's `browser early=` lines were only in a file inside the container). The window never sets it. Set before the first line.
    public static Action<string>? HistoryMirror { get; set; }
    public static bool InHistory(string line) => HistoryPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal));

    private static string? Path
    {
        get
        {
            if (path is not null) return path.Length == 0 ? null : path;
            try
            {
                var folder = AppPaths.Combine("GoogleMessages");
                Directory.CreateDirectory(folder);
                path = System.IO.Path.Combine(folder, Diagnostics.ProviderLogFile);
                LogRotation.StartFresh(path);
                written = 0;
            }
            catch (Exception) { path = ""; return null; }
            return path;
        }
    }

    // Queued here and drained on a background thread, as the app's own log and Messenger's already are. Every read the
    // interface thread performs — a conversation, the list, an avatar, contacts — logged its outcome after the await
    // that resumes on that thread, so each one opened, appended to, flushed and closed a file there. The lock is shared
    // with the realtime event thread, which writes a line per chat event, so a burst could hold the interface thread
    // behind another thread's file I/O. It costs nothing measurable and does not allocate, which is exactly why it left
    // no trace in the responsiveness log: no counter, no allocation, no collection, just a thread that was gone.
    public static void Write(string line)
    {
        if (!Diagnostics.Recording || written > Limit && !InHistory(line)) return;
        Pending.Enqueue((DateTimeOffset.Now, line));
        if (Interlocked.CompareExchange(ref draining, 1, 0) == 0) _ = Task.Run(Drain);
    }

    private static string? History
    {
        get
        {
            if (historyPath is not null) return historyPath.Length == 0 ? null : historyPath;
            try
            {
                var file = HistoryFile;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                if (File.Exists(file))
                {
                    var lines = File.ReadAllLines(file);
                    var kept = KeepHistory(lines, DateTimeOffset.Now);
                    if (kept.Length != lines.Length) File.WriteAllLines(file, kept);
                }
                // app.log's own first line is written apart from this queue; each run is marked here the same way.
                File.AppendAllText(file, HistoryStamp(DateTimeOffset.Now) + " run started\n");
                historyPath = file;
            }
            catch (Exception) { historyPath = ""; return null; }
            return historyPath;
        }
    }

    // The date written before each history line, the same whatever the user's regional format: a Finnish time separator
    // or a Thai year made every line unreadable, or never old, when the next launch trimmed the file.
    internal static string HistoryStamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture);

    // The library repeats the same error every five seconds for as long as a refused helper runs (a refused acknowledgement
    // retried); the history keeps one of each per ten minutes, so the lines it exists to keep are not pushed out by them.
    private static string? lastLibraryLine;
    private static DateTimeOffset lastLibraryAt;
    private static bool RepeatedInHistory((DateTimeOffset At, string Line) entry)
    {
        if (!entry.Line.StartsWith("library ", StringComparison.Ordinal)) return false;
        if (entry.Line == lastLibraryLine && entry.At - lastLibraryAt < TimeSpan.FromMinutes(10)) return true;
        lastLibraryLine = entry.Line;
        lastLibraryAt = entry.At;
        return false;
    }

    // The lines of the last seven days, and of those no more than the limit's worth, newest kept. A line whose date cannot
    // be read is dropped with the old ones.
    internal static string[] KeepHistory(IReadOnlyList<string> lines, DateTimeOffset now)
    {
        var since = now - HistoryKept;
        var recent = lines.Where(line => line.Length >= 30 && DateTimeOffset.TryParseExact(line[..30], "yyyy-MM-dd HH:mm:ss.fff zzz",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) && at >= since).ToList();
        long size = 0;
        var start = recent.Count;
        while (start > 0 && size + recent[start - 1].Length + 1 <= HistoryLimit / 2) size += recent[--start].Length + 1;
        return recent.Skip(start).ToArray();
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
            while (Pending.TryDequeue(out var entry))
            {
                if (Path is { } file && written <= Limit)
                {
                    var text = $"{entry.At:HH:mm:ss.fff} {entry.Line}\n";
                    try { File.AppendAllText(file, text); written += text.Length; }
                    catch (Exception) { path = ""; }
                }
                if (InHistory(entry.Line) && !RepeatedInHistory(entry))
                {
                    // The same lines, as app.log would time them, wherever the host reads its log from (the Linux server's standard output).
                    if (HistoryMirror is { } mirror) try { mirror($"{entry.At:HH:mm:ss.fff} {entry.Line}\n"); } catch (Exception) { }
                    if (History is { } history)
                    {
                        // Trimmed at the next launch, so one long run can at most double it.
                        try { if (new FileInfo(history).Length < HistoryLimit) File.AppendAllText(history, HistoryStamp(entry.At) + " " + entry.Line + "\n"); }
                        catch (Exception) { historyPath = ""; }
                    }
                }
            }
        }
    }

    public static string Tag(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "none";
        var native = GoogleModels.Owns(id) ? GoogleModels.Native(id) : id;
        var sum = SHA256.HashData(Encoding.UTF8.GetBytes("google-messages-app:" + native));
        return Convert.ToHexString(sum.AsSpan(0, 3)).ToLowerInvariant();
    }

    // A message's status as Google names it (OUTGOING_DELIVERED), which is a state and never content; anything not
    // shaped like one is "other".
    public static string Status(string? status) =>
        string.IsNullOrEmpty(status) || status.Length > 64 || status.Any(c => c is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) ? "other" : status;

    // Only names from this fixed vocabulary reach the file, even if a future caller passes a dynamic method string.
    public static string Operation(string? method) => method switch
    {
        "ui.avatar" => "avatar",
        "ui.chat" => "chat",
        "ui.chatList" => "chat_list",
        "ui.contacts" => "contacts",
        "ui.topContacts" => "top_contacts",
        "ui.messagePage" => "message_page",
        "ui.archivePage" => "archive_page",
        "ui.linkPreviews" => "link_previews",
        "ui.mediaRead" => "media_read",
        "ui.mediaRelearn" => "media_relearn",
        "ui.mediaStage" => "media_stage",
        "ui.markRead" => "mark_read",
        "ui.react" => "react",
        "ui.deleteMessage" => "delete_message",
        "ui.sendPermission" => "send_permission",
        "ui.holdSends" => "hold_sends",
        "ui.sendText" => "send_text",
        "ui.sendMedia" => "send_media",
        "ui.startDirect" => "start_direct",
        "ui.startGroup" => "start_group",
        "ui.typing" => "typing",
        "ui.updateChat" => "update_chat",
        _ => "other",
    };

    // Which description of the conversation the helper checked a send or a reaction against (conversation_cache.go): the
    // phone's own from the last minute, or a lookup made for it. Only those two words reach the file.
    public static string Conversation(System.Text.Json.JsonElement answer)
        => answer.ValueKind == System.Text.Json.JsonValueKind.Object && answer.TryGetProperty("conversation", out var source)
            && source.ValueKind == System.Text.Json.JsonValueKind.String && source.GetString() is "cached" or "fetched"
            ? " conversation=" + source.GetString() : "";

    // A helper Windows refused to start is said by its Win32 number (HelperStartFailure): its type alone told a blocked
    // helper and a missing one apart from nothing, and its message names the path.
    public static string Reason(Exception failure) => failure switch
    {
        OperationCanceledException => "cancelled",
        System.ComponentModel.Win32Exception refused => "win32_" + refused.NativeErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
        InvalidOperationException { Message: var code } when SafeReasons.Contains(code) || GooglePairingFailure.IsCode(code) => code,
        BackendException backend => "backend_" + backend.Failure.ToString().ToLowerInvariant(),
        _ => failure.GetType().Name,
    };
}
