using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// Archive and delete change the conversation on the phone. Pin and mute stay local
// preferences (LocalChatPreferenceStore). Uncertain outcomes are never retried.
internal sealed partial class GoogleMessagesBackend : IChatSettingsBackend, IChatDeletion
{
    public async Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken = default)
        => new(await GetChatAsync(chatId, cancellationToken), true);

    public async Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default)
    {
        if (setting != ChatSetting.Archived) throw new BackendException(BackendFailure.Unsupported, "Pin and mute are saved in Messages.");
        var native = GoogleModels.Native(chatId);
        var outcome = await UpdateChatAsync(native, value ? "archive" : "unarchive", cancellationToken);
        if (outcome == "rejected") throw new SendException(false, value ? "Google Messages didn't archive this conversation." : "Google Messages didn't unarchive this conversation.");
        if (outcome != "accepted") throw new SendException(true, "The archive change is unknown. Check your phone before trying again.");
        var chat = (chats.TryGetValue(native, out var known) ? known : await RefreshChatAsync(chatId, cancellationToken)) with { Archived = value };
        SetChat(chat);
        ArchiveChats([chat]);
        Publish(new(RealtimeKind.ChatChanged, chatId));
        return Summary(chat);
    }

    public async Task DeleteChatAsync(string chatId, CancellationToken cancellationToken = default)
    {
        var native = GoogleModels.Native(chatId);
        var outcome = await UpdateChatAsync(native, "delete", cancellationToken);
        if (outcome == "rejected") throw new SendException(false, "Google Messages didn't delete this conversation.");
        if (outcome != "accepted") throw new SendException(true, "The delete outcome is unknown. Check your phone before trying again.");
        if (RemoveChat(native, out var removed)) ArchiveChats([removed with { Hidden = true }]);
        else if (archive.Chat(native) is { } saved) ArchiveChats([saved with { Hidden = true }]);
        Publish(new(RealtimeKind.ChatDeleted, chatId));
    }

    private async Task<string> UpdateChatAsync(string nativeChat, string action, CancellationToken cancellationToken)
    {
        try
        {
            var result = await MediaCallAsync("ui.updateChat", new { chatId = nativeChat, action }, cancellationToken);
            return result.GetProperty("outcome").GetString() ?? "unknown";
        }
        // Helper error codes are returned before any change is submitted.
        catch (InvalidOperationException ex) { throw new SendException(false, GoogleReadFailure.Map(ex.Message).Message); }
        catch (BackendException ex) { throw new SendException(false, ex.Message); }
        catch (Exception) { return "unknown"; }
    }
}
