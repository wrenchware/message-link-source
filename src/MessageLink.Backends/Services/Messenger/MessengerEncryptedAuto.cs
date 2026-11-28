using MessageLink.Core.Services;

namespace MessageLink.App.Services.Messenger;

// When connecting Messenger turns encrypted conversations on by itself. Most Messenger conversations are end-to-end
// encrypted and stay read-only until this PC is registered as one of the account's Messenger devices, which was a
// separate Settings button a newcomer had no reason to look for. I decided (2026-10-02) that a new sign-in does
// it straight away. Only a new sign-in: a launch that restores a saved session, a recovery after a helper stopped and a
// restart from Connection health never do, so a PC whose owner chose not to register it stays as it is. It is asked
// once and never repeated by itself: a registration whose answer did not arrive may still have happened, and the
// person can sign in again on the server's page to try again.
internal static class MessengerEncryptedAuto
{
    // Null when it should start; otherwise the fixed word the log gives for not starting. alreadyOn: turned on before
    // on this PC, in which case connecting has already reconnected it (MessengerBackend.RestoreEncryptedAsync). deviceLost: Facebook removed that device, or its
    // keys are gone (DeviceGone, DeviceMissing): reconnecting cannot bring it back, and the person's new sign-in is what asks for a new one, as Settings' turn-on
    // button once did. Nothing else ever registers a device.
    public static string? Skip(bool newSignIn, bool alreadyOn, bool deviceLost = false) => !newSignIn ? "saved_session" : alreadyOn && !deviceLost ? "already_on" : null;

    /// <summary>The encrypted side is on but its device is gone or keyless: only a new registration brings it back.</summary>
    public static bool DeviceLost(MessengerEncryption state) => state is MessengerEncryption.DeviceGone or MessengerEncryption.DeviceMissing;

    // What the Messenger card says afterwards, after the line that says the sign-in connected.
    public const string Starting = "Turning on encrypted conversations…";
    public const string TurnedOn = "Encrypted conversations are on too: new messages in them arrive here from now on.";
    public const string NotTurnedOn = "Encrypted conversations didn't turn on, so most conversations can't be read here yet. " +
        "Sign in to Messenger again on your Message Link Server's page to try again.";

    // Facebook usually answers in seconds. Past this the card says it is still waiting, so a slow registration does not
    // look stuck; the helper gives the call two minutes before it stops waiting itself.
    public static readonly TimeSpan StillWaitingAfter = TimeSpan.FromSeconds(20);
    public const string StillWaiting = "Still waiting for Facebook to register this PC as a Messenger device. This can take up to two minutes.";

    // No answer is not a refusal: Facebook may have registered this PC after all, so the card does not say it didn't
    // (2026-10-04). Said after a new sign-in and by the Settings button alike.
    public const string NoAnswer = "Facebook didn't answer in time, so encrypted conversations may not have turned on. " +
        "Sign in to Messenger again on your Message Link Server's page to try again.";

    // The codes that mean the registration went unanswered rather than refused: the helper's two minutes ran out, the call
    // was cancelled, or the helper went away mid-call (the transport's own limit closes it the same way).
    public static bool Unanswered(string code) => code is "e2ee_register_timeout" or "request_cancelled"
        or nameof(IOException) or nameof(TimeoutException) or nameof(OperationCanceledException) or nameof(TaskCanceledException);
}
