using MessageLink.Core.Messaging;

namespace MessageLink.App.Services.Instagram;

internal sealed partial class InstagramBackend
{
    // Memory only, and only a message as whole as this session has it: one a conversation read brought, or a text the list
    // carried, which is all of it (InstagramArrivals.Complete). Never the list's words for a photo, a video or a share
    // (InstagramGlimpses): history built from those drew a reel as a card with no picture that then grew when the
    // conversation's own read came back. Never a request to Instagram, and no log line.
    internal ChatMessage? PeekWholeMessage(string messageId)
    {
        lock (receiptGate) return seen.TryGetValue(messageId, out var remembered) ? remembered : null;
    }
}
