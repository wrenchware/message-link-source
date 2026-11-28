namespace MessageLink.App.Services.GoogleMessages;

// Privacy-safe timing and outcome counts for Settings > Troubleshooting: durations and counts only.
internal sealed partial class GoogleMediaBackend
{
    private readonly object diagnosticsLock = new();
    private readonly Queue<(double Queue, double Ready, double Copy)> downloads = new();
    private int avatarDiskHits, avatarDiskReads, avatarPhotos, avatarEmpty, avatarFailures, avatarSkipped, avatarNotReady;
    private string? lastAvatarFailure;
    private int downloadFailures;
    private string? lastDownloadFailure;

    // Fixed helper codes or failure kinds only; never upstream text or media details.
    private void RecordDownloadFailure(TimeSpan elapsed, Exception error)
    {
        var kind = error switch
        {
            MessageLink.Core.Backend.BackendException backendError => backendError.Failure.ToString(),
            InvalidOperationException { Message: "media_unavailable" } => "media unavailable",
            InvalidOperationException => "helper error",
            IOException => "transfer error",
            _ => error.GetType().Name
        };
        lock (diagnosticsLock) { downloadFailures++; lastDownloadFailure = $"{kind} after {elapsed.TotalSeconds:0.0} s"; }
    }

    private int uploads;
    private string? lastUpload;

    // Type, size, timings and outcome of attachment sends; never names, captions or recipients.
    private void RecordUpload(string mimeType, long bytes, TimeSpan? staged, TimeSpan total, string outcome)
    {
        var timing = staged is { } helper
            ? $"{helper.TotalSeconds:0.0} s to the helper, {(total - helper).TotalSeconds:0.0} s uploading to Google and submitting"
            : $"stopped after {total.TotalSeconds:0.0} s";
        var result = outcome switch
        {
            "pending" => "Google accepted it; your phone sends it from there",
            "rejected" => "rejected",
            _ => outcome
        };
        var type = mimeType.Length <= 40 && mimeType.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '+') ? mimeType : "other type";
        lock (diagnosticsLock) { uploads++; lastUpload = $"{type}, {bytes / 1048576.0:0.0} MB, {timing}; {result}"; }
    }

    private void RecordDownload(TimeSpan queue, TimeSpan ready, TimeSpan total)
    {
        lock (diagnosticsLock)
        {
            downloads.Enqueue((queue.TotalSeconds, ready.TotalSeconds, Math.Max(0, (total - ready).TotalSeconds)));
            while (downloads.Count > 8) downloads.Dequeue();
        }
    }

    internal string DiagnosticsSummary
    {
        get
        {
            static double Median(IEnumerable<double> values)
            {
                var sorted = values.Order().ToArray();
                return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
            }
            string images, sends;
            lock (diagnosticsLock)
            {
                sends = uploads == 0 ? "No attachments sent this session." : $"Attachments sent this session: {uploads} (last: {lastUpload}).";
                images = downloads.Count == 0 ? "No image downloads measured yet this session." :
                    $"Last {downloads.Count} image downloads (median): {Median(downloads.Select(d => d.Queue)):0.0} s waiting for other downloads, " +
                    $"{Median(downloads.Select(d => d.Ready)):0.0} s until the file was ready, {Median(downloads.Select(d => d.Copy)):0.0} s copying into the app; " +
                    $"slowest {downloads.Max(d => d.Queue + d.Ready + d.Copy):0.0} s.";
                if (downloadFailures > 0) images += $" {downloadFailures} failed (last: {lastDownloadFailure}).";
            }
            var avatarsText = $"Avatars this session: {avatarDiskHits} from cache ({avatarDiskReads} disk reads), {avatarPhotos} downloaded, {avatarEmpty} without a photo, {avatarFailures} failed" +
                (lastAvatarFailure is null ? "" : $" (last: {lastAvatarFailure})") + $", {avatarSkipped} skipped while backing off" +
                $", {Volatile.Read(ref avatarNotReady)} asked before Google connected.";
            return images + "\n" + sends + "\n" + avatarsText;
        }
    }
}
