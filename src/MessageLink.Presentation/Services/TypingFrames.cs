namespace MessageLink.Presentation.Services;

// Whether the window should be asking for frames right now because someone is typing into it.
//
// A keystroke changes the composer's text and the interface thread handles it in well under a millisecond, but what a
// person sees is the frame carrying that change, and the frame was not always coming. The characters then arrive at
// whatever rate frames happen to be produced instead of the rate they were typed, which reads as latency and, when a
// key repeats, as text moving in visible steps rather than one character at a time. Nothing about it is visible from
// inside the thread's own work: it is never blocked, it allocates nothing extra, and every counter reads zero.
//
// Asking for a frame every refresh for as long as the app is running would cost that for the whole session. Typing is
// bursty, so frames are requested from the first keystroke and until a short quiet period has passed, which covers the
// gaps between words without leaving the request standing while nobody is typing.
//
// This decides when; the view does the asking, because only it can reach the compositor.
public sealed class TypingFrames(int holdMs = 400)
{
    private double lastKeystrokeMs;

    // Whether frames are being asked for at the moment.
    public bool Running { get; private set; }

    // A keystroke happened. True means start asking for frames; the caller has not been asking until now.
    public bool Note(double nowMs)
    {
        lastKeystrokeMs = nowMs;
        if (Running) return false;
        Running = true;
        return true;
    }

    // A frame arrived. True means stop asking: nothing has been typed for the whole quiet period, so the next
    // keystroke can start it again rather than every idle refresh paying for one that may never come.
    public bool Settled(double nowMs)
    {
        if (!Running || nowMs - lastKeystrokeMs < holdMs) return false;
        Running = false;
        return true;
    }

    // Focus left the composer, or the view closed. True means a standing request has to be withdrawn.
    public bool Stop()
    {
        if (!Running) return false;
        Running = false;
        return true;
    }
}
