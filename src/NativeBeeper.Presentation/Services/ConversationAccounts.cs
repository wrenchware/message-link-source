using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

// A display projection of the existing source participants, not another identity store. The account/person key
// keeps two Voice accounts distinct while folding that person's text, calls and voicemail into one row.
public sealed record ConversationAccount(string Network, string AccountId, string PersonId, string Service,
    string? DisplayName, string? Username, string? Phone);
public sealed record AccountCopyValue(string Label, string Value);

public static class ConversationAccounts
{
    public static IReadOnlyList<ConversationAccount> Collect(IEnumerable<ChatSummary> sources,
        Func<ChatSummary, string>? serviceLabel = null)
    {
        var rows = new List<ConversationAccount>();
        foreach (var chat in sources.Where(c => c.MemberChatIds.Count == 0))
        {
            var people = MergeSuggestions.Counterparts(chat).ToArray();
            // Google already fills Phone only for its visible/listed counterparts. Hidden RCS rows can include this
            // account without IsSelf, so reading their ids back as numbers exposes our own number as another account.
            if (chat.Network.Equals("Google Messages", StringComparison.OrdinalIgnoreCase))
                people = people.Where(p => Phone(p.Phone) is not null).ToArray();
            if (people.Length == 0)
            {
                rows.Add(new(chat.Network, chat.AccountId, chat.Id, serviceLabel?.Invoke(chat) ?? chat.Network,
                    Text(chat.Title), null, null));
                continue;
            }
            foreach (var person in people)
            {
                var phone = Phone(person.Phone);
                var username = Text(person.Username)?.TrimStart('@');
                username = Text(username) is { } handle ? "@" + handle : null;
                var name = Text(person.DisplayName);
                if (name == "Participant" || name == "Unknown") name = null;
                if (name is not null && (phone is not null && Phone(name) == phone || name.TrimStart('@') == username?.TrimStart('@'))) name = null;
                var id = Text(person.Id) ?? phone ?? username ?? chat.Id;
                var next = new ConversationAccount(chat.Network, chat.AccountId, id, serviceLabel?.Invoke(chat) ?? chat.Network,
                    name, username, phone);
                var index = rows.FindIndex(r => r.Network.Equals(next.Network, StringComparison.OrdinalIgnoreCase) && r.AccountId == next.AccountId &&
                    (r.PersonId == id || phone is not null && r.Phone == phone));
                if (index < 0) rows.Add(next);
                else
                {
                    var prior = rows[index];
                    rows[index] = prior with { DisplayName = prior.DisplayName ?? name, Username = prior.Username ?? username, Phone = prior.Phone ?? phone };
                }
            }
        }
        return rows;
    }

    public static IReadOnlyList<AccountCopyValue> Phones(IReadOnlyList<ConversationAccount> rows) =>
        rows.Where(r => r.Phone is not null).GroupBy(r => PhoneKey(r.Phone!), StringComparer.Ordinal)
            .Select(g => new AccountCopyValue(string.Join(" / ", g.Select(Label).Distinct()) + " · Phone number", g.First().Phone!)).ToArray();

    // Each item shows the value it copies (a name, or a handle such as @someone), led by the services that use it, so
    // one name shared by several services is a single item.
    public static IReadOnlyList<AccountCopyValue> Names(IReadOnlyList<ConversationAccount> rows) =>
        rows.SelectMany(r => new[] { r.DisplayName, r.Username }.OfType<string>().Select(value => (r.Service, Value: value)))
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(g => new AccountCopyValue(string.Join(" / ", g.Select(pair => pair.Service).Distinct()) + " · " + g.Key, g.Key)).ToArray();

    private static string Label(ConversationAccount row) => row.Service + (row.DisplayName is { } name ? " · " + name : row.Username is { } handle ? " · " + handle : "");
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Phone(string? value) => value is not null && value.Any(char.IsDigit) &&
        value.Trim().All(c => char.IsDigit(c) || c is '+' or '(' or ')' or '-' or '.' or ' ') ? ConversationHeaderText.Phone(value) : null;
    private static string PhoneKey(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length == 11 && digits[0] == '1' ? digits[1..] : digits;
    }
}
