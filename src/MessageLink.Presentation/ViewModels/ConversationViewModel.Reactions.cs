using MessageLink.Core.Messaging;
using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

// Reactions, edits and deletes made here show at once while their service makes them (PendingRows); this side finds the
// message as the conversation holds it, draws again, and ends each prediction when its time is out.
public sealed partial class ConversationViewModel
{
    /// <summary>How long a reaction shows before the service confirms it (PendingRows.ReactionPredictionTimeout).</summary>
    public static TimeSpan ReactionPredictionTimeout { get => PendingRows.ReactionPredictionTimeout; set => PendingRows.ReactionPredictionTimeout = value; }
    /// <summary>How long an edit or a delete shows before the service's copy confirms it (PendingRows.ChangePredictionTimeout).</summary>
    public static TimeSpan ChangePredictionTimeout { get => PendingRows.ChangePredictionTimeout; set => PendingRows.ChangePredictionTimeout = value; }

    /// <summary>
    /// Follows what the message actions make in this conversation: each reaction, edit and delete shows at once, and goes if
    /// it is refused. Dispose to stop.
    /// </summary>
    public IDisposable ShowMadeBy(MessageActionsViewModel actions)
    {
        actions.ReactionSubmitting += ShowPendingReaction; actions.ReactionFailed += ClearPendingReaction;
        actions.ChangeSubmitting += ShowPendingChange; actions.ChangeFailed += ClearPendingChange;
        return new Unfollow(() =>
        {
            actions.ReactionSubmitting -= ShowPendingReaction; actions.ReactionFailed -= ClearPendingReaction;
            actions.ChangeSubmitting -= ShowPendingChange; actions.ChangeFailed -= ClearPendingChange;
        });
    }
    private sealed class Unfollow(Action undo) : IDisposable { public void Dispose() => undo(); }

    // Shows a reaction immediately while Google confirms it. The prediction ends when the message's reactions
    // change from their state at submission (Google's update), when submission fails, or after a minute without an update.
    public void ShowPendingReaction(ChatMessage message, string key, bool remove)
    {
        if (!ContainsChat(message.ChatId) || current is null) return;
        var actual = current.Messages.FirstOrDefault(m => m.Id == message.Id && m.ChatId == message.ChatId);
        var at = pendingRows.ShowReaction(actual is null ? message : message with { Reactions = actual.Reactions }, key, remove);
        RenderLive();
        _ = ExpirePendingReactionAsync(MessageIdentity.Key(message), at);
    }

    public void ClearPendingReaction(string messageId)
    {
        var keys = current?.Messages.Where(m => m.Id == messageId).Select(MessageIdentity.Key).Append(messageId).ToArray() ?? [messageId];
        if (pendingRows.ClearReaction(keys) && current is not null) RenderLive();
    }
    public void ClearPendingReaction(ChatMessage message) => ClearPendingReaction(MessageIdentity.Key(message));

    private async Task ExpirePendingReactionAsync(string messageId, DateTimeOffset at)
    {
        await Task.Delay(ReactionPredictionTimeout + TimeSpan.FromSeconds(1));
        if (pendingRows.ExpireReaction(messageId, at) && current is not null) RenderLive();
    }

    /// <summary>
    /// Shows an edit (<paramref name="text"/>) or a delete (<paramref name="everyone"/>) at once, as the reactions are shown, while the service makes it. A delete
    /// for this account takes the message away; one for everyone draws it as deleted, which is how a service that keeps the row shows it too. The prediction
    /// ends when the service's copy of the message changes from what it was (or the message is gone), when the change is refused, or after a minute.
    /// </summary>
    public void ShowPendingChange(ChatMessage message, string? text, bool? everyone)
    {
        if (!ContainsChat(message.ChatId) || current is null) return;
        var actual = current.Messages.FirstOrDefault(m => m.Id == message.Id && m.ChatId == message.ChatId) ?? message;
        var at = pendingRows.ShowChange(actual, text, everyone);
        RenderLive();
        _ = ExpirePendingChangeAsync(MessageIdentity.Key(message), at);
    }

    public void ClearPendingChange(ChatMessage message)
    {
        if (pendingRows.ClearChange(MessageIdentity.Key(message)) && current is not null) RenderLive();
    }

    private async Task ExpirePendingChangeAsync(string key, DateTimeOffset at)
    {
        await Task.Delay(ChangePredictionTimeout + TimeSpan.FromSeconds(1));
        if (pendingRows.ExpireChange(key, at) && current is not null) RenderLive();
    }

    private IReadOnlyList<string> SelfIds(string chatId) => SourceChat(chatId)?.Participants.Where(p => p.IsSelf == true).Select(p => p.Id).ToArray() ?? [];
    // Null for a message deleted here for this account alone, which is not drawn while the service removes it.
    private ChatMessage? WithPending(ChatMessage message) => pendingRows.Overlay(message, SelfIds);
    private ChatMessage WithPendingReaction(ChatMessage message) => pendingRows.OverlayReaction(message, SelfIds);
}
