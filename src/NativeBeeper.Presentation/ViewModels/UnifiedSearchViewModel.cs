using NativeBeeper.Core.Services;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Backend;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public enum UnifiedSearchRowKind { Info, Header, Conversation, Message, Contact }

public sealed record UnifiedSearchRow(string Title, string Detail = "", SearchHit? Hit = null, ContactPerson? Contact = null)
{
    public UnifiedSearchRowKind Kind { get; init; } = Hit is { Message: not null } ? UnifiedSearchRowKind.Message
        : Hit is not null ? UnifiedSearchRowKind.Conversation : Contact is not null ? UnifiedSearchRowKind.Contact : UnifiedSearchRowKind.Info;
    public string Time { get; init; } = "";
    public string Query { get; init; } = "";
    public bool IsSelectable => Hit is not null || Contact is { CannotMessage: false };
    public string? AvatarChatId => Hit?.Chat.Id;
    public string? AvatarOverrideId => Hit is null ? null : AvatarOverrideIdentity.ForChat(Hit.Chat);
    public string? AvatarUrl => Hit?.Chat.AvatarUrl;
    public string? AvatarColor => Hit?.Chat.AvatarColor;
}

// Header search: conversations first (name matches), then message matches, then contacts, which can
// be slowest and are appended last so rows above them never move. A scope/indexing footer ends the list.
// Services that search over the network (Messenger, Instagram) are held to New chat's rules (RemoteSearchRules) through
// remoteCache, which the caller keeps between searches; without one they are not asked at all.
public sealed class UnifiedSearchViewModel(ISearchBackend searches, IContactBackend contacts, RemoteContactCache? remoteCache = null)
{
    private const int ConversationLimit = 4, MessageLimit = 12, ContactLimit = 4;
    // Give fast message searches one paint with their conversations; slow searches must not hide an already found chat.
    public TimeSpan ConversationRevealDelay { get; init; } = TimeSpan.FromMilliseconds(120);
    private static UnifiedSearchRow Header(string title) => new(title) { Kind = UnifiedSearchRowKind.Header };
    private static string PhoneKey(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    public async Task<IReadOnlyList<UnifiedSearchRow>> SearchAsync(string query, IReadOnlyList<ChatAccount> accounts, CancellationToken token,
        Func<IReadOnlyList<UnifiedSearchRow>, Task>? publish = null)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000) return [];
        var text = query.Trim();
        var now = DateTimeOffset.Now;

        async Task<List<UnifiedSearchRow>> ConversationsAsync()
        {
            try
            {
                var page = await searches.SearchAsync(new(text, SearchKind.Conversations), cancellationToken: token);
                var rows = page.Items.Where(hit => hit.Message is null).Take(ConversationLimit).Select(hit => new UnifiedSearchRow(hit.Chat.Title,
                    new ChatItemViewModel(hit.Chat).Preview, hit) { Time = ChatItemViewModel.RelativeTime(hit.Chat.LastActivity, now), Query = text }).ToList();
                if (rows.Count > 0) rows.Insert(0, Header("Conversations"));
                return rows;
            }
            catch (OperationCanceledException) { throw; }
            catch { return [new("Conversations unavailable")]; }
        }

        async Task<List<UnifiedSearchRow>> MessagesAsync()
        {
            try
            {
                var page = await searches.SearchAsync(new(text, SearchKind.Messages), cancellationToken: token);
                var matches = page.Items.Where(hit => hit.Message is { IsHidden: false, IsDeleted: false }).ToArray();
                var rows = matches.Take(MessageLimit).Select(hit =>
                {
                    var message = hit.Message!;
                    var body = MessageText.PlainText(message.Text ?? "");
                    if (string.IsNullOrWhiteSpace(body)) body = message.Attachments.Count > 0 ? "Attachment" : "Message";
                    var who = message.IsOutgoing ? "You: " : hit.Chat.Type == "group" && message.SenderName is { Length: > 0 } sender ? sender + ": " : "";
                    return new UnifiedSearchRow(hit.Chat.Title, who + SearchSnippet.Excerpt(body, text), hit)
                        { Time = ChatItemViewModel.RelativeTime(message.Timestamp, now), Query = text };
                }).ToList();
                if (rows.Count == 0) return [];
                rows.Insert(0, Header("Messages"));
                if (page.HasMore || matches.Length > MessageLimit) rows.Add(new("More messages match — add another word to narrow them down"));
                return rows;
            }
            catch (OperationCanceledException) { throw; }
            catch { return [new("Messages unavailable")]; }
        }

        async Task<(List<ContactPerson> People, bool Unavailable)> ContactsAsync()
        {
            var people = new List<ContactPerson>();
            var unavailable = false;
            var quiet = false;
            foreach (var account in accounts)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var options = await contacts.GetContactOptionsAsync(account.Id, token);
                    if (!options.CanSearch) continue;
                    if (options.RemoteSearch)
                    {
                        // A real call against the user's account: only for words worth it, after typing has stopped, never
                        // twice for the same words, and never sooner than the provider's interval.
                        if (remoteCache is null || !RemoteSearchRules.Allows(text)) continue;
                        if (remoteCache.Lookup(account.Id, text, DateTimeOffset.UtcNow) is { } remembered) { people.AddRange(remembered); continue; }
                        if (!quiet) { await Task.Delay(RemoteSearchRules.Idle, token); quiet = true; }
                        if (!remoteCache.MayCall(account.Id, DateTimeOffset.UtcNow)) continue;
                        remoteCache.Called(account.Id, DateTimeOffset.UtcNow);
                        var answered = await contacts.FindContactsAsync(account.Id, text, token);
                        remoteCache.Remember(account.Id, text, answered, DateTimeOffset.UtcNow);
                        people.AddRange(answered);
                        continue;
                    }
                    people.AddRange(await contacts.FindContactsAsync(account.Id, text, token));
                }
                catch (OperationCanceledException) { throw; }
                catch { unavailable = true; }
            }
            return (people, unavailable);
        }

        var contactsTask = ContactsAsync();
        var conversations = await ConversationsAsync();
        token.ThrowIfCancellationRequested();
        var messagesTask = MessagesAsync();
        if (publish is not null && conversations.Count > 0 && !messagesTask.IsCompleted)
        {
            await Task.WhenAny(messagesTask, Task.Delay(ConversationRevealDelay, token));
            token.ThrowIfCancellationRequested();
            if (!messagesTask.IsCompleted) await publish([.. conversations, new("Searching…")]);
        }
        var messages = await messagesTask.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        var footer = searches is ISearchScopeInfo scope ? new UnifiedSearchRow(scope.SearchScopeDescription) : null;
        List<UnifiedSearchRow> Compose(IEnumerable<UnifiedSearchRow> people, bool pending)
        {
            var rows = conversations.Concat(messages).Concat(people).ToList();
            if (pending) rows.Add(new("Looking up contacts…"));
            else if (rows.Count == 0) rows.Add(new($"No results for “{text}”"));
            if (footer is not null) rows.Add(footer);
            return rows;
        }
        if (publish is not null && !contactsTask.IsCompleted) await publish(Compose([], true));
        token.ThrowIfCancellationRequested();

        var found = await contactsTask.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        // People already shown as a direct conversation are not repeated as contacts.
        var known = conversations.Where(row => row.Hit?.Chat is { Type: not "group" })
            .SelectMany(row => row.Hit!.Chat.Participants.Where(person => person.IsSelf != true)).Select(person => PhoneKey(person.Id))
            .Where(key => key.Length > 0).ToHashSet();
        var contactRows = found.People.DistinctBy(person => (person.AccountId, person.Id))
            .Where(person => PhoneKey(person.Phone) is not { Length: > 0 } key || !known.Contains(key)).Take(ContactLimit)
            .Select(person => new UnifiedSearchRow(person.Name, ConversationHeaderText.Phone(person.Phone) ?? person.Username ?? person.Email ?? "Contact", Contact: person)
                { Time = person.CannotMessage ? "" : "New chat", Query = text }).ToList();
        if (contactRows.Count > 0) contactRows.Insert(0, Header("Contacts"));
        if (found.Unavailable) contactRows.Add(new("Some accounts could not be searched"));
        var final = Compose(contactRows, false);
        token.ThrowIfCancellationRequested();
        if (publish is not null) await publish(final);
        return final;
    }
}
