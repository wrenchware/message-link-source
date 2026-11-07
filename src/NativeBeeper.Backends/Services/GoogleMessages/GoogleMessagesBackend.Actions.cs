using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend
{
    private async Task<GoogleChat> RefreshChatAsync(string chatId,CancellationToken token)
    {
        await ConnectAsync(token);
        if(IsPlaceholder(chatId)) throw new BackendException(BackendFailure.Unavailable,"That conversation isn't available right now. Refresh and try again.");
        var result=await ReadAsync("ui.chat",new {chatId=Models.Native(chatId)},token);
        var chat=result.Deserialize<GoogleChat>(Json);
        if(chat is null || chat.Id!=Models.Native(chatId)) throw new BackendException(BackendFailure.Unavailable,"Conversation couldn't be refreshed.");
        SetChat(chat);
        ArchiveChats([chat]);
        return chat;
    }
    public async Task<bool> CanMarkReadAsync(string chatId,CancellationToken cancellationToken=default)
    {
        await GetChatAsync(chatId,cancellationToken);
        return connected;
    }
    public async Task MarkReadAsync(string chatId,string messageId,CancellationToken cancellationToken=default)
    {
        // A message still on its way has no id Google knows. Answering this as done showed the conversation read while Google
        // was never told (2026-10-22 audit); refused, it stays as Google says and is read again once the real id arrives.
        if(messageId.StartsWith(PendingPrefix,StringComparison.Ordinal))
        {
            GoogleMessagesLog.Write($"operation=mark_read outcome=deferred reason=pending chat={GoogleMessagesLog.Tag(chatId)}");
            throw new BackendException(BackendFailure.Unavailable,"That message is still sending.");
        }
        await MediaCallAsync("ui.markRead",new {chatId=Models.Native(chatId),messageId=Models.Native(messageId)},cancellationToken);
        // Do not force an unread count to zero: a newer message may have arrived during the write. Google took the read
        // whether or not this refresh answers, so a failed refresh is not a failed read.
        try
        {
            var after=await RefreshChatAsync(chatId,cancellationToken);
            // Whether Google still says unread straight after taking the read: the first sign of a read it did not keep.
            GoogleMessagesLog.Write($"mark read chat={GoogleMessagesLog.Tag(chatId)} unread_after={(after.Unread ? "yes" : "no")}");
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested) { throw; }
        catch(Exception) { GoogleMessagesLog.Write($"mark read chat={GoogleMessagesLog.Tag(chatId)} unread_after=unknown"); }
        Publish(new(RealtimeKind.ChatChanged,chatId));
    }
    public bool HasLocalActionOptions => true;
    public Task<MessageActionOptions> GetOptionsAsync(string chatId,string messageId,CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connected || !messageId.StartsWith("google-direct:",StringComparison.Ordinal) || messageId.StartsWith(PendingPrefix,StringComparison.Ordinal) ||
            !chats.TryGetValue(Models.Native(chatId),out var chat) || !messages.TryGetValue(Models.Native(messageId),out var message))
            return Task.FromResult(new MessageActionOptions(false,[],[]));
        return Task.FromResult(GoogleModels.Actions(chat,message));
    }
    public async Task<SendReceipt> ReplyAsync(string chatId,string messageId,string text,CancellationToken cancellationToken=default)
    {
        var options=await GetOptionsAsync(chatId,messageId,cancellationToken);
        if(!options.CanReply) throw new SendException(false,"This message cannot be quoted. Native replies require an available RCS conversation.");
        return await SendTextCoreAsync(chatId,text,messageId,cancellationToken);
    }
    public async Task ReactAsync(string chatId,string messageId,string reaction,bool remove,CancellationToken cancellationToken=default)
    {
        // ui.react validates the conversation, SIM, target and reaction itself, against the phone's description of the
        // conversation from the last minute or a fresh lookup, as a send does (GetSendPermissionAsync). Repeating
        // ui.actions here adds another phone round trip.
        await ThrowIfRenumberedAsync();
        try
        {
            var response=await MediaCallAsync("ui.react",new {chatId=Models.Native(chatId),messageId=Models.Native(messageId),key=reaction,remove},cancellationToken);
            var outcome=response.GetProperty("outcome").GetString();
            if(outcome!="accepted") throw new SendException(outcome!="rejected",outcome=="rejected"?"Google Messages rejected this reaction.":"Reaction outcome is unknown. Check the message before trying again.");
        }
        catch(SendException) {throw;}
        catch(Exception) {throw new SendException(true,"Reaction outcome is unknown. Check the message before trying again.");}
        // The event pump already retains echoes arriving before this response. Do not
        // hold the action busy for a second phone history request after acceptance.
        // Realtime and reconnect resync remain authoritative for the displayed reaction.
    }

    // Google Messages cannot change the words of a message that has gone: no such request exists in the protocol.
    // This is a permanent limit, not something unported, so it says so rather than promising a later version.
    public Task EditAsync(string chatId,string messageId,string? originalText,string text,CancellationToken cancellationToken=default)
        => throw new SendException(false,"Google Messages cannot edit a message after it has been sent.");

    // Takes a message out of this conversation. It is removed from this account everywhere — the phone and any device
    // signed in to it — and the person it was sent to keeps their copy. Google offers nothing that reaches them.
    public async Task DeleteAsync(string chatId,string messageId,bool forEveryone,CancellationToken cancellationToken=default)
    {
        if(forEveryone) throw new SendException(false,"Google Messages cannot remove a message from the other person's phone. It can only be deleted from your own conversation.");
        if(!connected||helper is null) throw new SendException(false,"Reconnect Google Messages to delete this message.");
        try
        {
            await MediaCallAsync("ui.deleteMessage",new {chatId=Models.Native(chatId),messageId=Models.Native(messageId),everyone=false},cancellationToken);
        }
        catch(SendException) {throw;}
        catch(OperationCanceledException) {throw;}
        catch(InvalidOperationException failure)
        {
            throw new SendException(failure.Message is not ("delete_rejected" or "message_unavailable" or "delete_everyone_unsupported"), failure.Message switch
            {
                "delete_rejected" => "Google Messages refused to delete that message.",
                "message_unavailable" => "That message is no longer loaded.",
                "delete_everyone_unsupported" => "Google Messages can only delete a message from your own conversation.",
                _ => "The delete outcome is unknown. Check the conversation before trying again.",
            });
        }
        catch(Exception) {throw new SendException(true,"The delete outcome is unknown. Check the conversation before trying again.");}
    }
}
