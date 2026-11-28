using MessageLink.Core.Backend;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services.GoogleMessages;

// Removing Google Messages from the server (its page's Remove). Until 2026-11-22 the only way was unpairing on the phone,
// and a server whose phone had done that kept trying the dead pairing: the owner had to wait for it to fail before the
// page offered anything. Now the server asks the phone to drop it, when it is connected and can ask, and forgets the
// pairing whatever the answer: the session, the sign-in's cookies, its age and the browser profile that renewed it, the
// search archive and what is held in memory. Nothing connects again until someone pairs it on the page.
internal sealed partial class GoogleMessagesBackend
{
    /// <summary>The words <see cref="RemoveAsync"/> answers with: whether the phone was asked to drop this computer, and how that went.</summary>
    internal static class Removal
    {
        public const string None = "none", Unpaired = "unpaired", NotConnected = "not_connected", Failed = "unpair_failed", TimedOut = "unpair_timeout";
    }

    internal async Task<string> RemoveAsync(CancellationToken token = default)
    {
        // The server's own invisible renewal is ended, not waited for: against a phone that has removed the server it runs a
        // minute before failing, and the first Remove in the sandbox was refused during one (2026-11-22). A pairing someone
        // is making on the page is theirs to finish or close; it is not torn away under them.
        if (invisibleRenewal is { } renewal)
        {
            try { renewal.Cancel(); } catch (ObjectDisposedException) { }
            GoogleMessagesLog.Write("remove renewal=ended");
        }
        else if (signingIn) throw new BackendException(BackendFailure.NotReady, "Google Messages is signing in. Finish or close that sign-in, then remove it.");
        // Ends a connect attempt in progress; the gate keeps new ones off while the pairing goes. signingIn is left alone: it is a
        // person's sign-in's, and a removal that set and cleared it could clear one that began meanwhile (code review 2026-11-22).
        try { attempt?.Cancel(); } catch (ObjectDisposedException) { }
        string outcome;
        string? profile;
        await gate.WaitAsync(token);
        try
        {
            if (!Files.SessionSaved && helper is null) { GoogleMessagesLog.Write("remove outcome=none"); return Removal.None; }
            // Read while the pairing it comes from is still on disk: an archive never loaded this run (Google never connected) is cleared all the same.
            try { profile = archive.Profile ?? await NotificationIdentity.ProfileAsync(tokens, token); }
            catch (Exception failure) when (failure is not OperationCanceledException) { profile = null; GoogleMessagesLog.Write($"remove profile=unknown reason={GoogleMessagesLog.Reason(failure)}"); }
            outcome = await AskPhoneToUnpairAsync();
            // Detached before it is disposed, so its end cannot mark anything down or write its session back.
            var previous = helper;
            helper = null;
            latestSession = null;
            connected = false;
            if (previous is not null) await previous.DisposeAsync();
            while (realtime.Reader.TryRead(out _)) { }
            // Under the session writes' own lock: a save the old helper started has passed its last check before its rename, and would otherwise put the file back
            // after this delete (code review 2026-11-22). Any save after this finds itself detached. The pairing a new device replaced goes too.
            await sessionWrites.WaitAsync(CancellationToken.None);
            try { File.Delete(Files.SessionFile); File.Delete(Files.SessionFile + ".previous"); }
            finally { sessionWrites.Release(); }
            Files.Cookies.Forget();
            await Files.Clock.ClearAsync();
            await Files.BrowserProfile.ForgetAsync();
            Files.Invalidate();
            signIn.SignedIn();
            authenticationRequired = false; lastLogoutMarker = null; renewalFailure = null; LastSignInFailure = null; presumedFor = null; qrSession = false;
            connectFailures = 0; ClearPause();
            lock (typingLock) typing.Clear();
            ForgetDefaultSms(); ForgetSims();
            GoogleMessagesLog.Write($"remove outcome=removed phone={outcome}");
        }
        finally { gate.Release(); }
        // The search archive and what is held in memory, as Troubleshooting's reset clears them; with no pairing nothing reads them again.
        if (profile is not null)
            try { await ResetLocalAsync(profile); }
            catch (Exception failure) { GoogleMessagesLog.Write($"remove archive=failed reason={GoogleMessagesLog.Reason(failure)}"); }
        Publish(new(RealtimeKind.Disconnected));
        NoteSignInState();
        return outcome;
    }

    // Only a connected helper can ask; a dead pairing or an offline phone is not waited for.
    private async Task<string> AskPhoneToUnpairAsync()
    {
        if (!connected || helper is not { } connection) return Removal.NotConnected;
        try
        {
            await connection.CallAsync("unpair", null, CancellationToken.None);
            return Removal.Unpaired;
        }
        catch (InvalidOperationException refused) { return refused.Message == Removal.TimedOut ? Removal.TimedOut : Removal.Failed; }
        catch (Exception) { return Removal.Failed; }
    }
}
