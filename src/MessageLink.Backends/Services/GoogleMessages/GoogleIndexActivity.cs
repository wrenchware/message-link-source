namespace MessageLink.App.Services.GoogleMessages;

// All composers share the process and UI thread, even when their provider has no remote typing signal.
internal sealed class GoogleIndexActivity(Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private long lastActivity;

    public void NoteForeground() => Interlocked.Exchange(ref lastActivity, now());
    public void NoteComposerActivity(string? chatId)
    {
        if (!string.IsNullOrEmpty(chatId)) NoteForeground();
    }
    public bool RecentlyActive(TimeSpan quiet)
    {
        var last = Interlocked.Read(ref lastActivity);
        return last != 0 && now() - last < quiet.TotalMilliseconds;
    }
}
