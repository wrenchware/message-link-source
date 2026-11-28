using System.Text.Json;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// The server's side of pushed state: builds the <see cref="ServerState"/> and appends it to the <see cref="EventLog"/>
/// as a <c>state</c> event whenever it differs from the last one appended, compared as serialized JSON. It looks again
/// <list type="bullet">
/// <item>when a connection event is about to be recorded (<see cref="Watch"/>), so the state is in the log before the event that announced it;</item>
/// <item>when an account port raises <c>Changed</c> (<see cref="Attach"/>), counted in the state's revisions so a change the snapshot does not
/// show still reaches a client;</item>
/// <item>and every <see cref="Interval"/>, which is the safety net for anything no event announced (a renewal that settles on its own, a
/// session file that appeared).</item>
/// </list>
/// An unchanged state appends nothing. The first look (<see cref="Start"/>) happens before any reader subscribes, so a subscriber always
/// has a state to be followed by.
/// </summary>
public sealed class StatePublisher : IDisposable
{
    private readonly EventLog log;
    private readonly Func<ServerState> build;
    private readonly Action<string>? write;
    private readonly object gate = new();
    private readonly List<Action> detach = [];
    private readonly Timer timer;
    private int messenger, instagram, google, voice;
    private string? lastJson;
    private (ServerState State, long Seq)? latest;
    private int queued;
    private bool disposed;
    private int appended;

    /// <param name="interval">How often the server looks again with nothing to announce it; a few seconds in production.</param>
    public StatePublisher(EventLog log, Func<ServerState> build, TimeSpan? interval = null, Action<string>? write = null)
    {
        this.log = log;
        this.build = build;
        this.write = write;
        Interval = interval ?? TimeSpan.FromSeconds(5);
        timer = new Timer(_ => Refresh(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Interval { get; }

    /// <summary>How many state events this publisher has appended.</summary>
    public int Appended => Volatile.Read(ref appended);

    /// <summary>Takes the first look and starts the timer. Call once the log's server is ready and before a reader subscribes.</summary>
    public void Start()
    {
        Refresh();
        lock (gate) if (!disposed) timer.Change(Interval, Interval);
    }

    /// <summary>
    /// Builds the state, and appends it if it differs from the last appended. Returns whether it did. A build that throws appends
    /// nothing and is logged as a fixed word; the next look tries again.
    /// </summary>
    public bool Refresh()
    {
        lock (gate)
        {
            if (disposed) return false;
            ServerState state;
            try { state = build() with { Revisions = new AccountRevisions(messenger, instagram, google, voice) }; }
            catch (Exception)
            {
                Write("protocol state build failed");
                return false;
            }
            var json = JsonSerializer.Serialize(state, ProtocolJson.Options);
            if (string.Equals(json, lastJson, StringComparison.Ordinal)) return false;
            lastJson = json;
            latest = (state, log.AppendState(state));
            Interlocked.Increment(ref appended);
            Write($"protocol state appended scopes={state.Scopes.Count} voice={state.Voice.Count}");
            return true;
        }
    }

    /// <summary>The newest state appended and the sequence number it was given, or none before the first look.</summary>
    public (ServerState State, long Seq)? Latest { get { lock (gate) return latest; } }

    /// <summary>
    /// Wire this to <see cref="EventLog.BeforeSourceEvent"/>: a connection change looks again now, so the state precedes the event.
    /// </summary>
    public void Watch(RealtimeEvent change)
    {
        if (change.Kind is RealtimeKind.Connected or RealtimeKind.Disconnected or RealtimeKind.AuthenticationRequired or RealtimeKind.ResyncRequired) Refresh();
    }

    /// <summary>
    /// Follows each account port's <c>Changed</c>: it counts as a change of that account, and the state is looked at again soon. The facts
    /// no state shows (the phone's SIMs changed, the default SMS app changed, the conversations were renumbered, Instagram stopped
    /// reconnecting) are appended to the log as account signals, in the order they were raised.
    /// </summary>
    public void Attach(IMessengerAccount messengerAccount, IInstagramAccount instagramAccount, IGoogleMessagesAccount googleAccount, IGoogleVoiceAccounts voiceAccounts)
    {
        void Messenger(object? sender, EventArgs args) => Changed(ref messenger);
        void Instagram(object? sender, EventArgs args) => Changed(ref instagram);
        void Google(object? sender, EventArgs args) => Changed(ref google);
        void Voice(object? sender, EventArgs args) => Changed(ref voice);
        // The default SMS app's line is part of Google's snapshot but is announced by an event of its own: the state first, then the signal.
        void DefaultSms(object? sender, EventArgs args) => Enqueue(() => { Refresh(); log.AppendSignal(new AccountSignal(AccountSignals.DefaultSmsApp)); });
        void Sims(object? sender, EventArgs args) => Enqueue(() => log.AppendSignal(new AccountSignal(AccountSignals.Sims)));
        void Renumbered(object? sender, bool value) => Enqueue(() => log.AppendSignal(new AccountSignal(AccountSignals.Renumbered, value)));
        void Abandoned(object? sender, EventArgs args) => Enqueue(() => log.AppendSignal(new AccountSignal(AccountSignals.ReconnectAbandoned)));
        messengerAccount.Changed += Messenger; instagramAccount.Changed += Instagram; googleAccount.Changed += Google; voiceAccounts.Changed += Voice;
        googleAccount.DefaultSmsAppChanged += DefaultSms; googleAccount.SimsChanged += Sims; googleAccount.RenumberedChanged += Renumbered;
        instagramAccount.ReconnectAbandoned += Abandoned;
        lock (gate)
            detach.Add(() =>
            {
                messengerAccount.Changed -= Messenger; instagramAccount.Changed -= Instagram; googleAccount.Changed -= Google; voiceAccounts.Changed -= Voice;
                googleAccount.DefaultSmsAppChanged -= DefaultSms; googleAccount.SimsChanged -= Sims; googleAccount.RenumberedChanged -= Renumbered;
                instagramAccount.ReconnectAbandoned -= Abandoned;
            });
    }

    // The ports raise these from whichever thread noticed, sometimes inside their own locks, so each is handled on another thread; one
    // chain keeps them in the order they were raised.
    private readonly object chainGate = new();
    private Task chain = Task.CompletedTask;

    private void Enqueue(Action action)
    {
        lock (chainGate)
            chain = chain.ContinueWith(_ =>
            {
                try { if (!Volatile.Read(ref disposed)) action(); }
                catch (Exception) { /* a signal that cannot be recorded is a missed refresh at worst */ }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Changed(ref int revision)
    {
        Interlocked.Increment(ref revision);
        Soon();
    }

    // An account raises Changed from whichever thread noticed, sometimes in bursts and sometimes inside its own lock, so the look happens
    // on another thread, once for the whole burst.
    private void Soon()
    {
        if (Interlocked.Exchange(ref queued, 1) == 1) return;
        _ = Task.Run(() =>
        {
            Volatile.Write(ref queued, 0);
            Refresh();
        });
    }

    private void Write(string line)
    {
        try { write?.Invoke(line); } catch (Exception) { /* a logger that fails must not fail the host */ }
    }

    public void Dispose()
    {
        Action[] ending;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ending = [.. detach];
            detach.Clear();
        }
        timer.Dispose();
        foreach (var undo in ending) undo();
    }
}
