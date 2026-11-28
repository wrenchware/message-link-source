namespace MessageLink.Server;

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
/// <c>runs</c>, when given, is asked once the gate opens whether the feed runs yet (Google's: only with a saved pairing), and again until it does. Asked there rather than by the
/// composite when it starts, which a passive opening makes early: a composite started during a hold, before the pairing was there, left Google out for the
/// whole run (review, 2026-11-18).
/// </para>
/// </summary>
internal sealed class GatedRealtimeFeed(MessageLink.Core.Services.IRealtimeFeed inner, GatedRealtimeFeed? connecting = null, Func<bool>? runs = null) : MessageLink.Core.Services.IRealtimeFeed
{
    private readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How often a feed that does not run yet asks <c>runs</c> again. Set short by the checks.</summary>
    internal TimeSpan RunsRecheck { get; init; } = TimeSpan.FromSeconds(2);

    public void Open()
    {
        connecting?.Open();
        open.TrySetResult();
    }

    /// <summary>Lets every feed go but the one held within that connects by itself.</summary>
    public void OpenPassive() => open.TrySetResult();

    public async Task RunAsync(Func<MessageLink.Core.Services.RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        await open.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        // Not run yet: asked again until it does, without ending (an ended feed is said as a disconnect). Held until the end, as this once was, a first
        // pairing made after the gate opened was saved and never connected for the rest of the run (0.1.491 in a clean sandbox, 2026-11-22). The question
        // is a file on disk, so asking it again touches no service.
        while (runs is not null && !runs()) await Task.Delay(RunsRecheck, cancellationToken).ConfigureAwait(false);
        await inner.RunAsync(receive, cancellationToken).ConfigureAwait(false);
    }
}
