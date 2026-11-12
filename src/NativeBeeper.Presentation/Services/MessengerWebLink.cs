using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

/// <summary>
/// Where a Messenger conversation opens in the browser: Facebook's own page for it, which carries what this app cannot do, such as a Marketplace chat's
/// Mark as sold, Rate buyer and the listing (owner, 2026-11-12). Facebook's conversation number is the last part of the app's id: "messenger:" and the
/// number, or with another account's slot between them.
/// </summary>
public static class MessengerWebLink
{
    public const string Title = "Open in Messenger";

    /// <summary>The conversation's page on Facebook, or null for anything that is not one Messenger conversation.</summary>
    public static Uri? For(ChatSummary chat)
    {
        if (!chat.Id.StartsWith("messenger:", StringComparison.Ordinal) || MergedConversations.IsMerged(chat.Id)) return null;
        var number = chat.Id[(chat.Id.LastIndexOf(':') + 1)..];
        return number.Length is > 0 and <= 20 && number.All(char.IsAsciiDigit) ? new Uri("https://www.facebook.com/messages/t/" + number + "/") : null;
    }
}
