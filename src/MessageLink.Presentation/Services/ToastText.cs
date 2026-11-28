using System.Text;
using System.Xml;

namespace MessageLink.Presentation.Services;

// A notification is XML to Windows, and a character XML 1.0 does not allow (a control, U+FFFE, half a surrogate pair) in a
// sender's name or words makes the whole notification fail to load. Such characters are dropped from everything shown.
public static class ToastText
{
    public static string XmlSafe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        StringBuilder? kept = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            { kept?.Append(character).Append(value[index + 1]); index++; continue; }
            if (XmlConvert.IsXmlChar(character)) { kept?.Append(character); continue; }
            kept ??= new StringBuilder(value.Length).Append(value, 0, index);
        }
        return kept?.ToString() ?? value;
    }
}
