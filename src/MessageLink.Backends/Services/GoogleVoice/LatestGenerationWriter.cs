namespace MessageLink.App.Services.GoogleVoice;

// Serializes snapshots that arrive from a helper and keeps only the newest generation that has not started writing.
// The writer owns a value once it starts; discarded values are returned to the caller for prompt secret cleanup.
internal sealed class LatestGenerationWriter<T>(Func<T, Task> write, Action<T> discard) where T : class
{
    private readonly object gate = new();
    private long newest;
    private T? pending;
    private bool running;
    private Task worker = Task.CompletedTask;

    public void Offer(long generation, T value)
    {
        T? replaced = null;
        lock (gate)
        {
            if (generation <= newest)
            {
                replaced = value;
            }
            else
            {
                newest = generation;
                replaced = pending;
                pending = value;
                if (!running)
                {
                    running = true;
                    worker = Task.Run(DrainAsync);
                }
            }
        }
        if (replaced is not null) discard(replaced);
    }

    public Task Completion { get { lock (gate) return worker; } }

    private async Task DrainAsync()
    {
        while (true)
        {
            T value;
            lock (gate)
            {
                if (pending is null)
                {
                    running = false;
                    return;
                }
                value = pending;
                pending = null;
            }
            await write(value);
        }
    }
}
