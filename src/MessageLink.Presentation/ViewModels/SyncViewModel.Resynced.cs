using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

public sealed partial class SyncViewModel
{
    /// <summary>
    /// Every time the list has been applied as a full resync, with the moment (ProgressIndicator.SinceLaunch) that resync
    /// began: every service connected before then has been read into the list. Raised on the interface thread, also for a
    /// resync that carried no events — the retry after a failed one — which BatchApplied leaves out. A handler must be quick.
    /// </summary>
    public event EventHandler<TimeSpan>? ListResynced;

    private void Resynced(TimeSpan began)
    {
        if (ListResynced is not { } listeners) return;
        try { listeners(this, began); }
        // Whatever a listener does wrong, the sync pass goes on. The type alone: an exception's message may name anything.
        catch (Exception failure) { Allocated?.Invoke(this, $"resync listener failed error={failure.GetType().Name}"); }
    }
}
