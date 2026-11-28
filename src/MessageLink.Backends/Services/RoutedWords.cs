using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services;

// Refusals several services give in the same words. Each service's own wording stays in its IConversationService.
internal static class RoutedWords
{
    internal const string CantOpenAtMessage = "This conversation can't open at that message.";
    internal const string NoPhotoSet = "This service can't send photos as one set.";
    // Instagram is the only service that cannot open a conversation without sending into it.
    internal const string OpensWithoutSending = "That service opens a conversation without sending anything; start it and type in the composer.";
    // Changing an existing group is Messenger's alone (2026-10-04). Instagram's helper has no confirmed call for it, Google
    // Messages' and Voice's none, so their conversations never offer it.
    internal static SendException NotMessengerGroup() => new(false, MessageLink.App.Services.Messenger.MessengerGroupChanges.OtherService);
}
