namespace NativeBeeper.Server;

// What the backends ask of their host that the server cannot give yet. Each is conservative (it says no, never guesses), and the composition
// logs `server port missing name=<word>` once for each, so a log shows what a headless run lacks. Delete each as the real one arrives.
// (The Google renewal's browser and sign-in host have arrived: WebViewGoogleBrowser over the shell's STA thread, and ServerSignInHost.)

/// <summary>
/// Holds a feed back until the host is allowed to connect. The event log runs its feed the moment a client subscribes, and Google's feed
/// connects the saved pairing by itself, so without <c>--restore</c> a client attached to a server on a real data root would have started the
/// very connection the flag keeps off. Opened by the restore, after it has started the other providers (SWITCHBOARD.md 1.7d).
/// <para>
/// Google's own feed, the only one that connects anything by being run, can be held by a gate of its own within this one (<c>connecting</c>). A start whose
/// restore is skipped or held then lets the others go (<see cref="OpenPassive"/>): they only pass on what their accounts already do, so Hermes and an account
/// a client connects are heard meanwhile.
/// </para>
/// <para>
/// <c>runs</c>, when given, is asked once the gate opens whether the feed runs at all (Google's: only with a saved pairing). Asked there rather than by the
/// composite when it starts, which a passive opening makes early: a composite started during a hold, before the pairing was there, left Google out for the
/// whole run (review, 2026-11-18).
/// </para>
/// </summary>
internal sealed class GatedRealtimeFeed(NativeBeeper.Core.Services.IRealtimeFeed inner, GatedRealtimeFeed? connecting = null, Func<bool>? runs = null) : NativeBeeper.Core.Services.IRealtimeFeed
{
    private readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open()
    {
        connecting?.Open();
        open.TrySetResult();
    }

    /// <summary>Lets every feed go but the one held within that connects by itself.</summary>
    public void OpenPassive() => open.TrySetResult();

    public async Task RunAsync(Func<NativeBeeper.Core.Services.RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        await open.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Not run: held until the end, as a feed the composite leaves out is (an ended feed is said as a disconnect and run again).
        if (runs is not null && !runs()) await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        await inner.RunAsync(receive, cancellationToken).ConfigureAwait(false);
    }
}
