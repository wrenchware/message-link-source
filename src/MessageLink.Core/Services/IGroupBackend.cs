using MessageLink.Core.Messaging;

namespace MessageLink.Core.Services;

public sealed record GroupOptions(bool Allowed, string Reason, int Minimum = 2, int Maximum = 250,
    bool NameAllowed = false, bool NameRequired = false, int NameMinimum = 0, int NameMaximum = 1000)
{
    public string? Validate(IReadOnlyList<ContactPerson> people, string accountId, string title)
    {
        if (!Allowed) return Reason;
        if (people.Count < Minimum || people.Count > Maximum) return $"Select {Minimum}–{Maximum} participants.";
        if (people.Any(p => p.AccountId != accountId || p.CannotMessage || p.IsSelf || string.IsNullOrWhiteSpace(p.Id)) || people.Select(p => p.Id).Distinct().Count() != people.Count)
            return "Choose different people you can message from this account.";
        if (!NameAllowed && title.Length > 0) return "This service doesn't let you name a group when you create it.";
        if (NameRequired && string.IsNullOrWhiteSpace(title)) return "Enter a group name.";
        if (title.Length > 0 && (title.Length < NameMinimum || title.Length > NameMaximum)) return $"Group names must be {NameMinimum}–{NameMaximum} characters.";
        return null;
    }
}
public interface IGroupBackend
{
    Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken = default);
    Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken = default);
}

/// <summary>
/// Changing a group that already exists: its name and who is in it. Each change is seen by everybody in the group the
/// moment the service takes it, so it is made only from an explicit choice the person has confirmed, and once: never
/// retried, and an outcome that did not arrive is reported as unknown rather than sent again (2026-10-04). Which
/// changes a conversation offers is on its capabilities (RenameGroup, AddMembers, RemoveMembers); unknown is none.
/// </summary>
public interface IGroupManagement
{
    /// <summary>The longest group name taken, in characters.</summary>
    int GroupNameMaximum { get; }
    /// <summary>The most people one Add takes.</summary>
    int AddMaximum { get; }
    /// <summary>Whether somebody found by searching this service is already in this conversation.</summary>
    bool IsMember(ChatSummary chat, ContactPerson person);
    Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken = default);
    Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken = default);
    Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken = default);
}
