namespace MessageLink.Core.Services;

public interface IMessageMutations
{
    Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default);
    Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default);
}
