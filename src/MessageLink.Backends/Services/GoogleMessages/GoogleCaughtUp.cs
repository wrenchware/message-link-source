namespace MessageLink.App.Services.GoogleMessages;

// Which messages of a page the connect-time priority catch-up names as caught up (GoogleMessagesBackend.CaughtUp), by the
// app's ids: those after the archive's boundary (every one while the pages have not reached it, since they come newest
// first), in the page's own conversation, and only those with something to prepare — a picture, or a link that may get a
// card — so that a catch-up of chatter does not push the few pictures out of the preparation queue's 64 places.
internal static class GoogleCaughtUp
{
    public static string[] Named(string native, IEnumerable<GoogleMessage> page, DateTimeOffset? after) =>
        [.. page.Where(message => message.ChatId == native && (after is not { } at || message.At > at) &&
                (message.Media is { Length: > 0 } || message.Text?.Contains("http", StringComparison.OrdinalIgnoreCase) == true))
            .Select(message => GoogleModels.Qualify(message.Id))];
}
