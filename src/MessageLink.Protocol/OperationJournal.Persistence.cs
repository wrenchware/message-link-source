using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MessageLink.Protocol;

// Keeping the journal across a host restart (SWITCHBOARD.md 3.0d).
//
// The blob, before the store protects it:
//   {"v":1,"entries":[{"id":..,"fp":..,"state":"running|done|failed","at":<unix ms>,"result":<json>,"error":{"code":..,
//     "transient":true,"outcomeUnknown":true,"done":true}}]}
// A failure keeps its code and never its sentence: some sentences name a contact ("You and <name> have no Instagram conversation
// yet", review 2026-10-08). After a restart the code is answered with a fixed sentence.
// `at` is when a running entry started and when a finished one finished; finished entries come first, oldest first. An entry never
// holds the request: only its fingerprint (a hash), the state and the answer. A result is kept only when it is plain ids or a number
// (KeepsResult), because the answers of some mutations carry a chat with its latest message and its people.
public sealed partial class OperationJournal
{
    private const int FormatVersion = 1;
    private const string ResultNotKeptSentence = "This was done before the host restarted, but what it answered was not kept.";
    private const string FailureNotKeptSentence = "This did not go through before the host restarted.";
    private const string DoneNotKeptSentence = "This went through before the host restarted, but what followed it was not kept. Refresh and it will appear.";

    private readonly IOperationJournalStore? store;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    // `version` counts changes and is read under the gate; `flushed` is the version the last good write held and is touched only under `writer`.
    private long version, flushed;
    private int scheduled;

    /// <summary>How long after an operation finishes the journal is written. Several that finish together share one write.</summary>
    public TimeSpan FlushDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    // The flush policy. Two moments matter, and they are not equally urgent.
    //  - An operation STARTING is written before it is asked of the provider, and the caller waits for it. If the process dies after
    //    that, the entry reloads as outcome_unknown, which is the truth. If it were written late, the same crash would leave no record, a
    //    reconnecting client would be told "unknown id" and could not tell "never started" from "started". A send is already a network
    //    round trip of hundreds of milliseconds; one small file replace in front of it is not noticed, and a start that arrives while
    //    another write is under way is covered by that write or the next, so a burst does not queue one write each.
    //  - An operation FINISHING is written after FlushDelay, off the caller's thread, and bursts coalesce. If the process dies in that
    //    window the entry reloads as still running, hence outcome_unknown: a finished send reported as unknown, never the other way, and
    //    unknown is never resent.
    // A write that fails is logged and the operation goes on: the journal in memory still makes it happen at most once in this process.
    // Dispose writes what is left.

    /// <summary>Writes the journal if anything changed since the last good write. Never throws.</summary>
    public async Task FlushAsync()
    {
        if (store is null) return;
        await writer.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[] snapshot;
            long taken;
            lock (gate)
            {
                if (flushed >= version) return;
                Prune(reserve: 0);
                taken = version;
                snapshot = Serialize();
            }
            try
            {
                await store.WriteAsync(snapshot).ConfigureAwait(false);
                flushed = taken;
            }
            catch (Exception) { log?.Invoke("journal write failed reason=io"); }
        }
        catch (Exception) { log?.Invoke("journal write failed reason=snapshot"); }
        finally { writer.Release(); }
    }

    private void ScheduleFlush()
    {
        if (store is null || Interlocked.Exchange(ref scheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(FlushDelay, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            Volatile.Write(ref scheduled, 0);
            await FlushAsync().ConfigureAwait(false);
        });
    }

    /// <summary>Writes anything not yet written. A host disposes its journal when it stops.</summary>
    public void Dispose()
    {
        if (store is null) return;
        stop.Cancel();
        Task.Run(FlushAsync).GetAwaiter().GetResult();
    }

    // Caller holds the gate.
    private byte[] Serialize()
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", FormatVersion);
            json.WriteStartArray("entries");
            foreach (var entry in finished) Write(json, entry);
            foreach (var entry in entries.Values)
                if (entry.State == OpState.Running) Write(json, entry);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void Write(Utf8JsonWriter json, Entry entry)
    {
        json.WriteStartObject();
        json.WriteString("id", entry.Id);
        json.WriteString("fp", entry.Fingerprint);
        json.WriteString("state", entry.State switch { OpState.Done => "done", OpState.Failed => "failed", _ => "running" });
        json.WriteNumber("at", (entry.State == OpState.Running ? entry.Started : entry.Finished).ToUnixTimeMilliseconds());
        if (entry.SendChatId is { } chatId)
        {
            json.WriteString("sendChatId", chatId);
            json.WriteNumber("sendAt", entry.Started.ToUnixTimeMilliseconds());
        }
        if (entry.Result is { } result && (KeepsResult(result) ? result : ScheduledOutcome(result)) is { } kept)
        {
            json.WritePropertyName("result");
            json.WriteRawValue(kept);
        }
        if (entry.Error is { } error)
        {
            json.WriteStartObject("error");
            json.WriteString("code", error.Code);
            if (error.Transient) json.WriteBoolean("transient", true);
            if (error.OutcomeUnknown) json.WriteBoolean("outcomeUnknown", true);
            if (error.Done) json.WriteBoolean("done", true);
            json.WriteEndObject();
        }
        json.WriteEndObject();
    }

    // A result is written only when it is a number, a flag or an object of nothing but ids (a send's receipt, the empty answer of a
    // mutation that returns nothing). Others, a chat above all, hold the text of the latest message and the people in it, and the
    // journal's rule is that no message text and no contact lands on disk. The operation is still recorded as done; what it answered is
    // not, so ops.status says "done" without a result and a repeat says so in words rather than inventing one.
    private static readonly HashSet<string> IdMembers = new(StringComparer.Ordinal) { "chatId", "pendingMessageId", "pendingMessageIds" };

    private static bool KeepsResult(byte[] result)
    {
        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    return true;
                case JsonValueKind.Object:
                    foreach (var member in root.EnumerateObject())
                    {
                        if (!IdMembers.Contains(member.Name)) return false;
                        var plain = member.Value.ValueKind == JsonValueKind.String
                            || (member.Value.ValueKind == JsonValueKind.Array && member.Value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String));
                        if (!plain) return false;
                    }
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException) { return false; }
    }

    private static readonly HashSet<string> ScheduledAnswerMembers = new(StringComparer.Ordinal) { "status", "id", "rev", "message", "text" };
    private static readonly HashSet<string> ScheduledStatusWords = new(StringComparer.Ordinal)
        { ScheduledStatuses.Done, ScheduledStatuses.Conflict, ScheduledStatuses.AlreadySending, ScheduledStatuses.Refused, ScheduledStatuses.Retired };

    // A scheduled command's answer, kept as its status word, id and rev without its sentence or words: dropped whole, a `refused` create whose answer was lost
    // read after a restart as done without a result, which a phone takes as kept and clears the draft for (review, 2026-11-18). Null for anything else.
    private static byte[]? ScheduledOutcome(byte[] result)
    {
        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().All(member => ScheduledAnswerMembers.Contains(member.Name))
                || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || !ScheduledStatusWords.Contains(status.GetString()!))
                return null;
            var buffer = new ArrayBufferWriter<byte>(128);
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteString("status", status.GetString());
                if (root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) json.WriteString("id", id.GetString());
                if (root.TryGetProperty("rev", out var rev) && rev.ValueKind == JsonValueKind.Number) json.WriteNumber("rev", rev.GetInt64());
                json.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }
        catch (Exception failure) when (failure is JsonException or FormatException) { return null; }
    }

    // Reads the stored journal into the empty one. An entry that was running when the process died has no outcome anyone knows, so it
    // becomes outcome_unknown, exactly as one cancelled after it started does.
    private void Load()
    {
        byte[]? plain;
        // The store may be asynchronous all the way down; run it off the caller's context so waiting here cannot deadlock one.
        try { plain = Task.Run(store!.ReadAsync).GetAwaiter().GetResult(); }
        catch (Exception)
        {
            log?.Invoke("journal load failed reason=unreadable");
            return;
        }
        if (plain is null)
        {
            log?.Invoke("journal loaded entries=0 interrupted=0");
            return;
        }

        var restored = new List<Entry>();
        string? problem = null;
        try { problem = Parse(plain, restored); }
        catch (Exception) { problem = "corrupt"; }
        if (problem is not null)
        {
            log?.Invoke("journal load failed reason=" + problem);
            return;
        }

        int interrupted = 0, before, after;
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var entry in restored.Where(e => e.State != OpState.Running).OrderBy(e => e.Finished))
                Adopt(entry);
            foreach (var entry in restored.Where(e => e.State == OpState.Running))
            {
                entry.State = OpState.Failed;
                entry.Error = new ProtocolError(ProtocolErrorCodes.OutcomeUnknown, UnknownSentence, OutcomeUnknown: true);
                entry.Finished = now;
                Adopt(entry);
                interrupted++;
            }
            before = entries.Count;
            Prune(reserve: 0);
            after = entries.Count;
            if (interrupted > 0 || after != before) version++;
        }
        log?.Invoke($"journal loaded entries={after} interrupted={interrupted}");
        // What was changed by loading is written now, so a second restart sees the same thing and not a fresh 24 hours.
        if (interrupted > 0 || after != before) Task.Run(FlushAsync).GetAwaiter().GetResult();

        void Adopt(Entry entry)
        {
            entry.Node = finished.AddLast(entry);
            entries[entry.Id] = entry;
            entry.Ended.TrySetResult();
        }
    }

    // Returns the word for what is wrong with the blob, or null when it was read into `restored`.
    private static string? Parse(byte[] plain, List<Entry> restored)
    {
        using var document = JsonDocument.Parse(plain);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number) return "corrupt";
        if (v.GetInt32() != FormatVersion) return "version";
        if (!root.TryGetProperty("entries", out var list) || list.ValueKind != JsonValueKind.Array) return "corrupt";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return "corrupt";
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() is not { Length: > 0 } idText || !seen.Add(idText)) return "corrupt";
            if (!item.TryGetProperty("fp", out var fp) || fp.ValueKind != JsonValueKind.String) return "corrupt";
            if (!item.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String) return "corrupt";
            if (!item.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.Number) return "corrupt";
            var time = DateTimeOffset.FromUnixTimeMilliseconds(at.GetInt64());
            var entry = new Entry(idText, fp.GetString()!) { Started = time, Finished = time };
            if (item.TryGetProperty("sendChatId", out var chatId))
            {
                if (chatId.ValueKind != JsonValueKind.String || chatId.GetString() is not { Length: > 0 } chat
                    || !item.TryGetProperty("sendAt", out var started) || started.ValueKind != JsonValueKind.Number) return "corrupt";
                entry.SendChatId = chat;
                entry.Started = DateTimeOffset.FromUnixTimeMilliseconds(started.GetInt64());
            }
            switch (state.GetString())
            {
                case "running":
                    break;
                case "done":
                    entry.State = OpState.Done;
                    if (item.TryGetProperty("result", out var result)) entry.Result = Encoding.UTF8.GetBytes(result.GetRawText());
                    break;
                case "failed":
                    entry.State = OpState.Failed;
                    if (!item.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object
                        || !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String) return "corrupt";
                    var outcomeUnknown = error.TryGetProperty("outcomeUnknown", out var unknownFlag) && unknownFlag.ValueKind == JsonValueKind.True;
                    // A failure that came after the operation went through must not read as "did not go through".
                    var done = error.TryGetProperty("done", out var doneFlag) && doneFlag.ValueKind == JsonValueKind.True;
                    entry.Error = new ProtocolError(code.GetString()!, outcomeUnknown ? UnknownSentence : done ? DoneNotKeptSentence : FailureNotKeptSentence,
                        error.TryGetProperty("transient", out var transient) && transient.ValueKind == JsonValueKind.True,
                        error.TryGetProperty("outcomeUnknown", out var unknown) && unknown.ValueKind == JsonValueKind.True, done);
                    break;
                default:
                    return "corrupt";
            }
            restored.Add(entry);
        }
        return null;
    }
}
