namespace NativeBeeper.Presentation.Services;

/// <summary>
/// How well a typed name or number matches somebody, shared by New chat and the console so one input ranks the same
/// person the same way in both. Moved unchanged from New chat, which is the proof: its checks still pass.
/// </summary>
public static class NameMatch
{
    public static NewChatRank? Rank(string name, string? phone, string text)
    {
        var digits = Digits(text);
        if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) return NewChatRank.ExactLocal;
        // A number typed in full is an exact match however it was punctuated, and whether or not the country code
        // was typed — the last ten digits are what identifies a number everywhere else in this app.
        if (digits.Length >= 7 && PhoneKey(phone) == PhoneKey(digits)) return NewChatRank.ExactLocal;
        if (name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return NewChatRank.PrefixLocal;
        // Any word of the name, so "okafor" finds "Sam Okafor" as strongly as "sam" does.
        if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(word => word.StartsWith(text, StringComparison.OrdinalIgnoreCase))) return NewChatRank.PrefixLocal;
        if (name.Contains(text, StringComparison.OrdinalIgnoreCase)) return NewChatRank.ExistingConversation;
        if (digits.Length >= 3 && Digits(phone).Contains(digits, StringComparison.Ordinal)) return NewChatRank.ExistingConversation;
        return null;
    }

    public static string Digits(string? value) =>
        string.IsNullOrEmpty(value) ? "" : new string([.. value.Where(char.IsDigit)]);

    // The same rule the rest of the app matches numbers by: the last ten digits, so a country code typed or left
    // off does not make one number into two.
    public static string PhoneKey(string? value)
    {
        var digits = Digits(value);
        return digits.Length > 10 ? digits[^10..] : digits;
    }
}
