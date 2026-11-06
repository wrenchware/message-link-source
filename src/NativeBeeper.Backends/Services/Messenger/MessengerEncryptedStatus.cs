using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Messenger;

/// <summary>
/// Whether the encrypted connection is carrying anything, as Settings says it (2026-10-04).
/// </summary>
/// <remarks>
/// Most of a Messenger account's conversations are end-to-end encrypted and travel on a second connection of their own.
/// Settings used to say "Connected" and show encrypted conversations as On whenever the ordinary connection was up, so
/// a PC whose encrypted connection had dropped looked healthy while nothing arrived in most conversations. The helper
/// reports that connection's state in "counts" as one word; this turns it into what the card shows, in fixed words.
/// </remarks>
internal static class MessengerEncryptedStatus
{

    // The helper's word. An older helper reports only deviceGone, which still says the worst case.
    public static MessengerEncryption Parse(string? word, bool deviceGone) => deviceGone ? MessengerEncryption.DeviceGone : word switch
    {
        "off" => MessengerEncryption.Off,
        "connecting" => MessengerEncryption.Connecting,
        "ready" => MessengerEncryption.Ready,
        "down" => MessengerEncryption.Down,
        "device_gone" => MessengerEncryption.DeviceGone,
        _ => MessengerEncryption.Unknown,
    };

    public const string DeviceGone = "Facebook no longer lists this PC as one of your Messenger devices, so end-to-end encrypted " +
        "conversations cannot send or receive. Turn encrypted conversations on again to register it.";
    public const string DeviceMissing = "This PC's encrypted-device keys are missing, so end-to-end encrypted conversations cannot send or receive, and Messages did not register a new device on its own. "
        + "To use them here again, turn encrypted conversations on in Settings, which registers this computer as a new Messenger device.";
    public const string Down = "Encrypted conversations are on, but their connection is down, so they can't send or receive here " +
        "right now. Messages keeps trying; choose Refresh to check again.";
    public const string Connecting = "Encrypted conversations are still connecting; new messages in them arrive once they have.";

    /// <summary>
    /// What the status line adds after "Connected", or null when there is nothing wrong to say. Turned on and not
    /// running in the helper (Off) is the same as down to the person: nothing arrives in those conversations.
    /// </summary>
    public static string? Problem(MessengerEncryption state, bool enabled) => state switch
    {
        MessengerEncryption.DeviceGone => DeviceGone,
        MessengerEncryption.DeviceMissing => DeviceMissing,
        MessengerEncryption.Down or MessengerEncryption.Off when enabled => Down,
        MessengerEncryption.Connecting when enabled => Connecting,
        _ => null,
    };

    /// <summary>The encrypted pill's word, and whether it is the healthy colour. Unknown keeps the plain "On".</summary>
    public static (string Label, bool Healthy) Pill(MessengerEncryption state) => state switch
    {
        MessengerEncryption.Ready or MessengerEncryption.Unknown => ("On", true),
        MessengerEncryption.Connecting => ("Connecting", false),
        MessengerEncryption.DeviceGone => ("Removed", false),
        MessengerEncryption.DeviceMissing => ("Keys missing", false),
        _ => ("Not connected", false),
    };

    public const string SignedOut = "Off while Messenger is signed out. Signing in again on this PC uses the same encrypted-device keys, so no new device is added.";

    /// <summary>
    /// The card's pill and line. A sign-out keeps the device keys and the switch (signing in again reuses the device), so with no sign-in the switch alone said a green
    /// On (seen 2026-10-12); signed out it is a plain Off. <c>Healthy</c> is null for that neutral case.
    /// </summary>
    public static (string Label, bool? Healthy, string Description) Card(MessengerEncryption state, bool signedIn)
    {
        if (!signedIn) return ("Off", null, SignedOut);
        var (label, healthy) = Pill(state);
        return (label, healthy, Description(state));
    }

    /// <summary>The line under the encrypted switch while it is on.</summary>
    public static string Description(MessengerEncryption state) => state switch
    {
        MessengerEncryption.DeviceGone => DeviceGone,
        MessengerEncryption.DeviceMissing => DeviceMissing,
        MessengerEncryption.Down or MessengerEncryption.Off => Down,
        MessengerEncryption.Connecting => Connecting,
        _ => "This PC receives new end-to-end encrypted messages. Earlier messages stay in Messenger.",
    };
}
