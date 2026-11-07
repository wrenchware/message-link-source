using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend : IDirectChatLookup
{
    // Loaded chats first, then older and archived chats from the encrypted local archive; no phone round trip.
    public ChatSummary? FindDirectChat(ContactPerson contact)
    {
        if (contact.AccountId != Models.Account || contact.CannotMessage) return null;
        var known = chats.Values.Concat(archive.ChatsSnapshot().Where(c => !chats.ContainsKey(c.Id)));
        return GoogleDirectory.FindDirect(known, contact.Phone) is { } chat ? Models.Map(chat) : null;
    }
}
