namespace MessageLink.GoogleMessages.Spike;

// Serializes only complete pipe frames, with user actions ahead of queued background writes.
// Replies are matched independently and never hold this gate.
internal sealed class HelperCallGate
{
    private sealed class Waiter(bool priority)
    {
        public bool Priority { get; } = priority;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object sync = new();
    private readonly LinkedList<Waiter> waiting = new();
    private bool busy;

    public bool TryEnter()
    {
        lock (sync)
        {
            if (busy) return false;
            busy = true;
            return true;
        }
    }

    public async Task EnterAsync(bool priority, CancellationToken cancellationToken)
    {
        Waiter waiter;
        LinkedListNode<Waiter> node;
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!busy) { busy = true; return; }
            waiter = new(priority);
            // Priority waiters keep their own order but go before every background waiter.
            var at = waiting.First;
            while (priority && at is not null && at.Value.Priority) at = at.Next;
            node = priority && at is not null ? waiting.AddBefore(at, waiter) : waiting.AddLast(waiter);
        }
        using (cancellationToken.Register(() =>
        {
            // A waiter still in the list was never granted, so removing it can't lose the gate.
            lock (sync)
            {
                if (node.List is null) return;
                waiting.Remove(node);
            }
            waiter.Ready.TrySetCanceled(cancellationToken);
        }))
            await waiter.Ready.Task;
    }

    public void Release()
    {
        lock (sync)
        {
            if (waiting.First is { } next) { waiting.RemoveFirst(); next.Value.Ready.TrySetResult(); }
            else busy = false;
        }
    }
}
