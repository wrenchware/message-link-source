using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services;

/// <summary>
/// Asks a provider to make the card for a link that has just come in. Messenger: a Facebook or Instagram share in an encrypted
/// conversation is only its words until the helper draws its card, which it does when asked: here as soon as the message comes rather
/// than when its conversation opens. Google Messages: only where <see cref="GoogleIncomingCards"/> allows, with one line saying which
/// way it went. Neither with link previews turned off, which is the user saying no page is to be fetched for them.
/// </summary>
internal sealed class IncomingCardRequester(MessengerBackend messenger, GoogleMessagesBackend google, GoogleIncomingCards cards, Action<string> log) : IIncomingCards
{
    internal ProviderAccounts? Accounts { get; set; }
    public async Task RequestAsync(string chatId, string messageId, bool linkPreviews, bool? listedAsSpam, CancellationToken cancellationToken = default)
    {
        if (MessengerModels.Owns(chatId))
        {
            if (linkPreviews) await (Accounts?.Messenger(chatId) ?? messenger).LoadLinkPreviewsAsync(chatId, [messageId], cancellationToken);
            return;
        }
        if (!GoogleModels.Owns(chatId)) return;
        var target = Accounts?.Google(chatId) ?? google;
        var refusal = await (ReferenceEquals(target, google) ? cards : target.IncomingCards).RefusalAsync(target.Slot.Ids.Native(chatId), linkPreviews, listedAsSpam, cancellationToken);
        log($"link card ahead provider=google outcome={(refusal is null ? "asked" : "refused")} reason={refusal ?? "-"}");
        if (refusal is null) await target.LoadLinkPreviewsAsync(chatId, [messageId], cancellationToken);
    }
}
