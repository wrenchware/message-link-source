using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NativeBeeper.Protocol;

/// <summary>
/// What makes a mutation happen at most once. A host keeps one journal for all its connections (so a client that
/// reconnects finds its operation) and runs every mutation through <see cref="ExecuteAsync"/>, keyed by the caller's
/// <c>clientOpId</c>:
/// <list type="bullet">
/// <item>A new id is recorded as running and the call is made, once.</item>
/// <item>An id that is running answers with that same call's outcome when it ends; nothing is started.</item>
/// <item>An id that is done answers with the stored result or the stored error, without calling anything.</item>
/// <item>An id used again for a different request (a different fingerprint) is refused with <c>invalid_request</c>.</item>
/// <item>A mutation whose token was cancelled before it started is recorded as <c>cancelled</c> and never made. One that was
/// cancelled after it started is recorded as <c>outcome_unknown</c>, because it may have reached the provider.</item>
/// </list>
/// Finished entries are kept for <see cref="MaxAge"/> and at most <see cref="MaxEntries"/> are held; the oldest finished go
/// first, and a running one is never dropped. Without a store it is in memory only: a restart forgets everything, so a client
/// that is told <c>unknown</c> must still report an unknown outcome and never send again. Given an
/// <see cref="IOperationJournalStore"/> (OperationJournal.Persistence.cs) it survives a restart: what had finished answers as it did,
/// and what was running when the process died is <c>outcome_unknown</c>.
/// </summary>
/// <remarks>
/// Log lines carry fixed words only; an operation id is the caller's and is never written. An entry holds the id, the fingerprint (a
/// hash), the answer and the error, and never the request's parameters: a sign-in's cookies are not kept here (SWITCHBOARD.md 2.6c; the
/// harness searches the entries for them).
/// </remarks>
public sealed partial class OperationJournal : IDisposable
{
    public const string UnknownSentence = "The connection was interrupted while this was being sent. It may or may not have gone through.";

    private sealed class Entry(string id, string fingerprint)
    {
        public string Id { get; } = id;
        public string Fingerprint { get; } = fingerprint;
        public OpState State = OpState.Running;
        public byte[]? Result;
        public ProtocolError? Error;
        public DateTimeOffset Started;
        public DateTimeOffset Finished;
        public string? SendChatId;
        public LinkedListNode<Entry>? Node;
        public readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly TimeProvider clock;
    private readonly Action<string>? log;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    // Finished entries, oldest first; a running entry joins when it ends, so the order is the order of finishing.
    private readonly LinkedList<Entry> finished = new();

    /// <summary>
    /// <paramref name="store"/> makes the journal survive a restart; it is read here, once, so build the journal off a UI thread.
    /// A store that cannot be read is logged and the journal starts empty: it never throws.
    /// </summary>
    public OperationJournal(TimeProvider? time = null, Action<string>? log = null, IOperationJournalStore? store = null)
    {
        clock = time ?? TimeProvider.System;
        this.log = log;
        this.store = store;
        if (store is not null) Load();
    }

    /// <summary>How long a finished operation is remembered.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The most operations held at once (running ones included); the oldest finished are dropped to stay within it.</summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>How many operations are held now.</summary>
    public int Count { get { lock (gate) return entries.Count; } }

    /// <summary>
    /// How many operations are running now: begun, not yet answered. A running entry is never in <c>finished</c> (it joins when it ends, and one
    /// reloaded after a restart is already finished as unknown), so this is a subtraction and costs nothing however full the journal is.
    /// </summary>
    public int RunningCount { get { lock (gate) return entries.Count - finished.Count; } }

    /// <summary>
    /// Runs <paramref name="call"/> for <paramref name="clientOpId"/> unless the journal already has it, and returns the
    /// serialized result. Throws a <see cref="ProtocolException"/> carrying the stored error for an operation that failed,
    /// <c>invalid_request</c> for an id reused with a different <paramref name="fingerprint"/>, and an
    /// <see cref="OperationCanceledException"/> when this caller, waiting on someone else's running operation, is cancelled
    /// (the operation itself carries on).
    /// </summary>
    public async Task<byte[]> ExecuteAsync(string clientOpId, string fingerprint, Func<CancellationToken, Task<byte[]>> call, CancellationToken cancellationToken,
        string? sendChatId = null)
    {
        Entry entry;
        bool owner;
        lock (gate)
        {
            // Looked up before room is made: making room first could drop this very id when the journal is full, and its repeat would
            // then run as new (review, 2026-10-08).
            Prune(reserve: 0);
            if (!entries.ContainsKey(clientOpId)) Prune(reserve: 1);
            if (entries.TryGetValue(clientOpId, out entry!))
            {
                if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    log?.Invoke("protocol journal refused reason=different-request");
                    throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "That operation id was already used for a different request."));
                }
                owner = false;
            }
            else
            {
                entry = new Entry(clientOpId, fingerprint) { Started = clock.GetUtcNow(), SendChatId = UnsettledSendRecorder is null ? null : sendChatId };
                entries[clientOpId] = entry;
                version++;
                owner = true;
            }
        }

        if (!owner)
        {
            log?.Invoke(entry.Ended.Task.IsCompleted ? "protocol journal repeat state=ended" : "protocol journal repeat state=running");
            // A repeat that gives up while the operation runs cannot say it did not happen: the operation may still go through.
            if (!entry.Ended.Task.IsCompleted)
                try { await entry.Ended.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.OutcomeUnknown, UnknownSentence, OutcomeUnknown: true)); }
            return Answer(entry);
        }

        byte[]? result = null;
        ProtocolError? error = null;
        // Known to be started on disk before anything is asked of the provider (see FlushAsync for why this one is synchronous).
        if (!cancellationToken.IsCancellationRequested) await FlushAsync().ConfigureAwait(false);
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Cancelled before anything was asked of the provider: certain that nothing happened.
                log?.Invoke("protocol journal outcome=cancelled-before-start");
                error = new ProtocolError(ProtocolErrorCodes.Cancelled, "The request was canceled.");
            }
            else result = await call(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled once the provider had been asked: it may have gone through, so it is not reported as cancelled.
            log?.Invoke("protocol journal outcome=unknown");
            error = new ProtocolError(ProtocolErrorCodes.OutcomeUnknown, UnknownSentence, OutcomeUnknown: true);
        }
        catch (Exception exception)
        {
            error = ProtocolErrors.From(exception);
        }

        lock (gate)
        {
            entry.Result = result;
            entry.Error = error;
            entry.State = error is null ? OpState.Done : OpState.Failed;
            entry.Finished = clock.GetUtcNow();
            if (error is null || !error.OutcomeUnknown && error.Code != ProtocolErrorCodes.OutcomeUnknown && (error.Done || error.Code == ProtocolErrorCodes.Cancelled))
                entry.SendChatId = null;
            entry.Node = finished.AddLast(entry);
            version++;
            Prune(reserve: 0);
        }
        // Publication is display state only. It is signalled from the first execution so repeating an operation cannot recreate a dismissed warning,
        // and only signalled: a stuck shared write must not hold up this answer or Ended (code review, round 3, 2026-10-13).
        _ = PublishUnsettledSendsAsync();
        entry.Ended.TrySetResult();
        ScheduleFlush();
        return Answer(entry);
    }

    // Reads only after Ended is set (or by the owner that set them), so no lock is needed. A finished operation reloaded from disk may have
    // no result (see KeepsResult): it was done, but this host cannot say what it answered.
    private static byte[] Answer(Entry entry) => entry.Error is { } error ? throw new ProtocolException(error)
        : entry.Result ?? throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.Internal, ResultNotKeptSentence));

    /// <summary>What the journal knows of <paramref name="clientOpId"/>, without starting, repeating or waiting for anything.</summary>
    public OpStatus Lookup(string clientOpId)
    {
        Entry? entry;
        lock (gate)
        {
            Prune(reserve: 0);
            if (!entries.TryGetValue(clientOpId, out entry)) return new OpStatus(OpState.Unknown);
            if (entry.State == OpState.Running) return new OpStatus(OpState.Running);
        }
        if (entry.Error is { } error) return new OpStatus(OpState.Failed, Error: error);
        if (entry.Result is null) return new OpStatus(OpState.Done);
        using var document = JsonDocument.Parse(entry.Result);
        return new OpStatus(OpState.Done, document.RootElement.Clone());
    }

    // Caller holds the gate. Drops finished entries that are too old, then the oldest finished while there is not room for
    // `reserve` more. A running entry is not in `finished`, so it is never dropped.
    private void Prune(int reserve)
    {
        var now = clock.GetUtcNow();
        var node = finished.First;
        while (node is { Value: var oldest } && (now - oldest.Finished >= MaxAge || entries.Count + reserve > MaxEntries))
        {
            var next = node.Next;
            // An unrecorded warning is still owed to the other devices, even if publication has been unavailable past the normal journal window.
            if (oldest.SendChatId is null) { finished.Remove(node); entries.Remove(oldest.Id); }
            node = next;
        }
    }

    /// <summary>
    /// A stable name for what a request asks: SHA-256 over the method and its params with <c>clientOpId</c> left out, written
    /// canonically (members in ordinal order, null members dropped), so the same request hashes the same whichever peer wrote it.
    /// </summary>
    public static string Fingerprint(string method, JsonElement parameters)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
            WriteCanonical(writer, parameters, skip: "clientOpId");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(method));
        hash.AppendData([0]);
        hash.AppendData(buffer.WrittenSpan);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, string? skip = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null && p.Name != skip)
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
                break;
            default:
                // Strings, numbers and literals as they were written: the same peer writes the same text for the same value.
                element.WriteTo(writer);
                break;
        }
    }
}
