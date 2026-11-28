using MessageLink.App.Services.GoogleMessages;
using MessageLink.Server;
using MessageLink.WindowsPlatform;

namespace MessageLink.Server.Windows;

/// <summary>
/// What <see cref="GoogleSignInKeeper"/> asks of the host that runs it, answered by the server (SWITCHBOARD.md 3.1c). The window answers the same
/// questions from its own state (<c>MainWindow.GoogleRecovery</c>); here each comes from the host:
/// <list type="bullet">
/// <item><see cref="Post"/> is the shell's STA thread, where the renewal's WebView2 lives, as the window's is its interface thread.</item>
/// <item><see cref="UserBusy"/> is what the clients said (<c>client.state</c>): busy if any of them has a dialog open. The keeper reads
/// <see cref="HostClientState"/> itself ahead of this; it is answered the same way so the two cannot disagree.</item>
/// <item><see cref="Closed"/> is the server stopping.</item>
/// <item><see cref="StartupLoad"/> is the launch's own Google connect, which the restore ends.</item>
/// <item><see cref="ReconnectAsync"/> and <see cref="ReloadAfterRelease"/> connect Google Messages only, as the window's
/// <c>ConnectAndLoadAsync(restoreOthers: false)</c> does: nothing else is restored behind a renewal.</item>
/// <item><see cref="PauseSyncAsync"/> does nothing: the window stops its synchronization coordinator (<c>SyncViewModel</c>) around the renewal,
/// and that coordinator, the conversation lists it refreshes and Settings (which the window opens once renewing cannot help) are each client's
/// own. <see cref="ResumeSync"/> is where the window restarted that coordinator and re-read: the host publishes a Google-scoped
/// <c>ResyncRequired</c> in its event log, so every client re-reads what it shows after the helper swap. It is published when a renewal ends
/// however it ended, because a renewal that failed after replacing the helper has changed what a read would answer just as one that worked has.
/// While the swap is under way (the helper replaced, then Google Messages reconnected) a client's Google read fails, with the transient mark the
/// provider puts on "still connecting"; a client waits for the resync rather than retrying in a loop, which is why that is the cue and not a
/// poll. The host's own feed has no loop of its own to stop.</item>
/// </list>
/// </summary>
internal sealed class ServerSignInHost(IStaQueue sta, HostClientState clients, Func<CancellationToken, Task> connect, CancellationToken stop,
    Action<string> log, Action? resync = null) : IGoogleSignInHost
{
    private readonly TaskCompletionSource startupLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int connecting;

    public bool Closed => stop.IsCancellationRequested;

    public bool UserBusy => clients.Busy;

    public Task StartupLoad => startupLoad.Task;

    // The window's is DispatcherQueue.TryEnqueue, which drops the work when the queue is shutting down; so is this.
    public void Post(Action action) => sta.TryEnqueue(() => action());

    public Task PauseSyncAsync() => Task.CompletedTask;

    // Never throws into the keeper's finally: a resync that could not be published is a client that re-reads at its next event.
    public void ResumeSync()
    {
        try { resync?.Invoke(); }
        catch (Exception failure) { log($"server sign-in resync failed type={failure.GetType().Name}"); }
    }

    public Task ReconnectAsync() => ConnectAsync();

    // The launch's own load, run again from the connect it skipped. The window also opens Settings when only Google Messages is saved
    // (googleSettingsHeld); a client does that by itself.
    public void ReloadAfterRelease() => _ = ConnectAsync();

    /// <summary>
    /// Marks the Google connect as running, as the window's Connect command is busy for the whole of it; null when one already is. The launch's
    /// restore holds it around its own connect, so a renewal that ends while that is still going does not start a second one beside it
    /// (the window's <c>if (Connection.IsBusy) return</c>).
    /// </summary>
    public IDisposable? BeginConnect() => Interlocked.CompareExchange(ref connecting, 1, 0) == 0 ? new Connecting(this) : null;

    /// <summary>The launch's own Google connect has ended, however it ended (or never began): a renewal that waited for it goes on.</summary>
    public void EndStartupLoad() => startupLoad.TrySetResult();

    // Never throws, as the window's connect never does: its failure is a status the user reads, and the keeper's renewal has succeeded by
    // then. Only the type is logged (an exception's message can carry what was being opened).
    private async Task ConnectAsync()
    {
        if (BeginConnect() is not { } lease) return;
        using (lease)
        {
            try { await connect(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception failure) { log($"server sign-in reconnect failed type={failure.GetType().Name}"); }
        }
    }

    private sealed class Connecting(ServerSignInHost owner) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Volatile.Write(ref owner.connecting, 0);
        }
    }
}
