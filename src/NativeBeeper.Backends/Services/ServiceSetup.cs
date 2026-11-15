namespace NativeBeeper.App.Services;

// Which services are set up on this PC: those with a saved sign-in or pairing, whether or not connected at this moment.
// Each is a file check, cheap enough to ask whenever it matters and never cached, so a sign-in, a sign-out or a restore
// at launch is seen at once. Nothing set up at all is a first run (or every service signed out), which the list, Settings
// and New chat each say plainly instead of reporting a connection that was never there to fail.
internal static class ServiceSetup
{
    public static bool GooglePaired => NativeBeeper.App.Services.GoogleMessages.GoogleSessionProfile.HasSavedSession;
    public static bool OtherThanGoogle => NativeBeeper.App.Services.GoogleVoice.GoogleVoiceAccounts.HasSavedSessions ||
        NativeBeeper.App.Services.Messenger.MessengerBackend.HasSavedSession || NativeBeeper.App.Services.Instagram.InstagramBackend.AnySavedSession ||
        File.Exists(AppPaths.Combine("Hermes", "settings.protected"));
    public static bool Any => GooglePaired || OtherThanGoogle;
}
