using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// Somewhere a message could go: one person, on one service. Two services carrying the same name are two
/// destinations, because they are two different conversations with two different histories and two different ways of
/// failing. Nothing here ever merges them.
/// </summary>
/// <param name="Conversation">The conversation that already exists with this person, when there is one. A
/// destination that has one is opened rather than started, which is instant and cannot fail.</param>
public sealed record ChatDestination(string AccountId, string Network, string PersonId, string Name,
    string? AvatarUrl, string? Phone, ChatSummary? Conversation, ContactPerson? Contact = null)
{
    /// <summary>Identity for de-duplication. Deliberately account-scoped: the same person on two services is two.</summary>
    public (string AccountId, string PersonId) Key => (AccountId, PersonId);

    /// <summary>
    /// How this person is named on this service. A phone number where there is one, otherwise whatever that service
    /// identifies people by — Instagram has only a handle, and printing an empty line under the name is worse than
    /// saying what the row actually is.
    /// </summary>
    public string Detail => !string.IsNullOrWhiteSpace(Phone) ? Phone!
        : !string.IsNullOrWhiteSpace(Contact?.Username) ? "@" + Contact!.Username
        : !string.IsNullOrWhiteSpace(Contact?.Email) ? Contact!.Email!
        : Conversation is not null ? "Existing conversation" : Network;

    /// <summary>One line for a screen reader, because the service is part of which row this is.</summary>
    public string Announcement => $"{Name}, {Detail}, {Network}";
}

/// <summary>Where a result came from, which is also the order results are shown in.</summary>
public enum NewChatRank
{
    // Somebody the service itself ranks among the people this account writes to most (Google Messages' top contacts)
    // who matches what is typed. Above everything local, because that ranking is the best evidence of intent there is.
    Frequent = -1,
    ExactLocal = 0,
    PrefixLocal = 1,
    ExistingConversation = 2,
    CachedRemote = 3,
    FreshRemote = 4,
}

public sealed record NewChatResult(ChatDestination Destination, NewChatRank Rank, int Score);

/// <summary>
/// The local half of New chat: who to offer before anything is typed, and who matches what is being typed without
/// asking a provider. Pure and deterministic, so the contract harness can hold it to its own rules and so the same
/// input always draws the same list — a picker that reshuffles under the cursor is worse than a slow one.
/// </summary>
public static class NewChatDirectory
{
    /// <summary>
    /// How much a conversation argues that this is who you are about to write to. Small, fixed and explainable on
    /// purpose: every number here can be pointed at in a list and justified, which is not true of a learned score.
    /// </summary>
    /// <remarks>
    /// Frequency is deliberately absent. Nothing local counts messages — the Google archive stores encrypted
    /// payloads keyed by id, and Instagram keeps no archive at all — so a frequency term would either be invented or
    /// require decrypting the archive on every keystroke. Recency and direction are what the conversation list
    /// actually knows, and having sent to somebody is the strongest of them.
    /// </remarks>
    public static int Score(ChatSummary chat, DateTimeOffset now)
    {
        var age = chat.LastActivity is { } at ? now - at : TimeSpan.MaxValue;
        var score = age switch
        {
            { TotalDays: <= 1 } => 50,
            { TotalDays: <= 3 } => 38,
            { TotalDays: <= 7 } => 28,
            { TotalDays: <= 30 } => 16,
            { TotalDays: <= 90 } => 8,
            _ => 2,
        };
        // Having written to somebody outranks their having written to you, which is what makes this list different
        // from the inbox. The conversation list carries only the newest message's direction, so this is "the last
        // thing said here was mine" rather than a count — weighted heavily enough to beat one step of recency.
        if (chat.Preview is { IsOutgoing: true }) score += 35;
        // Explicit intent: pinning is the one place the user has already said who matters.
        if (chat.IsPinned) score += 25;
        // A conversation waiting on a reply is one you are more likely to be opening New chat about.
        if (chat.UnreadCount > 0 || chat.IsMarkedUnread) score += 10;
        return score;
    }

    /// <summary>
    /// The people most worth offering first. Scored rather than chronological, so this is not another copy of
    /// <see cref="Recent"/>.
    /// </summary>
    public static IReadOnlyList<ChatDestination> Suggested(IReadOnlyList<ChatSummary> chats, DateTimeOffset now, int count = 4,
        IEnumerable<ChatDestination>? except = null)
    {
        var shown = Shown(except);
        return [.. Destinations(chats)
            .Where(pair => !shown(pair.Destination))
            .OrderByDescending(pair => Score(pair.Chat, now))
            .ThenByDescending(pair => pair.Chat.LastActivity ?? DateTimeOffset.MinValue)
            .ThenBy(pair => pair.Destination.AccountId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Destination.PersonId, StringComparer.Ordinal)
            .Take(Math.Max(0, count))
            .Select(pair => pair.Destination)];
    }

    /// <summary>
    /// The most recently active people, in the order they were active, minus anyone already offered above. Plainly
    /// chronological: this is the section that answers "who was I just talking to".
    /// </summary>
    public static IReadOnlyList<ChatDestination> Recent(IReadOnlyList<ChatSummary> chats, int count = 6,
        IEnumerable<ChatDestination>? except = null)
    {
        var shown = Shown(except);
        return [.. Destinations(chats)
            .Where(pair => !shown(pair.Destination))
            .OrderByDescending(pair => pair.Chat.LastActivity ?? DateTimeOffset.MinValue)
            .ThenBy(pair => pair.Destination.AccountId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Destination.PersonId, StringComparer.Ordinal)
            .Take(Math.Max(0, count))
            .Select(pair => pair.Destination)];
    }

    /// <summary>
    /// The people a service itself ranks as the ones this account writes to most (Google Messages' top contacts), in
    /// that service's order, each with the conversation already on this PC when there is one so picking them opens it.
    /// Nobody who cannot be written to, and nobody twice, whether by the same id or by the same conversation.
    /// </summary>
    /// <param name="existing">Finds the one-to-one conversation already here for somebody; a local lookup, never a
    /// request to the service.</param>
    public static IReadOnlyList<ChatDestination> Frequent(IEnumerable<ContactPerson> ranked, string network,
        Func<ContactPerson, ChatSummary?>? existing = null, int count = 8)
    {
        var result = new List<ChatDestination>();
        foreach (var person in ranked)
        {
            if (result.Count >= Math.Max(0, count)) break;
            if (person.CannotMessage || person.IsSelf || string.IsNullOrEmpty(person.Id) || string.IsNullOrWhiteSpace(person.Name)) continue;
            var chat = existing?.Invoke(person);
            // A conversation that is not one to write to in its own right is not offered as the way to this person.
            if (chat is { IsArchived: true } or { IsSpam: true } || chat?.Type == "group") chat = null;
            var destination = new ChatDestination(person.AccountId, network, person.Id, person.Name,
                person.AvatarUrl ?? chat?.AvatarUrl, person.Phone, chat, person);
            if (Shown(result)(destination)) continue;
            result.Add(destination);
        }
        return result;
    }

    // Whether a destination is already on screen: the same person on the same service, or the same conversation reached
    // another way (a phone's ranking names people by number, a conversation by its participant).
    private static Func<ChatDestination, bool> Shown(IEnumerable<ChatDestination>? except)
    {
        var list = (except ?? []).ToArray();
        if (list.Length == 0) return static _ => false;
        var keys = list.Select(destination => destination.Key).ToHashSet();
        var conversations = list.Select(destination => destination.Conversation?.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return destination => keys.Contains(destination.Key)
            || destination.Conversation?.Id is { } id && conversations.Contains(id);
    }

    /// <summary>
    /// Who matches what is being typed, without asking any provider. Everybody already in a conversation is here,
    /// whichever service that conversation belongs to — so somebody written to on Instagram before is found without
    /// Instagram being asked, which is most of what keeps the remote calls rare.
    /// </summary>
    public static IReadOnlyList<NewChatResult> MatchLocal(IReadOnlyList<ChatSummary> chats, string query,
        DateTimeOffset now, int count = 20)
    {
        var text = query.Trim();
        if (text.Length == 0) return [];
        // Ranked by the same rule the console resolves a typed name by, so both find the same person first.
        return [.. Destinations(chats)
            .Select(pair => (pair.Destination, pair.Chat, Rank: NameMatch.Rank(pair.Destination.Name, pair.Destination.Phone, text)))
            .Where(row => row.Rank is not null)
            .Select(row => new NewChatResult(row.Destination, row.Rank!.Value, Score(row.Chat, now)))
            .OrderBy(row => row.Rank)
            .ThenByDescending(row => row.Score)
            .ThenBy(row => row.Destination.AccountId, StringComparer.Ordinal)
            .ThenBy(row => row.Destination.PersonId, StringComparer.Ordinal)
            .Take(Math.Max(0, count))];
    }

    /// <summary>
    /// One destination per one-to-one conversation. Groups are not somebody to write to, and a conversation merged
    /// into another is the same destination twice.
    /// </summary>
    private static IEnumerable<(ChatDestination Destination, ChatSummary Chat)> Destinations(IReadOnlyList<ChatSummary> chats)
    {
        var seen = new HashSet<(string, string)>();
        foreach (var chat in chats)
        {
            if (chat.Type == "group" || chat.IsArchived || chat.IsSpam || chat.MergedIntoChatId is not null) continue;
            // Hermes is nobody to start a conversation with or put in a group (the owner's privacy line, 2026-11-14).
            if (HermesThreads.Is(chat)) continue;
            if (chat.Participants.FirstOrDefault(person => person.IsSelf != true) is not { } person) continue;
            if (string.IsNullOrEmpty(person.Id)) continue;
            var name = string.IsNullOrWhiteSpace(person.DisplayName) ? chat.Title : person.DisplayName;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!seen.Add((chat.AccountId, person.Id))) continue;
            // Somebody already in a conversation is still somebody who can be picked — to be written to, or to be
            // put in a group with. Without this they could only ever be opened, which would leave every person the
            // account already talks to unable to join a group.
            var contact = new ContactPerson(chat.AccountId, person.Id, name, null, person.Phone, null,
                CannotMessage: false, IsSelf: false, AvatarUrl: person.AvatarUrl ?? chat.AvatarUrl);
            yield return (new ChatDestination(chat.AccountId, chat.Network, person.Id, name,
                person.AvatarUrl ?? chat.AvatarUrl, person.Phone, chat, contact), chat);
        }
    }
}
