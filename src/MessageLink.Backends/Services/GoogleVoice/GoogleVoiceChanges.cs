using System.Text.Json;
using System.Threading.Channels;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleVoice;

// A helper 'changed' event: which conversations moved and which messages arrived in them.
internal static class GoogleVoiceChanges
{
    public static void Publish(string accountKey, JsonElement changes, GoogleVoiceChatReads reads, ChannelWriter<RealtimeEvent> realtime, DateTimeOffset received)
    {
        var changed = changes.TryGetProperty("chats", out var moved) && moved.ValueKind == JsonValueKind.Array
            ? moved.EnumerateArray().Select(id => id.GetString()).OfType<string>().Select(id => GoogleVoiceModels.Qualify(accountKey, id)).ToArray()
            : [];
        var arrivals = changes.TryGetProperty("messages", out var arrived) && arrived.ValueKind == JsonValueKind.Object
            ? arrived.EnumerateObject().Select(chat => (Chat: GoogleVoiceModels.Qualify(accountKey, chat.Name), Ids: chat.Value.EnumerateArray()
                .Select(id => id.GetString()).OfType<string>().Select(id => GoogleVoiceModels.Qualify(accountKey, id)).ToArray())).ToArray()
            : [];
        // The summaries are stale now. They go before anything is announced, so the notification worker and the chat list
        // read them afresh instead of the summary from before the message.
        reads.Invalidate(accountKey, changed.Concat(arrivals.Select(arrival => arrival.Chat)));
        foreach (var (chat, ids) in arrivals)
            if (ids.Length > 0) realtime.TryWrite(new(RealtimeKind.MessageChanged, chat, ids, true, received));
        if (changed.Length > 0) realtime.TryWrite(new(RealtimeKind.ChatChanged, null, changed, null, received));
    }

    // A conversation this account deleted (2026-10-04). Its summary and its messages leave memory before the deletion is
    // announced, and a list read that began earlier cannot put the summary back, so nothing reacting finds it again.
    public static void Deleted(string accountKey, string chatId, GoogleVoiceChatReads reads, MessageLink.Presentation.Services.MessageLookupCache messages,
        ChannelWriter<RealtimeEvent> realtime, DateTimeOffset received)
    {
        reads.Invalidate(accountKey, [chatId]);
        messages.RemoveWhere(message => message.ChatId == chatId);
        realtime.TryWrite(new(RealtimeKind.ChatDeleted, chatId, ReceivedAt: received));
    }
}
