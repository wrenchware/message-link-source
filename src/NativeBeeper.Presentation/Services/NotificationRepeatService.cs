using NativeBeeper.Core.Services;
using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

// Repeats only notifications this running session actually delivered. No message content is retained or persisted;
// every repeat checks the owning source again and keeps the original action identity, so Reply cannot replay.
public sealed class NotificationRepeatService : IDisposable
{
    private sealed record Pending(NotificationRecord Record, DateTimeOffset Due);
    private readonly NotificationObserver observer;
    private readonly IChatReader chats;
    private readonly IMessageSender messages;
    private readonly IAccessTokenStore tokens;
    private readonly INotificationStore store;
    private readonly INotificationPresenter presenter;
    private readonly NotificationPreferences preferences;
    private readonly MergedConversations? merges;
    private readonly TimeProvider clock;
    private readonly INotificationAccountScope? accountScopes;
    // A conversation read here while private reading kept its service from being told is read, whatever the service says.
    private readonly ReadHereMarks? readHere;
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = [];
    private readonly SemaphoreSlim checks = new(1);

    public NotificationRepeatService(NotificationObserver observer, IChatReader chats, IMessageSender messages,
        IAccessTokenStore tokens, INotificationStore store, INotificationPresenter presenter, NotificationPreferences preferences,
        MergedConversations? merges = null, TimeProvider? clock = null, INotificationAccountScope? accountScopes = null, ReadHereMarks? readHere = null)
    {
        this.observer = observer; this.chats = chats; this.messages = messages; this.tokens = tokens;
        this.store = store; this.presenter = presenter; this.preferences = preferences; this.merges = merges;
        this.clock = clock ?? TimeProvider.System;
        this.accountScopes = accountScopes;
        this.readHere = readHere;
        observer.Published += Published;
    }

    private void Published(object? sender, NotificationContent content)
    {
        if (content.IsRepeat || content.Record.IsTest || !preferences.Enabled || preferences.RepeatInterval is not { } interval || interval <= TimeSpan.Zero) return;
        var key = NotificationIdentity.Hash(content.Record.Profile + "\n" + content.Record.ChatId);
        lock (gate)
        {
            pending[key] = new(content.Record, clock.GetUtcNow() + interval);
            while (pending.Count > 1000) pending.Remove(pending.Keys.First());
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await DeliverDueAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception) { /* Notification work must not stop messaging. */ }
            try { if (!await timer.WaitForNextTickAsync(cancellationToken)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken = default)
    {
        await checks.WaitAsync(cancellationToken);
        try
        {
            if (!preferences.Enabled || preferences.RepeatInterval is not { } interval || interval <= TimeSpan.Zero)
            { lock (gate) pending.Clear(); return 0; }
            var now = clock.GetUtcNow();
            KeyValuePair<string, Pending>[] due;
            lock (gate) due = pending.Where(pair => pair.Value.Due <= now).OrderByDescending(pair => pair.Value.Record.CreatedAt).ToArray();
            var delivered = 0;
            var deliveredGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (key, item) in due)
            {
                // Spend this interval before any I/O. A new notification may replace it while an old read awaits.
                lock (gate)
                {
                    if (!pending.TryGetValue(key, out var current) || current != item) continue;
                    pending[key] = item with { Due = now + interval };
                }
                try
                {
                    var original = item.Record;
                    // A service turned off since ends its repeats here, before anything is read for them.
                    if (now - original.CreatedAt > TimeSpan.FromDays(1) || observer.RepeatsStopped(original.Id) || !await AccountCurrentAsync(original, cancellationToken))
                    { Forget(key, original.Id); continue; }
                    var record = await store.UpdateAsync(original.Id, prior => (prior, prior), cancellationToken);
                    if (record is null || record.ReplyState != NotificationActionState.None || record.ReadState != NotificationActionState.None)
                    { Forget(key, original.Id); continue; }
                    if (deliveredGroups.Contains(record.GroupId ?? key)) continue;
                    if (messages is IProviderConnectionState connection && !connection.IsConnected(record.ChatId)) continue;
                    var chat = await CurrentChatAsync(record.ChatId, cancellationToken);
                    if (!Eligible(chat, now))
                    { Forget(key, original.Id); continue; }
                    if (Quiet()) continue;
                    var message = await messages.ResolveMessageAsync(record.ChatId, record.MessageId, cancellationToken);
                    if (message.ChatId != record.ChatId || message.Id != record.MessageId || message.IsOutgoing || message.IsDeleted || message.IsHidden || message.Type == "system")
                    { Forget(key, original.Id); continue; }
                    if (!await AccountCurrentAsync(record, cancellationToken) || Quiet()) continue;
                    // Resolving a message can wait for a helper. Recheck read/mute/snooze and action claims after
                    // that wait; a reply or Mark read clicked in the meantime must cancel this repeat.
                    chat = await CurrentChatAsync(record.ChatId, cancellationToken);
                    var latestRecord = await store.UpdateAsync(record.Id, prior => (prior, prior), cancellationToken);
                    if (!Eligible(chat, clock.GetUtcNow()) || latestRecord is null || latestRecord.ReplyState != NotificationActionState.None || latestRecord.ReadState != NotificationActionState.None)
                    { Forget(key, original.Id); continue; }
                    if (!await AccountCurrentAsync(record, cancellationToken) || Quiet()) continue;
                    var currentGroup = NotificationIdentity.Hash(record.Profile + "\n" + (merges?.DisplayId(record.ChatId) ?? record.ChatId));
                    if (record.GroupId is not null && record.GroupId != currentGroup) { Forget(key, original.Id); continue; }
                    lock (gate)
                        if (!pending.TryGetValue(key, out var latest) || latest.Record.Id != record.Id) continue;
                    var title = chat.Type == "group" ? $"{message.SenderName ?? "New message"} · {chat.Title}" : chat.Title;
                    var preview = string.IsNullOrWhiteSpace(message.Text) ? message.Attachments.Count > 0 ? "Attachment" : "New message" : MessageText.PlainText(message);
                    await presenter.ShowMessageAsync(new(record, NotificationObserver.Clip(title, 100), NotificationObserver.Clip(preview, 240), chat.Network,
                        Attribution: chat.UnreadCount > 1 ? $"Still unread · {chat.UnreadCount} messages" : "Still unread", IsRepeat: true));
                    delivered++;
                    deliveredGroups.Add(record.GroupId ?? key);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception) { /* A temporarily unavailable source gets another read next interval, never a send. */ }
            }
            return delivered;
        }
        finally { checks.Release(); }
    }

    private async Task<ChatSummary> CurrentChatAsync(string chatId, CancellationToken token)
    {
        var chat = await chats.GetChatAsync(chatId, token);
        if (chat.Id != chatId) throw new InvalidOperationException("Conversation changed during notification lookup.");
        if (readHere is not null) chat = readHere.Apply(chat);
        if (merges is not null)
        {
            await merges.EnsureLoadedAsync();
            if (merges.Find(merges.DisplayId(chat.Id)) is { } group)
                chat = chat with { IsMuted = group.Muted, IsArchived = group.Archived, Title = group.Title, SnoozedUntil = group.SnoozedUntil };
        }
        return chat;
    }
    private static bool Eligible(ChatSummary chat, DateTimeOffset now) =>
        chat.UnreadCount > 0 && !chat.IsMuted && !chat.IsArchived && !chat.IsSpam && !(chat.SnoozedUntil > now);
    private async Task<bool> AccountCurrentAsync(NotificationRecord record, CancellationToken token) =>
        record.Profile == await NotificationIdentity.ProfileAsync(tokens, token) &&
        (accountScopes is null || record.SourceScope is not null && record.SourceScope == await accountScopes.ForChatAsync(record.ChatId, token));

    private bool Quiet() => !preferences.Enabled || preferences.RepeatInterval is null || preferences.WindowFocused ||
        preferences.AwayReason() is not null || preferences.WindowsBannerState() is not null;
    private void Forget(string key, string id)
    { lock (gate) if (pending.TryGetValue(key, out var item) && item.Record.Id == id) pending.Remove(key); }
    public void Dispose() { observer.Published -= Published; lock (gate) pending.Clear(); }
}
