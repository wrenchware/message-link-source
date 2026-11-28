using System.Text.Json;
using MessageLink.GoogleMessages.Spike;

namespace MessageLink.App.Services.GoogleMessages;

// A call that is skipped rather than queued while the helper's pipe is busy (skipIfBusy), asked again a moment later a
// few times. Busy is usually the instant another frame is being written, so a second ask a quarter of a second later
// mostly goes through; one that never does is the caller's to leave for later.
internal static class GoogleSkippedCalls
{
    // True once the helper took the call; false when it was still busy after the last try.
    internal static async Task<bool> TryAsync(Func<CancellationToken, Task<JsonElement>> call, int retries, CancellationToken token,
        Func<int, TimeSpan>? delay = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            if (!HelperRequests.IsSkipped(await call(token))) return true;
            if (attempt == retries) return false;
            await Task.Delay(delay?.Invoke(attempt) ?? TimeSpan.FromMilliseconds(250 << attempt), token);
        }
    }
}
