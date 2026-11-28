using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services;

// Texting someone from a Google Voice call or voicemail can go out two ways: from the Voice number they reached, or
// from the phone's own number through Google Messages. The person sees a different sender and answers into a
// different conversation, so the app offers the choice instead of deciding it. Which routes actually exist is the
// providers' answer, not a guess here: a route is offered only when its own account says it can start a conversation,
// which keeps a disconnected Voice account or an unpaired phone out of the choice entirely.
internal sealed record DirectTextRoute(string AccountId, string Network, ContactPerson Person);

internal static class DirectTextRoutes
{
    public const string GoogleVoiceNetwork = "Google Voice";
    public const string GoogleMessagesNetwork = "Google Messages";

    // The Voice account that owns the card comes first: it is the number this person already reached, so continuing
    // there is the answer that needs no explaining. Both carry the same number, normalized by Voice and accepted by
    // the Google Messages directory, so neither route depends on how the card happened to spell it.
    public static IReadOnlyList<DirectTextRoute> Candidates(ChatSummary? chat)
    {
        if (chat is null || GoogleVoice.GoogleVoiceModels.ParticipantNumber(chat) is not { } number) return [];
        var name = chat.Participants.FirstOrDefault()?.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) name = number;
        return
        [
            new(chat.AccountId, GoogleVoiceNetwork, Person(chat.AccountId, number, name!)),
            new(GoogleMessages.GoogleModels.Account, GoogleMessagesNetwork, Person(GoogleMessages.GoogleModels.Account, number, name!)),
        ];
    }

    private static ContactPerson Person(string accountId, string number, string name)
        => new(accountId, number, name, null, number, null, false);
}
