namespace MessageLink.App.Services.Instagram;

// At launch, a saved session reconnects by itself, so Instagram conversations are in the list without a visit to
// Settings. Without this the app came up reporting Instagram as disconnected every time, and the only way back was to
// press Reconnect by hand. Here rather than in the window so that every server reconnects to this real account the same
// way: at most three tries, none after an answer only the user can change.
internal static class InstagramRestore
{
    // Conversations when it connected. Otherwise the last failure, or Unreadable when the saved session cannot be read
    // here (a fresh sign-in, not a retry, is what that needs).
    internal sealed record Outcome(int? Conversations, Exception? LastFailure = null, bool Unreadable = false);

    // The wait before the second try; the third waits twice as long. A property so the harness need not sit through it.
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    // stopped: the server is closing, so no further try starts and giving up is not logged.
    public static Task<Outcome> RunAsync(InstagramBackend instagram, Func<bool> stopped) =>
        RunAsync(() => instagram.ResumeAsync(automatic: true), () => instagram.SavedSessionProblem, instagram.ExpectReconnect, stopped);

    // resume answers the conversation count, or -1 when no saved session could be used (problem says why).
    internal static async Task<Outcome> RunAsync(Func<Task<int>> resume, Func<string?> problem, Action<bool> expectReconnect,
        Func<bool> stopped)
    {
        // Said before the first attempt and kept through the waits between them, so a conversation opened meanwhile says
        // "reconnecting" throughout rather than flickering to "not connected" between tries.
        expectReconnect(true);
        try
        {
            Exception? lastFailure = null;
            var attempts = 0;
            // A helper that cannot start must not become a loop of sign-ins against Instagram.
            for (var attempt = 1; attempt <= 3 && !stopped(); attempt++)
            {
                attempts = attempt;
                try
                {
                    var resumed = await resume();
                    // A session this Windows user cannot read is not a failure to retry: it needs a fresh sign-in. Which
                    // step found nothing tells a store that failed apart from a helper that refused.
                    if (resumed < 0)
                    {
                        InstagramLog.Write(InstagramLog.Connection("restore", "saved_session_" + (problem() ?? "unreadable")));
                        return new(null, Unreadable: true);
                    }
                    return new(resumed);
                }
                catch (Exception failure)
                {
                    lastFailure = failure;
                    // The helper emits fixed codes. Never write arbitrary exception text: it may carry session or account data.
                    InstagramLog.Write(InstagramLog.Connection("restore", InstagramConnectionFailure.Code(failure)));
                    // Only what could turn out differently is asked again. An account check or an ended session gives
                    // the same answer until the user acts, and every repeat is another connection to a flagged account.
                    if (attempt == 3 || InstagramConnectionFailure.Definitive(failure)) break;
                    await Task.Delay(RetryDelay * attempt);
                }
            }
            if (!stopped()) InstagramLog.Write($"instagram restore gave_up attempts={attempts}");
            return new(null, lastFailure);
        }
        // The restore is over either way: connected, or nothing more is coming without the user.
        finally { expectReconnect(false); }
    }
}
