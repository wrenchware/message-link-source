using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Messenger;

// Finding people, starting conversations and changing groups, passed to the service that owns the account id (or, for a
// group's change, the conversation id). Each service answers or refuses in its own words (ConversationOwners).
internal sealed class DirectoryRouter(ConversationOwners owners)
    : IContactBackend, IGroupBackend, IDirectChatLookup, IFirstMessageStarter, IFrequentContacts, IGroupManagement
{
    internal ProviderAccounts? Accounts { get => owners.Accounts; set => owners.Accounts = value; }
    // Changing an existing group is Messenger's alone (2026-10-04), so its limits are the only ones there are.
    public int GroupNameMaximum => ((IGroupManagement)owners.Messenger).GroupNameMaximum;
    public int AddMaximum => ((IGroupManagement)owners.Messenger).AddMaximum;
    // The rule is Core's (MessengerGroupMembership), so a client answers it too; the harness holds it to Messenger's own naming.
    public bool IsMember(ChatSummary chat, ContactPerson person) => MessengerGroupMembership.IsMember(chat, person);
    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken = default)
        => owners.Of(chatId).RenameGroupAsync(chatId, title, cancellationToken);
    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken = default)
        => owners.Of(chatId).AddGroupMembersAsync(chatId, people, cancellationToken);
    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken = default)
        => owners.Of(chatId).RemoveGroupMemberAsync(chatId, personId, cancellationToken);

    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken = default)
        => owners.Of(accountId).GetContactOptionsAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken = default)
        => owners.Of(accountId).FindContactsAsync(accountId, query, cancellationToken);

    public Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken = default)
        => owners.Of(contact.AccountId).StartDirectAsync(contact, allowInvite, cancellationToken);

    // Instagram is the only provider that cannot open a conversation without sending into it. The others never
    // raise FirstMessageRequiredException, so New chat never asks them for one.
    public int MaxFirstMessageLength => owners.Instagram.MaxFirstMessageLength;

    public Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken = default)
        => owners.Of(contact.AccountId).StartDirectWithMessageAsync(contact, text, cancellationToken);

    // Only Google Messages' phone keeps a ranking of who this account writes to most. The others have none that the
    // helpers expose, and unknown is nothing: New chat shows no Frequent section for them.
    public Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken = default)
        => owners.Of(accountId).GetFrequentContactsAsync(accountId, cancellationToken);

    public ChatSummary? FindDirectChat(ContactPerson contact) => owners.Of(contact.AccountId).FindDirectChat(contact);

    // This reason is what New chat actually shows once a service is picked, so it carries the guidance for starting
    // one conversation as well as what that service does about groups.
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken = default)
        => owners.Of(accountId).GetGroupOptionsAsync(accountId, cancellationToken);

    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken = default)
        => owners.Of(accountId).CreateGroupAsync(accountId, people, title, cancellationToken);
}
