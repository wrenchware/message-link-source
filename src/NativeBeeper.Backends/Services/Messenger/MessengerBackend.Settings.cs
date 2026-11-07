using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using System.Text.Json;

namespace NativeBeeper.App.Services.Messenger;

internal sealed partial class MessengerBackend : IChatSettingsBackend
{
    public async Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken = default)
        => new(await GetChatAsync(chatId, cancellationToken), true);

    public async Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default)
    {
        if (setting != ChatSetting.Archived) throw new SendException(false, "That setting is kept on this PC only, not on Messenger.");
        if (helper is not { } started) throw new SendException(false, "Connect Messenger in Settings first.");
        try
        {
            var result = await started.CallAsync("ui.updateChat", new { chatId = Models.Native(chatId), archived = value }, cancellationToken);
            var updated = result.Deserialize<MessengerChat>(Json) ?? throw new InvalidOperationException("update_failed");
            MessengerLog.Write($"archive chat={MessengerLog.Tag(chatId)} archived={value} outcome=confirmed");
            chats = chats.Select(chat => chat.Id == updated.Id ? updated : chat).ToArray();
            await SaveChatsAsync(chats);
            ChatsChanged?.Invoke(this, EventArgs.Empty);
            return Models.Map(updated);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure)
        {
            // Nothing here moves the conversation: the helper's copy is what Messenger last said, and the list keeps it.
            MessengerLog.Write($"archive chat={MessengerLog.Tag(chatId)} archived={value} outcome={MessengerFailure.Code(failure)}");
            throw new SendException(MessengerFailure.IsUpdateUnknown(failure.Message), MessengerFailure.ExplainUpdate(failure.Message));
        }
        catch (Exception ex) when (ex is not SendException)
        {
            MessengerLog.Write($"archive chat={MessengerLog.Tag(chatId)} archived={value} outcome={ex.GetType().Name}");
            throw new SendException(true, MessengerFailure.ExplainUpdate(""));
        }
    }
}
