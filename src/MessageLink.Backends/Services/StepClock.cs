using System.Diagnostics;

namespace MessageLink.App.Services;

// Times consecutive start-up steps of a process that is not the window, one line each in the log's launch style: "server compose step=<word> ms=<n>".
// The server's start is a chain of steps whose total was measured long before anyone knew which of them it was (SWITCHBOARD.md 3.1d), so these lines
// stay: they cost a clock read and a queued line. Only words and counts, never a name.
internal sealed class StepClock(Action<string> log, string prefix = "server compose")
{
    private readonly Stopwatch phase = Stopwatch.StartNew();

    /// <summary>Logs how long the step that has just ended took, and starts timing the next.</summary>
    public void Took(string step)
    {
        log($"{prefix} step={step} ms={phase.ElapsedMilliseconds}");
        phase.Restart();
    }
}
