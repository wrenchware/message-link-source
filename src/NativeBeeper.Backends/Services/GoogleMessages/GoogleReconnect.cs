using System.Text.Json;

namespace NativeBeeper.App.Services.GoogleMessages;

internal static class GoogleReconnect
{
    private static readonly TimeSpan[] Pauses = [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
    // The pause after the nth failed connection attempt in a row; the fifth and every one after wait five minutes.
    public static TimeSpan Pause(int failures) => Pauses[Math.Clamp(failures, 1, Pauses.Length) - 1];
    // Only lowercase letters and underscores, briefly, reach the log from a helper event.
    public static string Code(string? value) =>
        string.IsNullOrEmpty(value) || value.Length > 32 || value.Any(c => c is not (>= 'a' and <= 'z' or '_')) ? "other" : value;

    // Google's logout marker is proof of nothing on its own (connection_state.go in the helper): it is pushed when the
    // phone's registration rotates. A second one within this window, after a reconnect, is.
    public static readonly TimeSpan LogoutConfirmWindow = TimeSpan.FromMinutes(30);
    public static bool LogoutConfirmed(DateTimeOffset? previous, DateTimeOffset now) =>
        previous is { } seen && now - seen <= LogoutConfirmWindow && now >= seen;

    // A library warning as the helper passed it: words only, or nothing worth logging.
    private static readonly System.Text.RegularExpressions.Regex Words = new("^[A-Za-z][A-Za-z ,'()-]{0,95}$");
    public static string LibraryMessage(string? value) => value is not null && Words.IsMatch(value) ? value : "(other)";

    // Cookie names from a helper event's array property, as "[A,B]": names only (the helper never sends a value), each
    // checked again, at most twenty. Which cookies Google changes is what says whether a renewal renewed anything.
    private static readonly System.Text.RegularExpressions.Regex CookieName = new("^[A-Za-z0-9_.-]{1,64}$");
    public static string CookieNames(JsonElement owner, string property)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return "[]";
        var names = list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .Where(name => name is not null && CookieName.IsMatch(name)).Take(20);
        return "[" + string.Join(",", names) + "]";
    }
}

// Why pairing failed, from the helper's fixed codes (pair_errors.go), in words someone can act on.
internal static class GooglePairingFailure
{
    private static readonly System.Text.RegularExpressions.Regex Shape = new("^pair_[a-z_]+?(_[0-9]{1,4}){0,2}$");
    public static bool IsCode(string code) => code.Length <= 40 && Shape.IsMatch(code);

    public static string? Message(string code) => code switch
    {
        "pair_no_devices" => "No phone answered for this Google account. Open Google Messages on your phone, check it uses this account, and try again.",
        "pair_phone_not_responding" => "Your phone didn't answer. Open Google Messages on it, keep it open, and try again.",
        "pair_multiple_devices" => "More than one phone uses this Google account in Google Messages. Open Google Messages on the one you want, keep it open, and try again.",
        "pair_no_permission" => "Google didn't allow pairing for this account. Check that Google Messages on your phone uses it.",
        "pair_wrong_emoji" => "The emoji tapped on your phone didn't match. Try again and tap the one shown here.",
        "pair_cancelled" => "Pairing was cancelled on your phone.",
        "pair_timeout" => "Your phone didn't confirm in time. Try again, and tap the emoji when Google Messages asks.",
        // Client attestation (codes 32, 33, 35): the phone's Google Messages checks the pairing app in a way this app cannot
        // pass yet (mautrix/gmessages#91). Retrying cannot help, and nothing here is the user's doing. The library reports
        // these as an unknown error, which read as "did not finish connecting... try again" until 0.1.306.
        _ when code.StartsWith("pair_attestation_", StringComparison.Ordinal) =>
            $"Google Messages on your phone now asks for a check Messages can't pass (code {code[(code.LastIndexOf('_') + 1)..]}), so a new pairing can't finish.",
        // The phone's own timeouts (codes 2, 3 and 6): the emoji step ran out or arrived late.
        _ when code.StartsWith("pair_refused_", StringComparison.Ordinal) =>
            $"Your phone didn't finish pairing in time (code {code["pair_refused_".Length..].Replace('_', '/')}). Keep Google Messages open on your phone, tap the emoji when it asks, and try again.",
        _ when code.StartsWith("pair_failed_code_", StringComparison.Ordinal) =>
            $"Google Messages on your phone refused this pairing (code {code["pair_failed_code_".Length..]}). Check that your phone runs the regular version of Google Messages, then try again.",
        _ when code.StartsWith("pair_start_google_", StringComparison.Ordinal) || code.StartsWith("pair_finish_google_", StringComparison.Ordinal) =>
            $"Google refused the pairing (error {code[(code.LastIndexOf('_') + 1)..]}). Try again in a few minutes.",
        _ => null,
    };
}

// Which phone device and Google account a saved pairing belongs to, and so which device this computer's local state
// is filed under.
internal static class GooglePairingIdentity
{
    // The device id as the session file writes it (its raw JSON text, which the profile has always hashed), and the
    // Google account it belongs to.
    public static (string? Device, string? Account) Identity(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object || !session.TryGetProperty("mobile", out var mobile) || mobile.ValueKind != JsonValueKind.Object) return (null, null);
        var device = mobile.TryGetProperty("userID", out var user) ? user.GetRawText() : null;
        var account = mobile.TryGetProperty("sourceID", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null;
        return (device, string.IsNullOrEmpty(account) ? null : account);
    }

    // The device the profile is named after: the one kept for this account, else the paired one. A device kept for
    // another account never applies.
    public static string ProfileDevice(string device, string? account, (string Device, string Account)? kept) =>
        kept is { } k && account is not null && k.Account == account ? k.Device : device;

    // Whether a new pairing should keep filing local state under the old device: the same account on a new device id.
    public static bool KeepsOldDevice(string? oldDevice, string? oldAccount, string? newDevice, string? newAccount) =>
        oldDevice is not null && oldAccount is not null && oldAccount == newAccount && oldDevice != newDevice;
}
