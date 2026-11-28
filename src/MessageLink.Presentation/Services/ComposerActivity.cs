namespace MessageLink.Presentation.Services;

// Whether somebody is typing right now, asked by anything that would otherwise put work on the interface thread
// while they are.
//
// Every composer shares one process and one interface thread, whatever provider its conversation belongs to. Google
// Messages has had its own version of this since its background history indexing was found competing with the
// composer, but that one is consulted only inside the Google backend — so Messenger and Google Voice event bursts
// reached the interface thread whether or not anybody was typing. This is the shared one, noted by every composer
// edit and asked at the single point all providers converge: the synchronization apply.
//
// It is deliberately a clock and a timestamp and nothing else. Typing is the most latency-sensitive thing the app
// does, so noting it must cost a single interlocked write and never a lock, an allocation or a dispatch.
public sealed class ComposerActivity(Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private long lastActivity;

    public void Note() => Interlocked.Exchange(ref lastActivity, now());
    public void Note(string? chatId)
    {
        if (!string.IsNullOrEmpty(chatId)) Note();
    }

    // True while the last composer edit is inside the window. False before anything has ever been typed, so nothing
    // is held back during a launch nobody is typing through.
    public bool RecentlyActive(TimeSpan quiet)
    {
        var last = Interlocked.Read(ref lastActivity);
        return last != 0 && now() - last < quiet.TotalMilliseconds;
    }
}
