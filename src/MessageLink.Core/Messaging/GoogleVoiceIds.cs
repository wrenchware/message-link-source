namespace MessageLink.Core.Messaging;

// How Google Voice names things in the shared inbox, and the one decision about them that every client needs: which text
// conversation a call row routes to. Every id carries its account, so several Voice accounts never collide and every call
// reaches the helper that owns the conversation: gvoice:<account key>:<Voice id>.
public static class GoogleVoiceIds
{
    public const string Network = "Google Voice";
    public const string Prefix = "gvoice:";
    public const int KeyLength = 16;
    // Voice reports neither delivery nor reading, so its messages say Sent and nothing after it. Said outright rather
    // than left unknown, so nothing downstream reads a read state into a Voice conversation.
    public static readonly ChatCapabilities Capabilities = ChatCapabilities.Unknown with { ReadReceipts = false };

    public static string Account(string key) => Prefix + key;
    public static string Qualify(string key, string id) => Prefix + key + ":" + id;
    public static (string Key, string Id)? Parse(string? value) => Parse(value, Prefix);
    public static string? AccountKey(string? accountId) =>
        accountId is { Length: > 0 } && accountId.StartsWith(Prefix, StringComparison.Ordinal) && accountId.Length == Prefix.Length + KeyLength
            ? accountId[Prefix.Length..] : null;

    public static (string Key, string Id)? Parse(string? value, string prefix)
    {
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = value.AsSpan(prefix.Length);
        if (rest.Length <= KeyLength + 1 || rest[KeyLength] != ':') return null;
        var key = rest[..KeyLength];
        foreach (var c in key) if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)) return null;
        return (key.ToString(), rest[(KeyLength + 1)..].ToString());
    }

    // A typed or listed number as Voice writes it: +1 and ten digits for a US or Canadian number, or + and a full
    // international number. Short codes and anything else are not numbers New chat can start.
    public static string? Number(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var plus = false;
        var digits = new System.Text.StringBuilder();
        foreach (var ch in input.Trim())
        {
            if (char.IsAsciiDigit(ch)) digits.Append(ch);
            else if (ch == '+' && digits.Length == 0 && !plus) plus = true;
            else if (ch is not (' ' or '-' or '(' or ')' or '.')) return null;
        }
        var value = digits.ToString();
        if (plus) return value.Length is >= 8 and <= 15 ? "+" + value : null;
        if (value.Length == 10) return "+1" + value;
        if (value.Length == 11 && value[0] == '1') return "+" + value;
        return null;
    }

    // The same local t.<number> destination used by New chat. This creates no provider
    // conversation and sends nothing; Voice creates an absent thread only on an explicit send.
    public static ChatSummary? TextRouteForCall(ChatSummary call)
    {
        if (call.Network != Network || call.Type != "single" || call.IsSpam || AccountKey(call.AccountId) is not { } key ||
            Parse(call.Id) is not { } source || source.Key != key || source.Id.StartsWith("t.", StringComparison.Ordinal)) return null;
        var people = call.Participants.Where(p => p.IsSelf != true).ToArray();
        if (people.Length != 1 || Parse(people[0].Id) is not { } person || person.Key != key ||
            !person.Id.StartsWith("person:", StringComparison.Ordinal) || Number(person.Id["person:".Length..]) is not { } number) return null;
        return new(Qualify(key, "t." + number), call.AccountId, call.Title, Network, "single", null, 0,
            false, false, false, false, people[0].AvatarUrl, null, [], null, null, Capabilities, people, 1);
    }
}
