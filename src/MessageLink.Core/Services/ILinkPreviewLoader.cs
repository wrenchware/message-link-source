namespace MessageLink.Core.Services;

// Optional enrichment requested only by the open conversation. Reading history for a chat-list snippet or an archive
// must not fetch the websites linked in those messages.
public interface ILinkPreviewLoader
{
    Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default);
}
