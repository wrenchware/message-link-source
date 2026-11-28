namespace MessageLink.Core.Services;

/// <summary>What a message offers. <paramref name="AddReactions"/> is the service's short row; <paramref name="AnyReaction"/> says it also takes any
/// other single emoji (<see cref="ReactionKey.IsEmoji"/>), as Google Messages, Messenger and Instagram each do from their own full pickers.</summary>
public sealed record MessageActionOptions(bool CanReply, IReadOnlyList<string> AddReactions, IReadOnlyList<string> RemoveReactions,
    bool CanEdit = false, bool CanDeleteForMe = false, bool CanDeleteForEveryone = false, int MaxEditLength = 65536, bool AnyReaction = false)
{
    /// <summary>Whether <paramref name="key"/> can be added: one of the short row, or any single emoji where the service takes any.</summary>
    public bool CanAdd(string key) => AddReactions.Contains(key) || AnyReaction && ReactionKey.IsEmoji(key) && Own(key) is null;

    /// <summary>This account's reaction that is <paramref name="key"/>, as the service wrote it, whatever variation selector the picker gave.</summary>
    public string? Own(string key) => RemoveReactions.FirstOrDefault(mine => ReactionKey.Same(mine, key));
}
public interface IMessageActions
{
    bool HasLocalActionOptions => false;
    Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default);
    Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default);
    Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default);
}
