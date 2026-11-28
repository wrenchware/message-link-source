namespace MessageLink.Presentation.Services;

/// <summary>
/// One dialog at a time across every window of the app: the main window's, each conversation's (its own window's too) and the message
/// actions'. A dialog site takes a lease before it shows anything and disposes it when it is done (<c>using var lease = ...</c>), so a dialog
/// that throws or returns early still lets the next one open. Whether one is open is part of "is the app free to leave?" (<see cref="LeavePolicy"/>),
/// and the Windows app tells a host in another process (AppBusy).
/// </summary>
public sealed class DialogGate
{
    private Lease? holder;

    /// <summary>Raised with the new answer of <see cref="IsHeld"/>, only when it changes, on the thread that took or released the lease.</summary>
    public event Action<bool>? Changed;

    public bool IsHeld => holder is not null;

    /// <summary>Whether the open dialog is one <paramref name="owner"/> took (a view, a window part, or a word for one kind of dialog).</summary>
    public bool IsHeldBy(object owner) => holder is { } lease && Equals(lease.Owner, owner);

    /// <summary>Null while another dialog is open; the caller shows nothing then.</summary>
    public Lease? TryTake(object owner)
    {
        if (holder is not null) return null;
        var lease = new Lease(this, owner);
        holder = lease;
        Changed?.Invoke(true);
        return lease;
    }

    private void Release(Lease lease)
    {
        // A lease disposed twice, or after the gate moved on, releases nothing it does not hold.
        if (!ReferenceEquals(holder, lease)) return;
        holder = null;
        Changed?.Invoke(false);
    }

    public sealed class Lease : IDisposable
    {
        private DialogGate? gate;

        internal Lease(DialogGate gate, object owner) { this.gate = gate; Owner = owner; }

        public object Owner { get; }

        public void Dispose()
        {
            var held = gate;
            gate = null;
            held?.Release(this);
        }
    }
}
