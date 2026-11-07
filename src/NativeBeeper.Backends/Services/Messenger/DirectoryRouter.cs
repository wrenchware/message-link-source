using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;

namespace NativeBeeper.App.Services.Messenger;

internal sealed class DirectoryRouter(GoogleMessagesBackend google, MessengerBackend messenger, GoogleVoiceBackend voice, InstagramBackend instagram)
    : IContactBackend, IGroupBackend, IDirectChatLookup, IFirstMessageStarter, IFrequentContacts, IGroupManagement
{
    internal ProviderAccounts? Accounts { get; set; }
    private GoogleMessagesBackend Google(string id) => Accounts?.Google(id) ?? google;
    private MessengerBackend Messenger(string id) => Accounts?.Messenger(id) ?? messenger;
    private NativeBeeper.App.Services.Instagram.InstagramBackend InstagramAccount(string id) => Accounts?.Instagram(id) ?? instagram;
    // Changing an existing group is Messenger's alone (2026-10-04). Instagram's helper has no confirmed call for it, Google
    // Messages' and Voice's none, so their conversations never offer it and a request for one is refused by name.
    private IGroupManagement Managing(string chatId) => MessengerModels.Owns(chatId) ? Messenger(chatId)
        : throw new SendException(false, MessengerGroupChanges.OtherService);
    public int GroupNameMaximum => ((IGroupManagement)messenger).GroupNameMaximum;
    public int AddMaximum => ((IGroupManagement)messenger).AddMaximum;
    // The rule is Core's (MessengerGroupMembership), so a client answers it too; the harness holds it to Messenger's own naming.
    public bool IsMember(ChatSummary chat, ContactPerson person) => MessengerGroupMembership.IsMember(chat, person);
    public Task<ChatSummary> RenameGroupAsync(string chatId, string title, CancellationToken cancellationToken = default)
        => Managing(chatId).RenameGroupAsync(chatId, title, cancellationToken);
    public Task<ChatSummary> AddGroupMembersAsync(string chatId, IReadOnlyList<ContactPerson> people, CancellationToken cancellationToken = default)
        => Managing(chatId).AddGroupMembersAsync(chatId, people, cancellationToken);
    public Task<ChatSummary> RemoveGroupMemberAsync(string chatId, string personId, CancellationToken cancellationToken = default)
        => Managing(chatId).RemoveGroupMemberAsync(chatId, personId, cancellationToken);

    private static bool Mine(string accountId) => MessengerModels.Owns(accountId);
    private static bool Voice(string accountId) => GoogleVoiceModels.AccountKey(accountId) is not null;
    // Instagram ids are account-scoped, so which account is asking is not known here; the backend owns that and
    // refuses an id that is not its own.
    private static bool Instagram(string accountId) => InstagramIdentity.OwnsAny(accountId);

    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken = default)
        => Voice(accountId) ? voice.GetContactOptionsAsync(accountId)
            : Instagram(accountId) ? InstagramAccount(accountId).GetContactOptionsAsync(accountId, cancellationToken)
            : Mine(accountId) ? Messenger(accountId).GetContactOptionsAsync(accountId, cancellationToken) : Google(accountId).GetContactOptionsAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken = default)
        => Voice(accountId) ? voice.FindContactsAsync(accountId, query, cancellationToken)
            : Instagram(accountId) ? InstagramAccount(accountId).FindContactsAsync(accountId, query, cancellationToken)
            : Mine(accountId) ? Messenger(accountId).FindContactsAsync(accountId, query, cancellationToken) : Google(accountId).FindContactsAsync(accountId, query, cancellationToken);

    public Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken = default)
        => Voice(contact.AccountId) ? voice.StartDirectAsync(contact)
            : Instagram(contact.AccountId) ? InstagramAccount(contact.AccountId).StartDirectAsync(contact, allowInvite, cancellationToken)
            : Mine(contact.AccountId) ? Messenger(contact.AccountId).StartDirectAsync(contact, allowInvite, cancellationToken) : Google(contact.AccountId).StartDirectAsync(contact, allowInvite, cancellationToken);

    // Instagram is the only provider that cannot open a conversation without sending into it. The others never
    // raise FirstMessageRequiredException, so New chat never asks them for one.
    public int MaxFirstMessageLength => instagram.MaxFirstMessageLength;

    public Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken = default)
        => Instagram(contact.AccountId) ? InstagramAccount(contact.AccountId).StartDirectWithMessageAsync(contact, text, cancellationToken)
            : throw new SendException(false, "That service opens a conversation without sending anything; start it and type in the composer.");

    // Only Google Messages' phone keeps a ranking of who this account writes to most. The others have none that the
    // helpers expose, and unknown is nothing: New chat shows no Frequent section for them.
    public Task<IReadOnlyList<ContactPerson>?> GetFrequentContactsAsync(string accountId, CancellationToken cancellationToken = default)
        => Voice(accountId) || Instagram(accountId) || Mine(accountId)
            ? Task.FromResult<IReadOnlyList<ContactPerson>?>(null)
            : Google(accountId).GetFrequentContactsAsync(accountId, cancellationToken);

    public ChatSummary? FindDirectChat(ContactPerson contact)
        => Voice(contact.AccountId) ? voice.FindDirectChat(contact)
            : Instagram(contact.AccountId) ? InstagramAccount(contact.AccountId).FindDirectChat(contact)
            : Mine(contact.AccountId) ? Messenger(contact.AccountId).FindDirectChat(contact) : ((IDirectChatLookup)Google(contact.AccountId)).FindDirectChat(contact);

    // This reason is what New chat actually shows once a service is picked, so it carries the guidance for starting
    // one conversation as well as what that service does about groups. Google Voice is the only one that cannot make
    // them: upstream has no call for it at all, and Beeper does not offer it either.
    public Task<GroupOptions> GetGroupOptionsAsync(string accountId, CancellationToken cancellationToken = default)
        => Voice(accountId)
            ? Task.FromResult(new GroupOptions(false, "Google Voice group texts can't be started from New chat."))
            : Instagram(accountId) ? InstagramAccount(accountId).GetGroupOptionsAsync(accountId, cancellationToken)
            : Mine(accountId) ? Messenger(accountId).GetGroupOptionsAsync(accountId, cancellationToken)
            : Google(accountId).GetGroupOptionsAsync(accountId, cancellationToken);

    public Task<ChatSummary> CreateGroupAsync(string accountId, IReadOnlyList<ContactPerson> people, string title, CancellationToken cancellationToken = default)
        => Voice(accountId)
            ? throw new SendException(false, "Google Voice group texts can't be started from New chat.")
            : Instagram(accountId) ? InstagramAccount(accountId).CreateGroupAsync(accountId, people, title, cancellationToken)
            : Mine(accountId) ? Messenger(accountId).CreateGroupAsync(accountId, people, title, cancellationToken)
            : Google(accountId).CreateGroupAsync(accountId, people, title, cancellationToken);
}
