namespace MessageLink.App.Services.GoogleMessages;

// Whether a sign-in renews the saved pairing or pairs this computer afresh (the phone's emoji step). Decided once the
// sign-in has its turn at the backend, not when it was asked for: the Windows server page's Sign in again used to decide
// before waiting behind the server's own renewal, so a person at the window sat through that renewal failing, then a
// second renewal of their own, before any pairing (2026-11-22: a phone that had removed the server, over two minutes,
// and the window was closed first).
internal static class GoogleSignInPlan
{
    // A renewal that ended this way cannot be mended by renewing again with other cookies: the phone removed the
    // pairing (pairing_lost), or no longer answers it (phone_unavailable, which is how a removal arrived on 2026-11-22,
    // after Google said "logged out").
    private static bool PhoneSideGone(string? failure) => failure is "pairing_lost" or "phone_unavailable";

    // pairIfRenewalCannot is for a sign-in a person is watching, where the emoji step costs a tap; the invisible
    // renewal never pairs (renewOnly).
    public static bool Fresh(bool asNewDevice, bool saved, bool qrSession, bool pairIfRenewalCannot, bool pairingLost, string? previousFailure) =>
        asNewDevice || !saved || qrSession || (pairIfRenewalCannot && (pairingLost || PhoneSideGone(previousFailure)));

    // After this sign-in's own renewal failed: pair afresh in the same window. authentication_required counts here
    // because the person's cookies are fresh; a different Google account (wrong_account) is never paired over the one there.
    public static bool PairAfterFailedRenewal(bool pairIfRenewalCannot, bool renewed, bool cancelled, bool pairingLost, string? failure) =>
        pairIfRenewalCannot && renewed && !cancelled && (pairingLost || PhoneSideGone(failure) || failure == "authentication_required");
}
