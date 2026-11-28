namespace MessageLink.Presentation.Services;

// Automatic shell warnings wait for startup to settle and for an error to persist. This does not gate operations,
// composer refusals or the result of an explicit user action. One policy belongs to each independent notice.
public sealed class ShellNotice(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan StartupLimit = TimeSpan.FromSeconds(30), ErrorDelay = TimeSpan.FromMilliseconds(2500);
    private long? startup, pending;
    public string Message { get; private set; } = "";
    public bool IsOpen { get; private set; }
    public bool NeedsTick => Message.Length > 0 && !IsOpen;

    public void BeginStartup()
    {
        startup = clock.GetTimestamp();
        pending = null;
        IsOpen = false;
    }

    public void EndStartup()
    {
        if (startup is null) return; // A startup that exceeded its limit already reports normally.
        startup = null;
        pending = null;
    }

    public void Update(string? warning, bool busy = false)
    {
        if (!string.IsNullOrWhiteSpace(warning)) Message = warning;
        // A refresh in progress has not proved recovery. Keep an established warning steady through that refresh.
        else if (!busy) { Message = ""; pending = null; IsOpen = false; }
        if (Message.Length == 0) return;
        if (startup is { } starting)
        {
            if (clock.GetElapsedTime(starting) < StartupLimit) { IsOpen = false; return; }
            startup = null;
        }
        pending ??= clock.GetTimestamp();
        IsOpen = clock.GetElapsedTime(pending.Value) >= ErrorDelay;
    }
}
