namespace MessageLink.Protocol;

/// <summary>
/// The client's end of <c>client.state</c> (SWITCHBOARD.md 3.0b): tells the host whether anybody is looking, whether a dialog is open, which
/// services are read privately and whether link previews may be fetched. It sends once when started, again after every reconnect, and
/// whenever <see cref="Changed"/> is called because any of the four moved. Latest wins: changes that come while a call is on its way are
/// sent as the one newest state, and a state the host already holds is not sent again. A call that fails is tried again after a growing
/// pause, and at once when the client reconnects, until it lands; the host keeps the last it was told, so a lost call only leaves it
/// behind, never ahead.
/// </summary>
public sealed class ClientStateReporter : IDisposable
{
    private readonly ProtocolClient client;
    private readonly Func<ClientStateParams> read;
    private readonly Action<string>? log;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private Task running = Task.CompletedTask;
    private bool dirty, started, disposed, active, reconnected;
    // Completed by a reconnect to end the pause after a failed call.
    private TaskCompletionSource? backoff;
    private string? held;
    // Counts reconnects: an answer from the connection before the last one says nothing about what the current one holds.
    private long connection;

    public ClientStateReporter(ProtocolClient client, Func<ClientStateParams> read, Action<string>? log = null)
    {
        this.client = client; this.read = read; this.log = log;
        client.Reconnected += Reconnected;
    }

    /// <summary>The pause after a call fails; each failure in a row doubles it up to <see cref="MaxRetryDelay"/>.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Says the state for the first time. Before this, a change is noted and waits for it.</summary>
    public void Start()
    {
        lock (gate) { started = true; dirty = true; }
        Wake();
    }

    /// <summary>One of the four moved: the newest state is sent. Safe from any thread, and cheap when nothing changed.</summary>
    public void Changed()
    {
        lock (gate) dirty = true;
        Wake();
    }

    /// <summary>Completes when every change so far has landed; not while a failed call is waiting to be tried again.</summary>
    public Task SettledAsync()
    {
        lock (gate) return running;
    }

    private void Reconnected(object? sender, EventArgs args)
    {
        // The new connection is a new host-side entry: whatever the old one said is gone with it.
        lock (gate)
        {
            held = null; dirty = true; reconnected = true; connection++;
            // A backoff that is running was waiting for exactly this: the host is there again, so the state goes now.
            backoff?.TrySetResult();
        }
        Wake();
    }

    private void Wake()
    {
        lock (gate)
        {
            if (disposed || !started || active) return;
            active = true;
            running = Task.Run(SendAsync, CancellationToken.None);
        }
    }

    private async Task SendAsync()
    {
        var pause = RetryDelay;
        while (!stop.IsCancellationRequested)
        {
            ClientStateParams state;
            lock (gate)
            {
                if (!dirty || disposed) { active = false; return; }
                dirty = false; reconnected = false;
            }
            try
            {
                state = read();
                var key = Key(state);
                bool already;
                long on;
                lock (gate) { already = string.Equals(key, held, StringComparison.Ordinal); on = connection; }
                if (!already)
                {
                    await client.CallAsync<ClientStateParams, Done>(ProtocolMethods.ClientStateSet, state, stop.Token).ConfigureAwait(false);
                    // Only for the connection it went to: an old one's answer landing after a reconnect must not make the new one look told
                    // (review, 2026-10-08).
                    lock (gate) if (on == connection) held = key;
                }
                pause = RetryDelay;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // Nothing here is the user's business: the host is behind, and the next try or the reconnect catches it up.
                TaskCompletionSource? cut = null;
                lock (gate)
                {
                    dirty = true; held = null;
                    // A reconnect that came while the call was on its way is the cut already made: no backoff to wait out.
                    if (reconnected) pause = RetryDelay;
                    else cut = backoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                try { log?.Invoke("client state not sent"); } catch (Exception) { /* a logger that fails must not fail the client */ }
                if (cut is null) continue;
                try
                {
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    var elapsed = Task.Delay(pause, waiting.Token);
                    var woken = await Task.WhenAny(elapsed, cut.Task).ConfigureAwait(false);
                    waiting.Cancel();
                    if (stop.IsCancellationRequested) return;
                    // Cut short by a reconnect: start again from the shortest pause.
                    pause = woken == cut.Task ? RetryDelay : TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, MaxRetryDelay.Ticks));
                }
                finally { lock (gate) backoff = null; }
            }
        }
    }

    // The state as a string, so the same answers compare equal whatever order the services were listed in.
    internal static string Key(ClientStateParams state) =>
        $"{(state.Presence ? 1 : 0)}{(state.Busy ? 1 : 0)}{(state.LinkPreviews ? 1 : 0)}{(state.Active switch { true => 1, false => 0, null => '-' })}|{string.Join(",", state.PrivateReading.Order(StringComparer.Ordinal))}";

    public void Dispose()
    {
        lock (gate) disposed = true;
        client.Reconnected -= Reconnected;
        stop.Cancel();
    }
}
