using System.Security.Cryptography;

namespace MessageLink.App.Services;

// Which colour profiles may stay in a photo sent as it is: only one byte for byte identical to a stock profile.
//
// Dropping a profile changes the colours, so a stock one stays. But a profile is more than its numbers: it has a creation
// date and text (description, copyright, device maker and model), a calibrated display's profile (the one a screenshot or
// an editor's export carries) has a name the person typed, the calibration date and the display's model, and any profile
// can carry bytes no reader looks at, in the gaps between its tags, after the last one or inside a tag past its data.
// Checking tag names and texts (the first approach here) left those gaps: a review's profile with private bytes in them
// passed and still decoded. A hash of the whole profile leaves nothing unchecked. Anything else is re-encoded, which
// converts it to sRGB and writes no profile; a stock profile missing from this list costs that photo its recompression,
// never its privacy.
//
// The hashes are SHA-256 of the profiles as their makers ship them, taken 2026-10-09 from copies on my PC:
// - sRGB IEC61966-2.1 (Hewlett-Packard, 3144 bytes), Windows' own "sRGB Color Space Profile.icm", the one Photoshop and
//   most cameras and editors embed.
// - Adobe RGB (1998) (560 bytes), Adobe's "AdobeRGB1998.icc", the one cameras set to Adobe RGB embed.
// Apple's Display P3, which iPhones embed, is not here: no copy known to be the shipped one was at hand, so a P3 photo is
// re-encoded (to sRGB) until its hash is added from one.
internal static partial class PhotoMetadata
{
    private static readonly HashSet<string> StockProfiles = new(StringComparer.Ordinal)
    {
        "2b3aa1645779a9e634744faf9b01e9102b0c9b88fd6deced7934df86b949af7e",
        "304f569a83c1e5eddaddac54e99ed03339333db013738bb499ab64f049887e28",
    };

    internal static bool IsStockProfile(ReadOnlySpan<byte> profile) =>
        profile.Length is > 128 and < 65536 && StockProfiles.Contains(Convert.ToHexStringLower(SHA256.HashData(profile)));
}
