using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

/// <summary>
/// Whether a person is already in a Messenger group, as a pure function of the conversation's people and the person.
/// It lives in Core so a client of any host answers it itself: asking the host would be a round trip for something the
/// conversation summary it already holds says. The harness checks it against the backend's own naming (<c>MessengerModels</c>).
/// </summary>
public static class MessengerGroupMembership
{
    private const string ChatPrefix = "messenger:", PersonPrefix = "fb:";

    /// <summary>Whether the id names a Messenger conversation. Another service's conversation has no Messenger members.</summary>
    public static bool Owns(string chatId) => chatId.StartsWith(ChatPrefix, StringComparison.Ordinal);

    public static bool IsMember(ChatSummary chat, ContactPerson person) => Owns(chat.Id) && HasMember(chat, person);

    // Search answers with Facebook's own id; a conversation's people carry it as "fb:" and that id.
    public static bool HasMember(ChatSummary chat, ContactPerson person) =>
        chat.Participants.Any(member => member.Id == (string.IsNullOrEmpty(person.Id) ? "" : PersonPrefix + person.Id));
}
