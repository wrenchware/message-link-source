using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

// Local photos follow a person across their one-to-one conversations. Groups and merged rows keep a
// conversation-specific identity because there is no single contact to own their picture.
public static class AvatarOverrideIdentity
{
    public static string ForChat(ChatSummary chat)
    {
        if (chat.Type != "group" && chat.MemberChatIds.Count == 0 &&
            chat.Participants.Where(person => person.IsSelf != true && !string.IsNullOrWhiteSpace(person.Id)).ToArray() is [var contact])
            return ForContact(chat.Network, contact.Id);
        return "chat:" + chat.Id;
    }

    public static string ForParticipant(ChatSummary chat, string participantId) =>
        ForContact(chat.Network, participantId);

    private static string ForContact(string network, string participantId) =>
        "contact:" + network.ToLowerInvariant() + ":" + participantId;
}
