using System.Security.Cryptography;
using System.Text;

namespace MessageLink.App.Services.GoogleMessages;

// The name everything this PC keeps locally is filed under: pins, mutes, labels, merges, drafts, names, reminders, read
// progress and the list kept between launches, for every service at once. Google Messages paired here names it after its
// device (GooglePairingIdentity). Without a pairing, a saved sign-in to any other service gives it a fixed name; with
// nothing signed in anywhere there is none, and none of those things can be kept.
internal static class GoogleLocalProfile
{
    // Tied to no account and never sent anywhere: it is only hashed into the keys of what this PC keeps. It also stands in a
    // kept record (GoogleSessionProfile.KeepAsync) as the "device" of an account whose state stayed under it.
    public const string Unpaired = "local:unpaired";

    // Not paired: the fixed name while another service is signed in here, else none. Until 2026-10-02 only Google Voice
    // counted, so a PC with only Messenger or Instagram kept nothing: no pins, mutes, drafts, labels or read progress,
    // and opening a conversation never marked it read (a clean-PC run found it).
    public static string? WithoutPairing(bool otherServiceSaved) => otherServiceSaved ? Unpaired : null;

    // Paired. kept is the record of which device this account's state is filed under (null when there is none);
    // unpairedUsed, whether state was ever filed under Unpaired here. An account's first pairing on a PC that already kept
    // state under Unpaired keeps it there (Adopt: the caller records that, as a kept record for this account), so
    // the pins, drafts and labels made for Messenger, Instagram or Voice before Google Messages was paired are still there
    // after. Before, the new device's name replaced it and all of that was left behind unread. A later new pairing of the
    // same account keeps whatever its record says, as before; another account starts afresh, as before. Without a kept
    // record or the Unpaired state this is the device's name exactly as it always was.
    public static (string Profile, bool Adopt) Paired(string device, string? account, (string Device, string Account)? kept, bool unpairedUsed)
    {
        if (kept is null && account is not null && unpairedUsed) return (Unpaired, true);
        var filed = GooglePairingIdentity.ProfileDevice(device, account, kept);
        return (filed == Unpaired ? Unpaired : DeviceProfile(filed), false);
    }

    public static string DeviceProfile(string device) => "google-direct:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(device)));
}
