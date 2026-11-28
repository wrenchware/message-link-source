using System.Text.Json;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleVoice;

internal sealed record VoiceContact(string Number, string Name, string? Avatar = null, string? AvatarUrl = null);

// New chat for Google Voice: the account's Google contacts, people from its recent conversations, or a typed number.
// Starting a conversation asks nothing of Google. A one-to-one Voice conversation is "t." and the other person's number,
// and Google creates it when the first message is sent to it; until then it opens empty.
internal sealed partial class GoogleVoiceBackend : IDirectChatLookup
{
    private static readonly TimeSpan ContactsFresh = TimeSpan.FromMinutes(30);
    private readonly SemaphoreSlim contactsGate = new(1, 1);
    private readonly Dictionary<string, (VoiceContact[] Contacts, DateTimeOffset At)> contactLists = new(StringComparer.Ordinal);

    public Task<ContactOptions> GetContactOptionsAsync(string accountId) =>
        Task.FromResult(GoogleVoiceModels.AccountKey(accountId) is { } key && accounts.Accounts.Any(account => account.Key == key && account.IsConnected)
            ? new ContactOptions(true, true, "Choose a contact or enter a phone number.")
            : new ContactOptions(false, false, "Connect this Google Voice account in Settings first."));

    public async Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken)
    {
        if (GoogleVoiceModels.AccountKey(accountId) is not { } key || string.IsNullOrWhiteSpace(query) || query.Length > 1000) return [];
        var account = accounts.Accounts.FirstOrDefault(candidate => candidate.Key == key && candidate.IsConnected)
            ?? throw new BackendException(BackendFailure.Unavailable, "Connect this Google Voice account in Settings first.");
        VoiceContact[] contacts = [];
        await contactsGate.WaitAsync(cancellationToken);
        try
        {
            var known = contactLists.TryGetValue(key, out var list);
            if (known) contacts = list.Contacts;
            if (!known || DateTimeOffset.UtcNow - list.At > ContactsFresh)
            {
                try
                {
                    var result = await account.CallAsync("ui.contacts", null, cancellationToken);
                    contacts = result.Deserialize<VoiceContact[]>(Json) ?? [];
                    contactLists[key] = (contacts, DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException) { throw; }
                // A typed number and people from recent conversations still work without the contact list.
                catch (Exception) when (known || GoogleVoiceModels.Number(query) is not null || chats.Values.Any(chat => chat.AccountId == accountId)) { }
            }
        }
        finally { contactsGate.Release(); }
        var recent = chats.Values.Where(chat => chat.AccountId == accountId && chat.Type == "single" && chat.Participants.Count == 1)
            .Select(chat => new VoiceContact(PersonNumber(key, chat.Participants[0].Id) ?? "", chat.Participants[0].DisplayName,
                AvatarUrl: chat.Participants[0].AvatarUrl))
            .Where(person => GoogleVoiceModels.Number(person.Number) is not null);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var digits = new string(query.Where(char.IsAsciiDigit).ToArray());
        var numeric = digits.Length > 0 && query.All(ch => char.IsAsciiDigit(ch) || " +()-.".Contains(ch));
        var found = contacts.Concat(recent)
            .Where(person => terms.All(term => person.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                numeric && new string(person.Number.Where(char.IsAsciiDigit).ToArray()).Contains(digits, StringComparison.Ordinal))
            .DistinctBy(person => person.Number)
            .OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase).Take(50)
            .Select(person => new ContactPerson(accountId, person.Number, person.Name, null, person.Number, null, false,
                AvatarUrl: person.AvatarUrl ?? GoogleVoiceModels.AvatarForContact(key, person.Avatar))).ToList();
        // An explicitly entered number, not a claim that it is a saved contact.
        if (GoogleVoiceModels.Number(query) is { } typed && !found.Any(person => person.Phone == typed))
            found.Insert(0, new ContactPerson(accountId, typed, typed, null, typed, null, false));
        return found;
    }

    private static string? PersonNumber(string key, string participantId) =>
        GoogleVoiceModels.Parse(participantId) is { } id && id.Key == key && id.Id.StartsWith("person:", StringComparison.Ordinal) ? id.Id["person:".Length..] : null;

    public ChatSummary? FindDirectChat(ContactPerson contact)
    {
        if (GoogleVoiceModels.AccountKey(contact.AccountId) is not { } key || GoogleVoiceModels.Number(contact.Phone ?? contact.Id) is not { } number) return null;
        return chats.Values
            // Only a text conversation: call and voicemail history with the same person cannot be texted into.
            .Where(chat => chat.AccountId == contact.AccountId && chat.Type == "single" && chat.Participants.Count == 1 &&
                GoogleVoiceModels.Parse(chat.Id) is { } id && id.Id.StartsWith("t.", StringComparison.Ordinal) &&
                GoogleVoiceModels.Number(PersonNumber(key, chat.Participants[0].Id)) == number)
            .OrderByDescending(chat => chat.LastActivity ?? DateTimeOffset.MinValue).FirstOrDefault();
    }

    public Task<ChatSummary> StartDirectAsync(ContactPerson contact)
    {
        if (GoogleVoiceModels.AccountKey(contact.AccountId) is not { } key || contact.CannotMessage || contact.IsSelf ||
            GoogleVoiceModels.Number(contact.Phone ?? contact.Id) is not { } number)
            return Task.FromException<ChatSummary>(new SendException(false, "Choose a contact with a phone number, or enter one."));
        if (FindDirectChat(contact) is { } existing) return Task.FromResult(existing);
        if (!accounts.Accounts.Any(account => account.Key == key && account.IsConnected))
            return Task.FromException<ChatSummary>(new SendException(false, "Connect this Google Voice account in Settings first."));
        var person = new ChatParticipant(GoogleVoiceModels.Qualify(key, "person:" + number), string.IsNullOrWhiteSpace(contact.Name) ? GoogleVoiceModels.Shown(number) : contact.Name,
            contact.AvatarUrl, Phone: number);
        var chat = new ChatSummary(GoogleVoiceModels.Qualify(key, "t." + number), contact.AccountId, person.DisplayName, GoogleVoiceModels.Network,
            "single", null, 0, false, false, false, false, contact.AvatarUrl, null, [], null, null, GoogleVoiceModels.Capabilities, [person], 1);
        chats[chat.Id] = chat;
        GoogleVoiceLog.Write($"start conversation account={GoogleVoiceLog.Tag(key)}");
        return Task.FromResult(chat);
    }
}
