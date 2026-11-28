namespace MessageLink.App.Services.Messenger;

// At launch, a saved session reconnects by itself so Messenger conversations are in the list without a visit to Settings.
// The policy is here rather than in the window so that every host (the app, a server) reconnects the same way: three
// tries, and none after an answer only the user can change.
internal static class MessengerRestore
{
    // Conversations when it connected; Refusal when Facebook refused the session outright; neither when it gave up, or
    // when nothing saved could be read (Skipped).
    internal sealed record Outcome(int? Conversations, Exception? Refusal = null, bool Skipped = false);

    // The wait before the second try; the third waits twice as long. A property so the harness need not sit through it.
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(3);

    // stopped: the host is closing, so no further try starts. A failure on the last try is thrown, as before.
    public static Task<Outcome> RunAsync(MessengerBackend messenger, Func<bool> stopped) =>
        RunAsync(messenger.ReadSavedCookiesAsync, cookies => messenger.ConnectAsync(cookies, adoptLegacy: true), stopped);

    internal static async Task<Outcome> RunAsync(Func<Task<Dictionary<string, string>?>> readSaved,
        Func<Dictionary<string, string>, Task<int>> connect, Func<bool> stopped)
    {
        var cookies = await readSaved();
        if (cookies is null) return new(null, Skipped: true);
        for (var attempt = 1; attempt <= 3 && !stopped(); attempt++)
        {
            // A retry reads the saved session again: one signed out (or removed from a server) during the wait is not signed back in with cookies kept from before.
            if (attempt > 1 && (cookies = await readSaved()) is null)
            {
                MessengerLog.Write($"startup restore attempt={attempt} skipped: signed out meanwhile");
                return new(null, Skipped: true);
            }
            try { return new(await connect(cookies)); }
            // Signed out between the read and the connect (the backend reads the session again under its lock and found none): nothing to restore, not a refusal.
            catch (InvalidOperationException gone) when (gone.Message == "missing_cookies")
            {
                MessengerLog.Write($"startup restore attempt={attempt} skipped: signed out meanwhile");
                return new(null, Skipped: true);
            }
            // Facebook's own refusal of the session (a checkpoint, an expired session) cannot change in nine
            // seconds, and two more logins during a checkpoint look worse to Facebook, so only what might pass on
            // a second try is retried: login_failed stays, because a network not up yet at boot or wake reads so.
            catch (Exception ex) when (MessengerFailure.IsDefiniteConnectFailure(ex))
            {
                MessengerLog.Write($"startup restore attempt={attempt} refused: {MessengerFailure.Code(ex)}; not retried");
                return new(null, ex);
            }
            catch (Exception) when (attempt < 3)
            {
                MessengerLog.Write($"startup restore attempt={attempt} failed; retrying");
                await Task.Delay(RetryDelay * attempt);
            }
        }
        return new(null);
    }
}
