using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.Messenger;

internal sealed partial class MessengerBackend
{
    // Derived from already loaded provider metadata, never from cookies or a second network call.
    // Requiring one unambiguous self identity makes an unknown account unavailable rather than
    // allowing a bookmark from an earlier sign-in to bleed into its replacement.
    internal string? LocalAccountScope
    {
        get
        {
            if (!IsConnected && !SessionSaved) return null;
            var identities = chats.SelectMany(c => c.Participants).Where(p => p.Self && !string.IsNullOrEmpty(p.Id))
                .Select(p => MessengerModels.Person(p.Id)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            return identities.Length == 1 ? "messenger:" + NotificationIdentity.Hash(identities[0]) : null;
        }
    }
}
