namespace MessageLink.Presentation.Services;

public static class NotificationDelayText
{
    // Durations only, no message content. Arrival compares the message's own timestamp with this
    // PC's clock, so a small clock difference between phone and PC shows up in that number.
    public static string Summary(IReadOnlyList<(double Arrival, double App)> samples)
    {
        if (samples.Count == 0) return "Notification timing appears here after the next message notification.";
        static double Median(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        }
        return $"Last {samples.Count} notification{(samples.Count == 1 ? "" : "s")}: messages reached this app {Median(samples.Select(s => s.Arrival)):0.0} s after sending " +
            $"(slowest {samples.Max(s => s.Arrival):0.0} s), then appeared {Median(samples.Select(s => s.App)):0.0} s later.";
    }
}
