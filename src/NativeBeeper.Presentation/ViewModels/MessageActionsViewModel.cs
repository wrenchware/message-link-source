using CommunityToolkit.Mvvm.ComponentModel;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed partial class MessageActionsViewModel(IMessageActions backend, IAccessTokenStore tokens, ComposerViewModel composer,
    IMessageMutations? mutations = null, DialogGate? dialogs = null) : ObservableObject, INoticeSource
{
    private bool busy, live;
    private string status = "";
    private (ChatMessage Message, string? Profile, MessageActionOptions Options, DateTimeOffset Until)? menuCache;
    private (ChatMessage Message, string? Profile, Task<MessageActionOptions> Task)? menuRequest;
    private readonly HashSet<(string? Profile, string Chat, string Message, string Key, bool Remove)> pending = [];
    // Reactions whose outcome is unknown. Unlike a pending one the option stays on the menu, so it does not vanish without
    // a word; choosing exactly this change again says why it is not sent, and nothing else is blocked.
    private readonly HashSet<(string? Profile, string Chat, string Message, string Key, bool Remove)> uncertainReactions = [];
    public const string UnknownChangeStatus = "A previous change has an unknown outcome. Check the conversation before making another change.";
    public bool IsBusy { get => busy; private set => SetProperty(ref busy, value); }
    public string Status { get => status; private set { if (!markingAdvisory) StatusIsAdvisory = false; SetProperty(ref status, value); } }
    // See INoticeSource: shown unless it is progress or success, which the conversation itself already reports.
    public bool StatusIsAdvisory { get; private set; }
    private bool markingAdvisory;
    private void Advisory(string text)
    {
        markingAdvisory = true; StatusIsAdvisory = true;
        try { Status = text; } finally { markingAdvisory = false; }
    }
    public bool IsLive => backend is IProviderConnectionState connections ? connections.AnyConnected : live;
    private bool Available(string chatId) => backend is IProviderConnectionState connections ? connections.IsConnected(chatId) : live;
    // Composers of conversations open in their own windows; a reply goes to whichever composer has that conversation.
    private readonly List<ComposerViewModel> composers = [];
    public void AddComposer(ComposerViewModel other) => composers.Add(other);
    public void RemoveComposer(ComposerViewModel other) => composers.Remove(other);
    // Instant reaction display: raised when a reaction is submitted, and when it is known not to have been applied.
    public event Action<ChatMessage, string, bool>? ReactionSubmitting;
    public event Action<ChatMessage>? ReactionFailed;
    public void SetLive(bool value) { if (backend is IProviderConnectionState connections) value = connections.AnyConnected; if(live==value) return; live=value; menuCache=null; OnPropertyChanged(nameof(IsLive)); }
    public async Task<(MessageActionOptions Options, string? Profile)> GetMenuAsync(ChatMessage message, bool reuseRecent = false)
    {
        if (!Available(message.ChatId)) return (new(false, [], []), null);
        // Hermes takes no reply, reaction, edit or removal (the server refuses each by name), so its host is not asked.
        if (HermesThreads.Is(message.ChatId)) return (new(false, [], []), null);
        try
        {
            var profile = await NotificationIdentity.ProfileAsync(tokens);
            var cached = menuCache;
            var reuse = !backend.HasLocalActionOptions && reuseRecent && cached is { } entry && ReferenceEquals(entry.Message, message) && entry.Profile == profile && entry.Until > DateTimeOffset.UtcNow;
            Task<MessageActionOptions>? request = null;
            if (!reuse)
            {
                request = reuseRecent && menuRequest is { } running && ReferenceEquals(running.Message,message) && running.Profile==profile && !running.Task.IsCompleted
                    ? running.Task : backend.GetOptionsAsync(message.ChatId,message.Id);
                menuRequest=(message,profile,request);
            }
            var options = reuse ? cached!.Value.Options : await request!;
            if (profile != await NotificationIdentity.ProfileAsync(tokens)) return (new(false, [], []), null);
            if (!Available(message.ChatId)) return (new(false, [], []), null);
            if (!reuse) menuCache = (message, profile, options, DateTimeOffset.UtcNow.AddSeconds(10));
            foreach (var item in pending.Where(p => p.Profile == profile && p.Chat == message.ChatId && p.Message == message.Id).ToArray())
                if (item.Remove ? !options.RemoveReactions.Contains(item.Key) : options.RemoveReactions.Contains(item.Key)) pending.Remove(item);
            // An unknown reaction the service now shows as applied is settled; one it does not show stays blocked, since it may yet arrive.
            foreach (var item in uncertainReactions.Where(p => p.Profile == profile && p.Chat == message.ChatId && p.Message == message.Id).ToArray())
                if (item.Remove ? !options.RemoveReactions.Contains(item.Key) : options.RemoveReactions.Contains(item.Key)) uncertainReactions.Remove(item);
            return (options with
            {
                AddReactions = options.AddReactions.Where(key => !pending.Contains((profile, message.ChatId, message.Id, key, false))).ToArray(),
                RemoveReactions = options.RemoveReactions.Where(key => !pending.Contains((profile, message.ChatId, message.Id, key, true))).ToArray()
            }, profile);
        }
        catch (Exception) { Status = "Could not check message actions. Reconnect and try again."; return (new(false, [], []), null); }
    }
    public async Task ReplyAsync(ChatMessage message, string? profile)
    {
        if (!Available(message.ChatId) || IsBusy) return;
        var menu = await GetMenuAsync(message, reuseRecent: true);
        if (!Available(message.ChatId) || IsBusy || menu.Profile != profile || !menu.Options.CanReply) return;
        IsBusy = true;
        try
        {
            if (profile != await NotificationIdentity.ProfileAsync(tokens)) return;
            // Selecting a local quote can reuse the recent menu. Sending still validates remotely.
            if (profile == await NotificationIdentity.ProfileAsync(tokens))
            {
                var preview = MessageItemViewModel.PreviewText(message);
                // The quoted message appearing above the composer is the confirmation; only failing to find one is news.
                var selected = await composer.SelectReplyAsync(message.ChatId, message.Id, preview);
                foreach (var other in composers.ToArray())
                    if (!selected) selected = await other.SelectReplyAsync(message.ChatId, message.Id, preview);
                if (selected)
                    Advisory("Reply selected.");
                else Status = "Open that conversation to reply.";
            }
        }
        catch (Exception) { Status = "This message cannot currently be replied to."; }
        finally { IsBusy = false; }
    }
    /// <summary>Adds or removes a reaction once. Accepted: submitted and waiting for confirmation. Rejected: nothing was
    /// submitted, or the provider refused it (Status says why when there is a reason). Unknown: it may have been applied.</summary>
    public async Task<MessageMutationOutcome> ReactAsync(ChatMessage message, string key, bool remove, string? profile)
    {
        var operation = (profile, message.ChatId, message.Id, key, remove);
        if (uncertainReactions.Contains(operation)) { Status = UnknownChangeStatus; return MessageMutationOutcome.Unknown; }
        if (!Available(message.ChatId) || IsBusy || pending.Contains(operation)) return MessageMutationOutcome.Rejected;
        IsBusy = true;
        try
        {
            if (profile != await NotificationIdentity.ProfileAsync(tokens)) return MessageMutationOutcome.Rejected;
            if (pending.Count >= 128) { Status = "Review pending reactions before adding more."; return MessageMutationOutcome.Rejected; }
            pending.Add(operation);
            menuCache = null;
            ReactionSubmitting?.Invoke(message, key, remove);
            await backend.ReactAsync(message.ChatId, message.Id, key, remove);
            Advisory("Reaction change queued. Waiting for confirmation.");
            return MessageMutationOutcome.Accepted;
        }
        // An unknown outcome keeps the prediction until the service's update or its timeout.
        catch (SendException ex)
        {
            if (!ex.OutcomeUnknown) { pending.Remove(operation); ReactionFailed?.Invoke(message); }
            else { pending.Remove(operation); uncertainReactions.Add(operation); }
            Status = ex.Message;
            return ex.OutcomeUnknown ? MessageMutationOutcome.Unknown : MessageMutationOutcome.Rejected;
        }
        catch (Exception) { pending.Remove(operation); ReactionFailed?.Invoke(message); Status = "Could not prepare this reaction. Refresh the message and try again."; return MessageMutationOutcome.Rejected; }
        finally { IsBusy = false; }
    }
}
