using NativeBeeper.Core.Messaging;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend
{
    // Google says a conversation is end-to-end encrypted only in system lines, and the newest one is rarely in the page a
    // conversation opens with. The newest one saved on this PC goes with the summary, and the conversation weighs it
    // against what it has loaded.
    private ChatSummary Summary(GoogleChat chat) => Models.Map(chat) with { SavedSwitch = archive.Switch(chat.Id) };
}
