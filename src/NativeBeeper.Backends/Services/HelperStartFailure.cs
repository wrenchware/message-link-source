namespace NativeBeeper.App.Services;

// Why Windows would not start one of the helper processes, said to someone who did not build Messages. A helper missing
// from the install, or one that antivirus, Smart App Control or a policy refused to run, used to reach the person as a
// build instruction, as "check your phone connection", or as a sign-in that could not finish; and the logs kept only
// the exception's type, so none of those could be told apart afterwards. Process.Start's own message names the full
// path, so only the Win32 number is ever written down (win32=<n>), never the message.
internal static class HelperStartFailure
{
    public const string Missing = "helper_missing";
    public const string Blocked = "helper_blocked";
    public const string Failed = "helper_start_failed";

    public const string MissingSentence = "Part of Messages is missing. Reinstall Messages.";
    public const string BlockedSentence = "Windows security blocked part of Messages. Allow it in Windows Security, or reinstall Messages.";
    public const string FailedSentence = "Part of Messages couldn't start. Restart your PC, or reinstall Messages.";
    // The app and a helper from different versions of Messages, which only a mixed or damaged install produces.
    public const string OutOfDateSentence = "Part of Messages is out of date. Reinstall Messages.";

    // ERROR_ACCESS_DENIED, ERROR_VIRUS_INFECTED (antivirus), ERROR_ACCESS_DISABLED_BY_POLICY (AppLocker or a software
    // restriction policy) and ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION (Smart App Control, App Control for Business).
    private static readonly int[] BlockedCodes = [5, 225, 1260, 4551];
    // ERROR_FILE_NOT_FOUND and ERROR_PATH_NOT_FOUND: gone between the existence check and the start, or never checked.
    private static readonly int[] MissingCodes = [2, 3];

    // The Win32 error a refused start carried, or null for anything that is not a refused start.
    public static int? Win32Code(Exception failure) =>
        failure is System.ComponentModel.Win32Exception refused ? refused.NativeErrorCode : null;

    // helper_blocked, helper_missing or helper_start_failed for a start Windows refused; null otherwise.
    public static string? Code(Exception failure) => Win32Code(failure) is { } code
        ? BlockedCodes.Contains(code) ? Blocked : MissingCodes.Contains(code) ? Missing : Failed
        : null;

    // What the person is told for one of these codes, or null when the code is not about starting a helper.
    public static string? Sentence(string? code) => code switch
    {
        Missing => MissingSentence,
        Blocked => BlockedSentence,
        Failed => FailedSentence,
        "unsupported_protocol" => OutOfDateSentence,
        _ => null,
    };

    public static string? Sentence(Exception failure) => Sentence(Code(failure));

    // For a log line: the code and the number, both fixed words.
    public static string Fields(Exception failure) =>
        $"reason={Code(failure) ?? "none"} win32={Win32Code(failure)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}";
}
