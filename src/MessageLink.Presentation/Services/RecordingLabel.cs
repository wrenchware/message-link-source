namespace MessageLink.Presentation.Services;

// What a recording calls itself before anybody plays it.
//
// A player shows the running time once it is playing, so this is only ever read while it sits there. A voicemail says
// so. Anything else shows its file name, which is useful when somebody attached a recording they had — and useless
// when the provider's "name" is one of its own identifiers, which is what Google Messages sends for an audio message:
// a bare GUID, drawn where a phone would have shown the length of the recording. A name nobody wrote is not a name.
public static partial class RecordingLabel
{
    public const string Voicemail = "Voicemail", Recording = "Audio message", VoiceMessage = "Voice message", Audio = "Audio";

    public static string Describe(string? name, bool isVoiceNote)
    {
        if (isVoiceNote) return Voicemail;
        var bare = string.IsNullOrWhiteSpace(name) ? "" : Path.GetFileNameWithoutExtension(name).Trim();
        return bare.Length == 0 || IsIdentifier(bare) ? Recording : bare;
    }

    /// <summary>
    /// What a recording alone is called in the list's preview line, a reply's quote and a notification. Messenger and Instagram hand a voice message over
    /// as plain audio named "Voice message" (Messenger's encrypted chats name other audio "Audio"), so a name that reads as words is said as it is. A file's
    /// name ("memo.m4a") or a service's identifier (Google Messages' GUIDs) is not a label: that is "Audio". The phone says the same (DraftRows.audioLabel).
    /// </summary>
    public static string Preview(string? name, bool isVoiceNote)
    {
        if (isVoiceNote) return VoiceMessage;
        var words = name?.Trim() ?? "";
        return words.Length is > 0 and <= LabelLimit && !FileEnding().IsMatch(words) && words.IndexOfAny(['/', '\\']) < 0 && !IsIdentifier(words) ? words : Audio;
    }

    private const int LabelLimit = 40;
    [System.Text.RegularExpressions.GeneratedRegex(@"\.[A-Za-z0-9]{1,5}$")]
    private static partial System.Text.RegularExpressions.Regex FileEnding();

    // Hexadecimal and separators, and long enough that nobody typed it: a GUID with or without its dashes, or the
    // same idea in another shape. Short hex words a person might actually have named a file are left alone. A received
    // file's name is held to the same test (ReceivedFiles.IsPlaceholder).
    internal static bool IsIdentifier(string value)
    {
        if (value.Length < 16) return false;
        var digits = 0;
        foreach (var character in value)
        {
            if (character is '-' or '_') continue;
            if (!Uri.IsHexDigit(character)) return false;
            digits++;
        }
        return digits >= 16;
    }
}
