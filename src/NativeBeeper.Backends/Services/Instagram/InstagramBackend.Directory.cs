using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using System.Text.Json;

namespace NativeBeeper.App.Services.Instagram;

internal sealed record InstagramCandidate(string Id, string Name, string? Username, string? Avatar, bool Verified);
internal sealed record InstagramStartedChat(InstagramChat? Chat, string MessageId, long At);
internal sealed record InstagramCreatedGroup(InstagramChat? Chat, string ThreadId, bool Named);

/// <summary>
/// Finding somebody on Instagram and starting the conversation with them.
/// </summary>
/// <remarks>
/// Starting one takes the two shapes Instagram offers. Where a thread already exists, the helper asks Instagram for
/// it and hands back that conversation carrying Instagram's own ids, and nothing is sent. Where none exists there is
/// nothing to open — Instagram has no call that creates an empty conversation — so the first message is what brings
/// it into being, and <see cref="StartDirectWithMessageAsync"/> sends it addressed to the person.
/// </remarks>
internal sealed partial class InstagramBackend : IContactBackend, IDirectChatLookup, IFirstMessageStarter
{
    /// <summary>
    /// This account's id, for the callers that have to name it before they can ask it anything. Null until a session
    /// is known: Instagram identities are account-scoped, and a placeholder would name a different account.
    /// </summary>
    internal string? AccountId => identity?.Account;

    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken = default)
        => Task.FromResult(identity is { } known && accountId == known.Account && IsConnected
            ? new ContactOptions(true, true, "Search Instagram by name or handle. Someone you have never written to gets a conversation when you send the first message from here.", RemoteSearch: true)
            : new ContactOptions(false, false, "Sign in to Instagram in Settings first."));

    public async Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken = default)
    {
        if (identity is not { } known || accountId != known.Account || helper is not { } started || !IsConnected)
            throw new BackendException(BackendFailure.Unavailable, "Sign in to Instagram in Settings first.");
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaxSearchLength) return [];
        // The search rules of the window (three characters, 900 ms apart, answers kept five minutes) are held here, for whoever asks: see PacedDirectorySearch.
        return await directorySearch.SearchAsync(accountId, query, async text =>
        {
            JsonElement result;
            try { result = await started.CallAsync("ui.searchUsers", new { query = text }, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception failure)
            {
                throw new BackendException(BackendFailure.Unavailable, failure.Message switch
                {
                    "session_expired" or "checkpoint_required" or "challenge_required" or "consent_required" =>
                        "Instagram needs you to sign in again. Reconnect Instagram in Settings.",
                    _ => "Instagram search could not complete. Try again.",
                });
            }
            return (IReadOnlyList<ContactPerson>)[.. (result.Deserialize<InstagramCandidate[]>(Json) ?? [])
                .Select(person => new ContactPerson(known.Account, person.Id, person.Name, person.Username, null, null,
                    // Whether a conversation can be opened is not known until it is looked up, so nobody is marked
                    // unmessageable here on a guess. A pair with no thread yet is refused at that point, by name.
                    CannotMessage: false, IsSelf: false, AvatarUrl: identity.Media(person.Avatar)))];
        }, cancellationToken);
    }

    // Held by the backend so the rules apply to every client of it, not only the window's own picker.
    internal readonly PacedDirectorySearch directorySearch = new();

    internal const int MaxSearchLength = 200;

    /// <summary>The conversation with this person among the ones already loaded, without asking Instagram.</summary>
    public ChatSummary? FindDirectChat(ContactPerson contact)
    {
        if (identity is not { } known || contact.AccountId != known.Account) return null;
        var found = chats.FirstOrDefault(chat => !chat.Group && !chat.ReadOnly && chat.Folder == "inbox"
            && chat.Participants.Any(person => person.Id == contact.Id && !person.Self));
        return found is null ? null : known.Map(found);
    }

    public async Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken = default)
    {
        if (identity is not { } known || contact.AccountId != known.Account || contact.IsSelf || string.IsNullOrWhiteSpace(contact.Id))
            throw new SendException(false, "Choose an Instagram account to write to.");
        // Already loaded is already open: no round trip, and no chance of the lookup disagreeing with the list.
        if (FindDirectChat(contact) is { } existing) return existing;
        if (helper is not { } started || !IsConnected) throw new SendException(false, "Sign in to Instagram in Settings first.");
        JsonElement result;
        try { result = await started.CallAsync("ui.startDirect", new { personId = contact.Id }, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        // No conversation yet is not a dead end: Instagram will open one, but only once something has been said in it,
        // so New chat answers by asking for that message rather than by printing a refusal.
        catch (Exception failure)
        {
            InstagramLog.Write($"instagram start direct outcome={InstagramRefusal.LogCode(failure)}");
            throw InstagramRefusal.Start(failure, contact.Name);
        }
        InstagramLog.Write("instagram start direct outcome=opened");
        var opened = result.Deserialize<InstagramChat>(Json)
            ?? throw new SendException(false, "Instagram did not describe that conversation. Refresh Instagram and try again.");
        return known.Map(Remember(opened));
    }

    public int MaxFirstMessageLength => MaxTextLength;

    /// <summary>
    /// Starts the conversation by sending its first message. Instagram has no call that creates an empty
    /// conversation, so this is the only way to reach somebody the account has never written to: the message is
    /// addressed to the person, and the conversation exists once it lands.
    /// </summary>
    /// <remarks>
    /// The send is never retried. If Instagram takes the message but the conversation cannot be read back, that is
    /// reported as sent-but-not-shown rather than as a failure, because a failure invites sending it again into a
    /// conversation that may already have it.
    /// </remarks>
    public async Task<ChatSummary> StartDirectWithMessageAsync(ContactPerson contact, string text, CancellationToken cancellationToken = default)
    {
        if (identity is not { } known || contact.AccountId != known.Account || contact.IsSelf || string.IsNullOrWhiteSpace(contact.Id))
            throw new SendException(false, "Choose an Instagram account to write to.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
            throw new SendException(false, "Write a message between 1 and " + MaxTextLength + " characters.");
        if (helper is not { } started || !IsConnected) throw new SendException(false, "Sign in to Instagram in Settings first.");
        JsonElement result;
        try { result = await started.CallAsync("ui.startDirectWithText", new { personId = contact.Id, text }, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        // Unlike opening a conversation, this one sends, so a failure whose outcome is unknown must say so: the
        // message may already be in somebody's conversation, and nothing here may offer to send it again.
        // Logged by its outcome alone, so the first live one says whether Instagram took the person's messaging id (PROVIDERS.md).
        catch (Exception failure)
        {
            InstagramLog.Write($"instagram first message outcome=refused code={InstagramRefusal.LogCode(failure)}");
            throw InstagramRefusal.FirstMessage(failure);
        }
        // An answer that cannot be read is an unconfirmed send, not an error to try again: the message may have gone.
        InstagramStartedChat? sent;
        try { sent = result.Deserialize<InstagramStartedChat>(Json); }
        catch (JsonException) { sent = null; }
        InstagramLog.Write($"instagram first message outcome={(sent is null || string.IsNullOrEmpty(sent.MessageId) ? "unconfirmed" : sent.Chat is null ? "sent_unshown" : "sent")}");
        if (sent is null || string.IsNullOrEmpty(sent.MessageId))
            throw new SendException(true, "Instagram did not confirm the message. Check Instagram before sending it again.");
        // The message went. A conversation Instagram would not describe afterwards is a drawing problem, not a
        // sending one, and saying otherwise would invite a second copy. Done keeps New chat from offering to send again.
        if (sent.Chat is null)
            throw new SendException(false, "Your message was sent. Instagram has not described the new conversation yet — refresh Instagram and it will appear.") { Done = true };
        return known.Map(Remember(sent.Chat));
    }

    // A conversation Instagram just described, taken into the list so it is there before any refresh runs.
    private InstagramChat Remember(InstagramChat chat)
    {
        chats = [.. chats.Where(known => known.Id != chat.Id), chat];
        _ = SaveChatsAsync();
        chatsReadAt = DateTimeOffset.MinValue;
        // A first message or a new group: the conversation's newest page is read from Instagram, never reused from before it.
        if (identity is { } owner) Announced(owner.Chat(chat.Id));
        ChatsChanged?.Invoke(this, EventArgs.Empty);
        return chat;
    }
}
