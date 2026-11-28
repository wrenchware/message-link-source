using System.Text.Json;

namespace MessageLink.GoogleMessages.Spike;

// Each instance belongs to one process generation. Abandoned calls retain their slot until their reply
// arrives or the transport deadline expires; cancellation never hands their answer to another caller.
internal sealed class HelperRequests(Func<string, CancellationToken, Task> write, Action failed, TimeSpan? replyTimeout = null)
{
    // pair.qrWait outlasts six codes of thirty seconds (the helper's own bound is four minutes, requestBudget in dispatch.go).
    internal static TimeSpan TimeoutFor(string method) => method == "pair.finish" ? TimeSpan.FromMinutes(6.5) : method == "pair.qrWait" ? TimeSpan.FromMinutes(4.5) : TimeSpan.FromSeconds(135);
    private readonly object sync = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> pending = [];
    private readonly SemaphoreSlim capacity = new(64);
    private readonly HelperCallGate writes = new();
    private readonly CancellationTokenSource lifetime = new();
    private int nextId;
    private bool closed;
    // Calls someone on the other end would see (HelperMethods), counted from the moment the call is made, not from when
    // its frame is written, so one still queued behind the write gate is waited for too.
    private int mutations;
    // How many of those have ever begun on this generation: what tells a restart's question about the sends in flight
    // from a send that began after it was asked.
    private long started;
    public int MutationsInFlight => Volatile.Read(ref mutations);
    public long MutationsStarted => Interlocked.Read(ref started);
    // What a call that may be skipped answers when the pipe is busy. It has no id, because nothing was written and
    // nothing will answer it, which is how it is told from a reply; "skipped" is the shape its callers test for.
    internal static JsonElement Skipped { get; } = JsonSerializer.SerializeToElement(new { skipped = true });
    internal static bool IsSkipped(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object && !reply.TryGetProperty("id", out _)
        && reply.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True;

    // Priority comes from HelperMethods unless the caller says otherwise.
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken token, bool? priority = null, bool skipIfBusy = false)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        // A call that may be skipped is never one whose outcome matters.
        var mutation = !skipIfBusy && HelperMethods.IsMutation(method);
        var ahead = priority ?? HelperMethods.IsPriority(method);
        if (mutation) { Interlocked.Increment(ref started); Interlocked.Increment(ref mutations); }
        if (skipIfBusy)
        {
            waiting.Token.ThrowIfCancellationRequested();
            if (!capacity.Wait(0)) return Skipped;
        }
        else
        {
            try { await capacity.WaitAsync(waiting.Token); }
            catch { if (mutation) Interlocked.Decrement(ref mutations); throw; }
        }
        var entered = false;
        var registered = false;
        var id = 0;
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (skipIfBusy)
            {
                if (!writes.TryEnter()) return Skipped;
            }
            else await writes.EnterAsync(ahead, waiting.Token);
            entered = true;
            waiting.Token.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (closed) throw new IOException("Helper disconnected.");
                id = checked(++nextId);
                pending.Add(id, completion);
                registered = true;
            }
            // Once written, it settles when it is answered, fails or is torn down, not when its caller stops waiting.
            if (mutation) _ = completion.Task.ContinueWith(static (_, state) => ((HelperRequests)state!).MutationSettled(),
                this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            // Once a frame starts, finish it independently of caller cancellation. Any partial-write
            // failure makes this generation unusable. Never retry a potentially submitted mutation.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            _ = ExpireAsync(id, completion.Task, replyTimeout ?? TimeoutFor(method));
            try { await write(JsonSerializer.Serialize(new { id, method, @params = parameters }), deadline.Token); }
            catch { if (Close()) failed(); throw new IOException("Helper transport failed."); }
        }
        finally
        {
            if (entered) writes.Release();
            if (!registered)
            {
                capacity.Release();
                if (mutation) MutationSettled();
            }
        }
        try { return await completion.Task.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Submitted mutations and session operations keep their original outcome. Reads may stop
            // their upstream work, but still produce exactly one correlated terminal response.
            if (method is "ui.messages" or "ui.messagePage" or "ui.archivePage" or "ui.mediaRead" or "ui.avatar" or "ui.chatList" or "ui.contacts" or "ui.topContacts" or "ui.chat" or "ui.actions" or "cookies.replace")
                _ = CancelReadAsync(id);
            throw;
        }
    }

    private void MutationSettled() => Interlocked.Decrement(ref mutations);

    // Whether every contact-visible call settled within the budget. Polled: this runs once, at shutdown, and a wait
    // that had to be woken by every reply would cost every reply something for the sake of that one moment.
    public async Task<bool> WhenMutationsSettledAsync(TimeSpan budget)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (MutationsInFlight > 0)
        {
            if (clock.Elapsed >= budget) return false;
            await Task.Delay(20);
        }
        return true;
    }

    private async Task CancelReadAsync(int id)
    {
        var entered = false;
        try
        {
            await writes.EnterAsync(true, lifetime.Token);
            entered = true;
            lock (sync) { if (closed || !pending.ContainsKey(id)) return; }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await write(JsonSerializer.Serialize(new { cancel = id }), deadline.Token);
        }
        catch (Exception) { if (Close()) failed(); }
        finally { if (entered) writes.Release(); }
    }

    private async Task ExpireAsync(int id, Task<JsonElement> reply, TimeSpan timeout)
    {
        try { await reply.WaitAsync(timeout, lifetime.Token); }
        catch (TimeoutException)
        {
            if (Close(id)) failed();
        }
        catch (Exception) { } // Observe abandoned replies and teardown faults.
    }

    public void Accept(JsonElement reply)
    {
        if (!reply.TryGetProperty("id", out var received) || !received.TryGetInt32(out var id) || id <= 0)
            throw new IOException("Helper correlation failed.");
        TaskCompletionSource<JsonElement>? completion;
        lock (sync)
        {
            if (closed) return;
            if (!pending.Remove(id, out completion)) throw new IOException("Unexpected helper response.");
        }
        capacity.Release();
        completion.TrySetResult(reply.Clone());
    }

    public bool Close(int? onlyIfPending = null)
    {
        TaskCompletionSource<JsonElement>[] remaining;
        lock (sync)
        {
            if (closed || onlyIfPending is { } id && !pending.ContainsKey(id)) return false;
            closed = true;
            remaining = pending.Values.ToArray();
            pending.Clear();
        }
        Fail(remaining);
        return true;
    }

    // Closes this generation only while no contact-visible call is counted, so a restart never cuts one off. True when it
    // is closed, including by something else before. A mutation is counted before it takes this lock to register, so one
    // counted after the look here finds the generation closed and fails before its frame is written: nothing was sent.
    public bool CloseIfIdle()
    {
        TaskCompletionSource<JsonElement>[] remaining;
        lock (sync)
        {
            if (closed) return true;
            if (Volatile.Read(ref mutations) > 0) return false;
            closed = true;
            remaining = pending.Values.ToArray();
            pending.Clear();
        }
        Fail(remaining);
        return true;
    }

    // Closes this generation under the sends in flight, but only if no contact-visible call has begun since the count
    // `through` was read: those still in flight are the ones somebody agreed to cut off, and one that began after is not
    // theirs to cut. The count is taken before the lock a registering call takes, as in CloseIfIdle, so a call that begins
    // after this look finds the generation closed. True when it is closed, including before.
    public bool CloseIfNoneStartedSince(long through)
    {
        TaskCompletionSource<JsonElement>[] remaining;
        lock (sync)
        {
            if (closed) return true;
            if (Interlocked.Read(ref started) != through) return false;
            closed = true;
            remaining = pending.Values.ToArray();
            pending.Clear();
        }
        Fail(remaining);
        return true;
    }

    private void Fail(TaskCompletionSource<JsonElement>[] remaining)
    {
        lifetime.Cancel();
        foreach (var completion in remaining)
        {
            capacity.Release();
            completion.TrySetException(new IOException("Helper disconnected."));
        }
    }
}
