using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services.Messenger;

/// <summary>
/// The rules and words for changing an existing Messenger group (MessengerBackend.Groups.cs), kept apart from the
/// backend so the offline checks can hold them. Everything a change is refused for here is refused before anything is
/// asked of the helper; the helper checks again before it sends (2026-10-04).
/// </summary>
internal static class MessengerGroupChanges
{
    public const string NotHere = "Messenger doesn't let this group be changed from here.";
    public const string OtherService = "This service's groups can't be changed from here.";
    public const string Unknown = "Messenger didn't say whether that change was made. Check the group in Messenger before trying again.";

    // Whether the change may have happened: anything but a refusal made before or by Messenger.
    public static bool Uncertain(string code) => code is not ("change_rejected" or "invalid_input" or "not_allowed" or "unknown_chat" or
        "login_required" or "not_connected");

    public static string Explain(string code) => code switch
    {
        "change_rejected" => "Messenger wouldn't make that change. Nothing in the group changed.",
        "not_allowed" or "unknown_chat" => NotHere,
        "invalid_input" => "Messenger wouldn't accept that change.",
        "not_connected" or "login_required" => "Connect Messenger in Settings first.",
        _ => Unknown,
    };

    /// <summary>The conversation, when the helper offered this change for it; otherwise null and nothing is sent.</summary>
    public static MessengerChat? Offered(string chatId, IEnumerable<MessengerChat> chats, Func<MessengerChat, bool> offered)
    {
        if (!MessengerModels.Owns(chatId)) return null;
        var native = MessengerModels.Native(chatId);
        return chats.FirstOrDefault(chat => chat.Id == native) is { Group: true } known && offered(known) ? known : null;
    }

    public static string? RenameRefusal(string name, int maximum) =>
        name.Length == 0 ? "Enter a group name." : name.Length > maximum ? $"A group name can be up to {maximum} characters." : null;

    public static string? AddRefusal(MessengerChat? known, IReadOnlyList<ContactPerson> people, int maximum)
    {
        var ids = people.Select(person => person.Id).ToArray();
        if (people.Count < 1 || people.Count > maximum || people.Any(person => person.AccountId != MessengerModels.Account || person.CannotMessage || person.IsSelf) ||
            ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            return $"Choose 1–{maximum} different Messenger people.";
        return known is not null && people.FirstOrDefault(person => known.Participants.Any(member => member.Id == person.Id)) is { } already
            ? GroupChangeWording.AlreadyMember(already.Name) : null;
    }

    // Search answers with Facebook's own id; a conversation's people carry it as MessengerModels.Person does.
    public static bool IsMember(ChatSummary chat, ContactPerson person) =>
        chat.Participants.Any(member => member.Id == MessengerModels.Person(person.Id));

    // A conversation's people carry the "fb:" form; the helper takes Facebook's own id.
    public static string NativePerson(string personId) => personId.StartsWith("fb:", StringComparison.Ordinal) ? personId[3..] : personId;
}
