using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

/// <summary>One of the header's service dots: the colour family it is drawn in and the name it is read out as.</summary>
public sealed record HeaderDot(BubbleFamily Family, string Name);

public static class ConversationHeaderText
{
    // Direct chats show the other person's number, or their username where the service has none; groups their size. The
    // service and route are the dots beside it (Dots), so they are named in words only when there is nothing else to say.
    public static string Subtitle(ChatSummary chat, string? transport)
    {
        var parts = new List<string>();
        if (chat.MemberChatIds.Count > 0) parts.Add($"{chat.MemberChatIds.Count} conversations");
        else if (chat.Type == "group") parts.Add($"{Math.Max(chat.ParticipantCount, chat.Participants.Count)} people");
        else if ((DirectNumber(chat) ?? DirectHandle(chat)) is { } who && who != chat.Title) parts.Add(who);
        if (parts.Count == 0) parts.Add(ServiceName(chat.Network, transport));
        if (chat.IsReadOnly) parts.Add("Read-only");
        return string.Join(" · ", parts);
    }

    // A merged conversation's header: the person's number, an RCS one first (owner, 2026-10-26), and how many more there
    // are; its services are the dots. The tooltip says how many conversations it links and their services by name, as
    // Details lists them, and every number when there are several. It used to count distinct networks, so a second
    // conversation on a service already linked (another account, or the person's other number) was listed in Details but
    // left the header a number short. A Voice person's texts, calls and voicemail on one account are one conversation
    // there and one here.
    public static (string Subtitle, string Detail) Merged(IReadOnlyList<ChatSummary> members, Func<ChatSummary, string> serviceLabel, string? onlyLabel)
    {
        var count = members.Select(LinkedKey).Distinct(StringComparer.Ordinal).Count();
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
    /// The services a conversation is reached through, one dot each in the colour its sent bubbles have, the one the next
    /// message goes by first. Google's are told apart by route, so a person on RCS and on SMS has two. The service colour
    /// is used even where Settings draws a merged conversation's bubbles in one colour: the dots are there to tell the
    /// services apart.
    /// </summary>
    public static IReadOnlyList<HeaderDot> Dots(IEnumerable<ChatSummary> sources, string? sendChatId) => sources
        .OrderBy(c => c.Id == sendChatId ? 0 : 1)
        .Select(c => new HeaderDot(BubbleColours.TagFamily(c.Network, c.ExpectedTransport), ServiceName(c.Network, c.ExpectedTransport)))
        .DistinctBy(d => d.Family).ToArray();

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
