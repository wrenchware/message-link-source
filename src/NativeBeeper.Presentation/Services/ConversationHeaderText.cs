using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

public static class ConversationHeaderText
{
    // Direct chats show the other person's number, or their username where the service has none; groups their size. The
    // service and route are the subtitle's tooltip (OnlyService), so they are named in words only when there is nothing else to say.
    public static string Subtitle(ChatSummary chat, string? transport)
    {
        var parts = new List<string>();
        if (chat.MemberChatIds.Count > 0) parts.Add($"{chat.MemberChatIds.Count} conversations");
        else if (chat.Type == "group") parts.Add($"{GroupSize(chat)} people");
        else if ((DirectNumber(chat) ?? DirectHandle(chat)) is { } who && who != chat.Title) parts.Add(who);
        if (parts.Count == 0) parts.Add(ServiceName(chat.Network, transport));
        if (chat.IsReadOnly) parts.Add("Read-only");
        return string.Join(" · ", parts);
    }

    // Google's rows for a group include this account, rows the phone never shows and a person again under a second
    // identity, so counting them said 7 people beside Details' four (owner, 2026-11-05). Google's size is the people
    // Details lists, once per number, and this account. Other services keep their count, which can exceed the rows sent.
    private static int GroupSize(ChatSummary chat)
    {
        if (chat.Network.Equals("Google Messages", StringComparison.OrdinalIgnoreCase)
            && MergeSuggestions.Counterparts(chat).Select(p => PhoneKey(p.Phone!)).Distinct(StringComparer.Ordinal).Count() is > 0 and var others)
            return others + 1;
        return Math.Max(chat.ParticipantCount, chat.Participants.Count);
    }

    // A merged conversation's header: the person's number, an RCS one first (owner, 2026-10-26), and how many more there
    // are; the link badge beside it counts the linked conversations (LinkedCount). The tooltip says how many conversations
    // it links and their services by name, and every number when there are several. It used to count distinct networks, so a second
    // conversation on a service already linked (another account, or the person's other number) was listed in Details but
    // left the header a number short. A Voice person's texts, calls and voicemail on one account are one conversation
    // there and one here.
    public static (string Subtitle, string Detail) Merged(IReadOnlyList<ChatSummary> members, Func<ChatSummary, string> serviceLabel, string? onlyLabel)
    {
        var count = LinkedCount(members);
        var linked = $"{count} linked {(count == 1 ? "conversation" : "conversations")}";
        var numbers = Numbers(members);
        var lead = numbers.Count switch
        {
            0 => members.Select(DirectHandle).OfType<string>().FirstOrDefault() ?? linked,
            1 => numbers[0],
            var n => $"{numbers[0]} +{n - 1} {(n == 2 ? "number" : "numbers")}"
        };
        var detail = linked + " · " + string.Join(" · ", members.Select(serviceLabel).Distinct());
        if (numbers.Count > 1) detail += "\n" + string.Join("\n", numbers);
        return (onlyLabel is null ? lead : $"{onlyLabel} only · {lead}", detail);
    }
    /// <summary>
    /// How many conversations a merge links; the header's link badge shows it. Each is a conversation of its own, so a
    /// person's RCS and SMS threads at one number count two where Details' Accounts has one row for them; a merge of
    /// two must never read as one and lose its badge.
    /// </summary>
    public static int LinkedCount(IEnumerable<ChatSummary> members) => members.Select(LinkedKey).Distinct(StringComparer.Ordinal).Count();
    private static string LinkedKey(ChatSummary chat)
    {
        if (!chat.Network.Equals("Google Voice", StringComparison.OrdinalIgnoreCase)) return "chat|" + chat.Id;
        var people = chat.Participants.Where(p => p.IsSelf != true).Select(p => p.Id).Order(StringComparer.Ordinal).ToArray();
        return people.Length == 0 ? "chat|" + chat.Id : "voice|" + chat.AccountId + "|" + string.Join(",", people);
    }

    // The person's numbers across a merge's conversations, once each: an RCS conversation's first, then Google's others,
    // then Voice's, then any other service's.
    private static IReadOnlyList<string> Numbers(IEnumerable<ChatSummary> members) => members
        .Where(c => c.MemberChatIds.Count == 0 && c.Type != "group")
        .OrderBy(c => c.Network.Equals("Google Messages", StringComparison.OrdinalIgnoreCase)
            ? ComposeTransport.BubbleFamily(c.ExpectedTransport) == "RCS" ? 0 : 1
            : c.Network.Equals("Google Voice", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
        .Select(DirectNumber).OfType<string>().DistinctBy(PhoneKey).ToArray();

    /// <summary>
    /// The one service a conversation is reached through, as the subtitle's tooltip names it ("RCS", "Messenger"); null
    /// when there are several, which the link badge counts instead. Google's are told apart by route.
    /// </summary>
    public static string? OnlyService(IEnumerable<ChatSummary> sources) =>
        sources.Select(c => ServiceName(c.Network, c.ExpectedTransport)).Distinct(StringComparer.Ordinal).ToArray() is [var only] ? only : null;

    // Google by its route where one is expected ("RCS", "SMS"), every other service by its name.
    private static string ServiceName(string network, string? transport) =>
        network.Equals("Google Messages", StringComparison.OrdinalIgnoreCase) && ComposeTransport.BubbleFamily(transport) is { } route ? route : network;

    // The counterpart's number on its own, for copying it. The subtitle joins it with the read-only mark, so reading the
    // number back out of that string would be parsing our own presentation.
    public static string? Number(ChatSummary chat) =>
        chat.MemberChatIds.Count > 0 || chat.Type == "group" ? null : DirectNumber(chat);

    // A direct conversation is not always one participant row: Google Messages lists hidden rows and can
    // repeat a person under a second identity, so requiring exactly one row left an RCS header with no
    // number at all. A provider-supplied number decides when there is one; the identifier is only a
    // fallback for providers that supply none. Rows that disagree stay unprinted rather than guessed.
    private static string? DirectNumber(ChatSummary chat)
    {
        if (chat.MemberChatIds.Count > 0 || chat.Type == "group") return null;
        var others = chat.Participants.Where(p => p.IsSelf != true).ToArray();
        return Only(others.Select(p => Phone(p.Phone))) ?? Only(others.Select(p => Phone(p.Id)));
    }
    // The counterpart's @handle where the service has no number; Send using tells two routes apart by it.
    public static string? Handle(ChatSummary chat) => DirectHandle(chat);
    // What Send using tells two routes to one number apart by: Google's RCS and SMS threads by route, a Messenger chat
    // and its encrypted one by "Encrypted". Null where the service says neither.
    public static string? RouteKind(ChatSummary chat) =>
        chat.Network.Equals("Google Messages", StringComparison.OrdinalIgnoreCase) && ComposeTransport.BubbleFamily(chat.ExpectedTransport) is { } route
            ? route : chat.IsEncrypted == true ? "Encrypted" : null;
    // A Messenger or Instagram person has a handle where Google has a number; one the rows agree on, as for numbers.
    private static string? DirectHandle(ChatSummary chat)
    {
        if (chat.MemberChatIds.Count > 0 || chat.Type == "group") return null;
        return Only(chat.Participants.Where(p => p.IsSelf != true)
            .Select(p => p.Username?.Trim().TrimStart('@') is { Length: > 0 } handle ? "@" + handle : null));
    }
    private static string? Only(IEnumerable<string?> values)
        => values.OfType<string>().Distinct(StringComparer.Ordinal).ToArray() is [var single] ? single : null;
    private static string PhoneKey(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length == 11 && digits[0] == '1' ? digits[1..] : digits;
    }

    // North American numbers read as (202) 555-0147; other numbers are shown as provided.
    public static string? Phone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsLetter)) return null;
        var trimmed = value.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        // An account id is all digits too, and far longer than any number written without a country code. Printing one
        // under a conversation title reads as a phone number, which it is not.
        if (!trimmed.StartsWith('+') && digits.Length > 11) return null;
        if (digits.Length == 11 && digits[0] == '1' && (trimmed.StartsWith("+1", StringComparison.Ordinal) || !trimmed.StartsWith('+'))) digits = digits[1..];
        else if (trimmed.StartsWith('+')) return trimmed;
        return digits.Length == 10 ? $"({digits[..3]}) {digits[3..6]}-{digits[6..]}" : trimmed;
    }
}
