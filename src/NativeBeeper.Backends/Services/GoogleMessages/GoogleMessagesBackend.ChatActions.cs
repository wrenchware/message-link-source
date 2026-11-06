using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// Archive, spam and delete change the conversation on the phone. Pin and mute stay local
// preferences (LocalChatPreferenceStore). Uncertain outcomes are never retried.
internal sealed partial class GoogleMessagesBackend : IChatSettingsBackend, IChatDeletion
{
    public async Task<ChatSettings> GetSettingsAsync(string chatId, CancellationToken cancellationToken = default)
        => new(await GetChatAsync(chatId, cancellationToken), true);

    public async Task<ChatSummary> SetAsync(string chatId, ChatSetting setting, bool value, CancellationToken cancellationToken = default)
    {
        if (setting == ChatSetting.Spam) return await SetSpamAsync(chatId, value, cancellationToken);
        if (setting != ChatSetting.Archived) throw new BackendException(BackendFailure.Unsupported, "Pin and mute are saved in Message Link.");
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

    // Google has no "spam" alone: its action blocks the number and reports it, and moves the conversation to Spam & blocked.
    // Not spam is its Unblock. Google's own event or the next list read corrects this if the phone did otherwise.
    private async Task<ChatSummary> SetSpamAsync(string chatId, bool value, CancellationToken cancellationToken)
    {
        var native = GoogleModels.Native(chatId);
        var outcome = await UpdateChatAsync(native, value ? "blockAndReport" : "unblock", cancellationToken);
        GoogleMessagesLog.Write($"spam change chat={GoogleMessagesLog.Tag(chatId)} to={value} outcome={outcome}");
        if (outcome == "rejected") throw new SendException(false, value ? "Google Messages didn't block this conversation." : "Google Messages didn't take this conversation out of spam.");
        if (outcome != "accepted") throw new SendException(true, value ? "The block is unknown. Check your phone before trying again." : "The Not spam result is unknown. Check your phone before trying again.");
        var chat = (chats.TryGetValue(native, out var known) ? known : await RefreshChatAsync(chatId, cancellationToken)) with { Spam = value, Archived = false };
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
            var outcome = result.GetProperty("outcome").GetString() ?? "unknown";
            if (result.TryGetProperty("reason", out var reason)) GoogleMessagesLog.Write($"chat update action={action} outcome={outcome} reason={reason.GetString()}");
            return outcome;
        }
        // Helper error codes are returned before any change is submitted.
        catch (InvalidOperationException ex) { throw new SendException(false, GoogleReadFailure.Map(ex.Message).Message); }
        catch (BackendException ex) { throw new SendException(false, ex.Message); }
        catch (Exception) { return "unknown"; }
    }
}
