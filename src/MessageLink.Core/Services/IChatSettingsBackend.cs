using MessageLink.Core.Messaging;
namespace MessageLink.Core.Services;
public enum ChatSetting { Archived, Pinned, Muted, MarkedUnread, Spam }
public sealed record ChatSettings(ChatSummary Chat, bool CanArchive);
public interface IChatDeletion
{
    Task DeleteChatAsync(string chatId, CancellationToken cancellationToken = default);
}
public interface IChatSettingsBackend
{
    Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken = default);
    Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default);
}
