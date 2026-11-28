namespace MessageLink.App.Services.GoogleVoice;

// The app's Google Voice diagnostic log. Steps, counts and fixed reasons only: no numbers, names, message text, cookies
// or raw identifiers. An account appears as the first four characters of its folder key.
internal static class GoogleVoiceLog
{
    private const long Limit = 1 << 20;
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
                var folder = AppPaths.Combine("GoogleVoice");
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

    // Queued and drained on a background thread, for the same reason as the Google Messages log beside it: an append
    // here happened on whichever thread logged, and that was routinely the interface thread.
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

    // A helper diagnostic as name=value pairs. Only fixed field names, numbers and short plain words (HTTP statuses, step
    // names, well-known cookie names) are written; anything else is dropped rather than risk logging what it holds.
    private static readonly HashSet<string> FieldNames = new(StringComparer.Ordinal) { "login", "status", "realtime", "refresh", "cookies", "added", "changed", "removed", "voicemails", "withMedia", "shape", "voicemail", "type", "kb",
        "send", "archived", "listExtra", "olderPage", "threads", "overlap", "nextToken", "replace", "recovery",
        "contactList", "listedNumbers", "lookedUp", "matched", "lookup", "unnamed", "named",
        "direct", "directIdsMatch", "thread", "spam", "arrived", "newThreads", "delete", "reason" };
    public static string Fields(System.Text.Json.JsonElement data)
    {
        if (data.ValueKind != System.Text.Json.JsonValueKind.Object) return "diagnostic=invalid";
        static string? Word(System.Text.Json.JsonElement value) => value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when value.TryGetInt32(out var number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            System.Text.Json.JsonValueKind.String when value.GetString() is { Length: > 0 and <= 80 } text && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') => text,
            _ => null,
        };
        var parts = new List<string>();
        foreach (var field in data.EnumerateObject())
        {
            if (!FieldNames.Contains(field.Name)) continue;
            var value = field.Value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? "[" + string.Join(",", field.Value.EnumerateArray().Select(Word).Select(word => word ?? "?")) + "]"
                : Word(field.Value) ?? "?";
            parts.Add(field.Name + "=" + value);
        }
        return parts.Count == 0 ? "diagnostic=empty" : string.Join(" ", parts);
    }

    public static string Tag(string? key) => key is { Length: >= 4 } ? key[..4] : "none";

    // A failure as a fixed code: the helper's own error codes pass through, anything else is named by its type.
    public static string Reason(Exception failure) => failure switch
    {
        InvalidOperationException { Message: var code } when code.Length <= 40 && code.All(c => char.IsAsciiLetterLower(c) || c == '_') => code,
        MessageLink.Core.Backend.BackendException backend => "backend_" + backend.Failure.ToString().ToLowerInvariant(),
        _ => failure.GetType().Name,
    };
}
