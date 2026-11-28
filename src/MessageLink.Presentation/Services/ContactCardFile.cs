using System.Text;

namespace MessageLink.Presentation.Services;

// A shared contact (a .vcf file), read far enough to show who it is: the name, numbers, email addresses, company and
// picture. vCard 2.1, 3.0 and 4.0 all arrive in practice. Older phones fold long lines and write names in
// quoted-printable; newer ones write tel: and data: URIs. Each of those is undone here. Only the first card in a file
// is described, and any others are counted.
public sealed record ContactCardFile(string Name, IReadOnlyList<string> Phones, IReadOnlyList<string> Emails,
    string? Organization, byte[]? Photo, int Count)
{
    // Read to show the contact, not to keep it. A card with a photo is tens of kilobytes; anything past this is not
    // a contact card worth downloading unasked.
    public const long ReadLimit = 256 * 1024;
    private const int PhotoLimit = 512 * 1024;

    public static bool Handles(string? name, string? mime) => AttachmentLabel.Category(name, mime) == FileCategory.Contact;

    public static ContactCardFile? Parse(byte[] bytes)
    {
        var text = new UTF8Encoding(false, false).GetString(bytes);
        return Parse(text.StartsWith((char)0xFEFF) ? text[1..] : text);
    }

    public static ContactCardFile? Parse(string text)
    {
        string? formatted = null, structured = null, organization = null;
        byte[]? photo = null;
        var phones = new List<string>();
        var emails = new List<string>();
        var cards = 0;
        var inFirst = false;
        foreach (var line in Unfold(text))
        {
            var colon = ValueStart(line);
            if (colon <= 0) continue;
            var head = line[..colon].Split(';');
            var property = head[0];
            var dot = property.LastIndexOf('.');
            if (dot >= 0) property = property[(dot + 1)..];
            property = property.Trim().ToUpperInvariant();
            var value = line[(colon + 1)..];
            if (property == "BEGIN" && value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase)) { cards++; inFirst = cards == 1; continue; }
            if (property == "END" && value.Trim().Equals("VCARD", StringComparison.OrdinalIgnoreCase)) { inFirst = false; continue; }
            if (!inFirst) continue;
            var parameters = head.Skip(1).Select(p => p.Trim()).ToArray();
            string Decoded() => Parameter(parameters, "ENCODING") is { } encoding && encoding.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase)
                || parameters.Any(p => p.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
                ? QuotedPrintable(value, Parameter(parameters, "CHARSET")) : value;
            switch (property)
            {
                case "FN": formatted ??= Clean(Unescape(Decoded())); break;
                case "N": structured ??= StructuredName(Decoded()); break;
                case "ORG": organization ??= Clean(Unescape(SplitComponents(Decoded()).FirstOrDefault() ?? "")); break;
                case "TEL":
                    var number = Clean(Unescape(Decoded()));
                    if (number?.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) == true) number = Clean(number[4..]);
                    if (number is not null && !phones.Contains(number)) phones.Add(number);
                    break;
                case "EMAIL":
                    var email = Clean(Unescape(Decoded()));
                    if (email?.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) == true) email = Clean(email[7..]);
                    if (email is not null && !emails.Contains(email, StringComparer.OrdinalIgnoreCase)) emails.Add(email);
                    break;
                case "PHOTO": photo ??= DecodePhoto(parameters, value); break;
            }
        }
        var name = formatted ?? structured ?? organization ?? phones.FirstOrDefault() ?? emails.FirstOrDefault();
        return cards == 0 || name is null ? null : new(name, phones, emails, organization, photo, cards);
    }

    // RFC 6350 folds a long line by starting the next with a space or tab. vCard 2.1 quoted-printable ends a broken
    // line with "=" instead, and the next line starts in column one.
    private static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        var softBreak = false;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (current.Length > 0 && (softBreak || raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t')))
            {
                if (softBreak) current.Length--;
                current.Append(softBreak ? raw : raw[1..]);
            }
            else
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear().Append(raw);
            }
            var line = current.ToString();
            softBreak = line.EndsWith('=') && line.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase) && ValueStart(line) > 0;
        }
        if (current.Length > 0) yield return current.ToString();
    }

    // The value begins after the first colon that is not inside a quoted parameter (vCard 4 quotes TYPE lists).
    private static int ValueStart(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) return i;
        }
        return -1;
    }

    private static string? Parameter(string[] parameters, string name) =>
        parameters.Select(p => p.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))?[1].Trim().Trim('"');

    private static string QuotedPrintable(string value, string? charset)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            { bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16)); i += 2; }
            else if (value[i] < 0x80) bytes.Add((byte)value[i]);
            else bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }
        Encoding encoding;
        try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException) { encoding = Encoding.UTF8; }
        return encoding.GetString(bytes.ToArray());
    }

    // Family;Given;Additional;Prefix;Suffix, written the way a person's name is read.
    private static string? StructuredName(string value)
    {
        var parts = SplitComponents(value).Select(p => Clean(Unescape(p))).ToArray();
        string? Part(int index) => index < parts.Length ? parts[index] : null;
        var ordered = new[] { Part(3), Part(1), Part(2), Part(0), Part(4) }.Where(p => p is not null);
        return Clean(string.Join(" ", ordered));
    }

    // Components are separated by semicolons that are not escaped.
    private static IEnumerable<string> SplitComponents(string value)
    {
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\') { i++; continue; }
            if (value[i] != ';') continue;
            yield return value[start..i];
            start = i + 1;
        }
        yield return value[start..];
    }

    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 == value.Length) { result.Append(value[i]); continue; }
            var next = value[++i];
            result.Append(next is 'n' or 'N' ? '\n' : next);
        }
        return result.ToString();
    }

    private static string? Clean(string value)
    {
        var trimmed = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static byte[]? DecodePhoto(string[] parameters, string value)
    {
        string encoded;
        var data = value.Trim();
        if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = data.IndexOf(',');
            if (comma < 0 || !data[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
            encoded = data[(comma + 1)..];
        }
        else if (Parameter(parameters, "ENCODING") is { } encoding && (encoding.Equals("b", StringComparison.OrdinalIgnoreCase) || encoding.Equals("BASE64", StringComparison.OrdinalIgnoreCase))
            || parameters.Any(p => p.Equals("BASE64", StringComparison.OrdinalIgnoreCase)))
            encoded = data;
        else return null; // A picture at a web address would mean fetching it from wherever the card points.
        var compact = string.Concat(encoded.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length / 4 * 3 > PhotoLimit) return null;
        try { return Convert.FromBase64String(compact); }
        catch (FormatException) { return null; }
    }
}
