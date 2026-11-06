using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using System.Text.Json;

namespace NativeBeeper.App.Services.Messenger;

internal sealed record MessengerContact(string Id, string Name, string? Username, bool CannotMessage, bool Self, string? AvatarUrl);

internal sealed partial class MessengerBackend : IContactBackend, IDirectChatLookup
{
    public Task<ContactOptions> GetContactOptionsAsync(string accountId, CancellationToken cancellationToken = default)
        => Task.FromResult(accountId == MessengerModels.Account && helper is not null
            ? new ContactOptions(true, true, "Search Messenger people by name, then start the conversation without sending a message.", RemoteSearch: true)
            : new ContactOptions(false, false, "Connect Messenger in Settings first."));

    public async Task<IReadOnlyList<ContactPerson>> FindContactsAsync(string accountId, string query, CancellationToken cancellationToken = default)
    {
        if (accountId != MessengerModels.Account || helper is not { } started)
            throw new BackendException(BackendFailure.Unavailable, "Connect Messenger in Settings first.");
        if (string.IsNullOrWhiteSpace(query) || query.Length > 1000) return [];
        // The search rules of the window (three characters, 900 ms apart, answers kept five minutes) are held here, for whoever asks: see PacedDirectorySearch.
        // Answers are kept per connection (a new sign-in is a new helper), so a person who signs in as somebody else is never shown the last one's results.
        return await directorySearch.SearchAsync(accountId + "#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(started), query, async text =>
        {
            try
            {
                var result = await started.CallAsync("ui.searchContacts", new { query = text }, cancellationToken);
                return (IReadOnlyList<ContactPerson>)(result.Deserialize<MessengerContact[]>(Json) ?? [])
                    .Select(person => new ContactPerson(MessengerModels.Account, person.Id, person.Name,
                        person.Username, null, null, person.CannotMessage, person.Self,
                        MessengerModels.QualifyMedia(person.AvatarUrl ?? ""))).ToArray();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not BackendException)
            {
                _ = ex;
                throw new BackendException(BackendFailure.Unavailable, "Messenger contact search could not complete. Try again.");
            }
        }, cancellationToken);
    }

    // Held by the backend so the rules apply to every client of it, not only the window's own picker.
    internal readonly NativeBeeper.App.Services.PacedDirectorySearch directorySearch = new();

    public ChatSummary? FindDirectChat(ContactPerson contact)
    {
        if (contact.AccountId != MessengerModels.Account) return null;
        var found = chats.FirstOrDefault(chat => !chat.Group && !chat.ReadOnly && chat.Participants.Any(person => person.Id == contact.Id && !person.Self));
        return found is null ? null : MessengerModels.Map(found);
    }

    // A conversation of the list as last read, without asking the helper: how a host with no window's catalogue presents a
    // kept search's conversations (MessengerKeptHistory's liveChat).
    internal ChatSummary? KnownChat(string chatId)
    {
        if (!MessengerModels.Owns(chatId)) return null;
        var native = MessengerModels.Native(chatId);
        var found = chats.FirstOrDefault(chat => chat.Id == native);
        return found is null ? null : MessengerModels.Map(found);
    }

    public async Task<ChatSummary> StartDirectAsync(ContactPerson contact, bool allowInvite, CancellationToken cancellationToken = default)
    {
        if (contact.AccountId != MessengerModels.Account || contact.CannotMessage || contact.IsSelf || string.IsNullOrWhiteSpace(contact.Id))
            throw new SendException(false, "Choose a Messenger contact you can message.");
        if (FindDirectChat(contact) is { } existing) return existing;
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings first.");
        try
        {
            var result = await started.CallAsync("ui.startDirect", new { personId = contact.Id }, cancellationToken);
            var created = result.Deserialize<MessengerChat>(Json) ?? throw new InvalidOperationException("start_failed");
            chats = chats.Where(chat => chat.Id != created.Id).Append(created).ToArray();
            _ = SaveChatsAsync(chats);
            ChatsChanged?.Invoke(this, EventArgs.Empty);
            return MessengerModels.Map(created);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) when (ex.Message is "start_rejected" or "start_failed" or "start_unavailable")
        {
            throw new SendException(ex.Message is not "start_rejected", ex.Message switch
            {
                "start_rejected" => "Messenger would not start that conversation.",
                "start_unavailable" => "Messenger started the conversation but hasn't finished setting it up. Refresh chats, then try again.",
                _ => "Messenger may have started the conversation. Refresh chats before trying again."
            });
        }
        catch (Exception ex) when (ex is not SendException)
        {
            _ = ex;
            throw new SendException(true, "The result is unknown. Refresh chats before trying again.");
        }
    }
}
