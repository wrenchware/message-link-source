using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NativeBeeper.Presentation.ViewModels;

public sealed class ChatItemViewModel(ChatSummary chat) : ObservableObject
{
    public ChatSummary Chat { get; private set; } = chat;
    private bool isLoading;
    public bool IsLoading => isLoading;
    internal void SetLoading(bool value)
    {
        if (!SetProperty(ref isLoading, value, nameof(IsLoading))) return;
        OnPropertyChanged(nameof(AccessibleName));
    }
    // The user's labels on this row, in the order they were made. The list sets them on every rebuild; see ChatLabels.
    private IReadOnlyList<ChatLabel> labels = [];
    public IReadOnlyList<ChatLabel> Labels => labels;
    // The pills beside the time: Pinned first, then labels, two at most. See ChatLabelPills.Layout.
    private ChatLabelChip[] pills = ChatLabelPills.Layout(chat.IsPinned, []);
    public ChatLabelChip? FirstPill => pills.Length > 0 ? pills[0] : null;
    public ChatLabelChip? SecondPill => pills.Length > 1 ? pills[1] : null;
    internal void SetLabels(IReadOnlyList<ChatLabel> value)
    {
        if (labels.SequenceEqual(value)) return;
        var wasAccessible = AccessibleName;
        labels = value;
        OnPropertyChanged(nameof(Labels));
        RefreshPills();
        if (wasAccessible != AccessibleName) OnPropertyChanged(nameof(AccessibleName));
    }
    private void RefreshPills()
    {
        var next = ChatLabelPills.Layout(Chat.IsPinned, labels);
        if (next.SequenceEqual(pills)) return;
        ChatLabelChip? first = FirstPill, second = SecondPill;
        pills = next;
        if (first != FirstPill) OnPropertyChanged(nameof(FirstPill));
        if (second != SecondPill) OnPropertyChanged(nameof(SecondPill));
    }
    // Reading the old values off this instance before the summary is replaced, rather than building a throwaway view
    // model to hold them. A list rebuild calls this for every row, and it used to allocate one of these per row on a
    // thread where every collection is a full blocking one.
    public void Update(ChatSummary value)
    {
        if (Chat == value) return;
        var previous = Chat;
        bool wasGroup = IsGroup, wasUnreadFlag = HasUnread;
        string? wasOverrideId = AvatarOverrideId;
        string wasTitle = Title, wasPreview = Preview, wasTime = Time, wasUnread = Unread,
            wasNetwork = Network, wasState = State, wasPin = PinAction, wasMute = MuteAction, wasMuteText = MuteText, wasAccessible = AccessibleName;
        var wasSnoozed = IsSnoozed;
        var wasCallKind = CallKind;
        Chat = value;
        shownTime = null;
        if (!previous.Participants.SequenceEqual(value.Participants) || previous.AvatarUrl != value.AvatarUrl || previous.AvatarColor != value.AvatarColor || previous.IsPinned != value.IsPinned || previous.IsMuted != value.IsMuted || previous.SnoozedUntil != value.SnoozedUntil) OnPropertyChanged(nameof(Chat));
        if (wasGroup != IsGroup) OnPropertyChanged(nameof(IsGroup));
        if (wasOverrideId != AvatarOverrideId) OnPropertyChanged(nameof(AvatarOverrideId));
        if (wasTitle != Title) OnPropertyChanged(nameof(Title));
        if (wasPreview != Preview)
        {
            OnPropertyChanged(nameof(Preview));
            if (draftPreview is null) OnPropertyChanged(nameof(PreviewLine));
        }
        if (wasCallKind != CallKind)
        {
            OnPropertyChanged(nameof(CallKind));
            OnPropertyChanged(nameof(IsMissedCall));
            OnPropertyChanged(nameof(IsOtherPhoneCall));
            OnPropertyChanged(nameof(IsVoicemail));
            OnPropertyChanged(nameof(CallIndicatorLabel));
        }
        if (wasTime != Time) OnPropertyChanged(nameof(Time));
        if (wasUnreadFlag != HasUnread) OnPropertyChanged(nameof(HasUnread));
        if (wasUnread != Unread) OnPropertyChanged(nameof(Unread));
        if (wasNetwork != Network) OnPropertyChanged(nameof(Network));
        if (wasState != State) OnPropertyChanged(nameof(State));
        if (wasSnoozed != IsSnoozed) OnPropertyChanged(nameof(IsSnoozed));
        if (wasPin != PinAction) OnPropertyChanged(nameof(PinAction));
        if (previous.IsPinned != value.IsPinned) RefreshPills();
        if (wasMute != MuteAction) OnPropertyChanged(nameof(MuteAction));
        if (wasMuteText != MuteText) OnPropertyChanged(nameof(MuteText));
        if (wasAccessible != AccessibleName) OnPropertyChanged(nameof(AccessibleName));
    }
    public string Title => Chat.Title;
    public bool IsDraft => Chat.Id == "draft:new-chat";
    public bool IsRealChat => !IsDraft;
    public bool IsGroup => Chat.Type == "group";
    public string? AvatarChatId => IsDraft ? null : Chat.Id;
    public string? AvatarOverrideId => IsDraft ? null : NativeBeeper.Presentation.Services.AvatarOverrideIdentity.ForChat(Chat);
    public string PinAction => Chat.IsPinned ? "Unpin chat" : "Pin chat";
    public string MuteAction => Chat.IsMuted ? "Unmute chat" : "Mute chat";
    // The muted icon's tooltip: "Muted", or "Muted until 5:30 PM" for a timed mute (with the day when it is not today).
    public string MuteText => shownMuteText = Chat.MutedUntil is { } until ? "Muted until " + MuteMoment(until, DateTimeOffset.Now) : "Muted";
    private string? shownMuteText;
    /// <summary>When a timed mute ends, as the menu and the row say it: the local time, after the weekday unless today.</summary>
    public static string MuteMoment(DateTimeOffset until, DateTimeOffset now)
    {
        var local = until.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? $"{local:t}" : $"{local:ddd} {local:t}";
    }
    public string Network => IsDraft ? "Choose a recipient" : NativeBeeper.Presentation.Services.MergedConversations.IsMerged(Chat.Id) ? "Merged" : Chat.MemberChatIds.Count > 0 ? $"{Chat.MemberChatIds.Count} conversations" : Chat.Network;
    private bool showNetwork;
    // The service/account line only matters with more than one service, for merged chats and drafts.
    public bool ShowNetwork { get => showNetwork || IsDraft || Chat.MemberChatIds.Count > 0; set { if (SetProperty(ref showNetwork, value)) OnPropertyChanged(nameof(ShowNetworkBadge)); } }
    // Which network a conversation is on, as a small logo on its avatar rather than a line of words under every row.
    // Words remain only where there is no single network to name: a draft, or a conversation merged from several.
    public bool ShowNetworkWords => IsDraft || Chat.MemberChatIds.Count > 0 && !NativeBeeper.Presentation.Services.MergedConversations.IsMerged(Chat.Id);
    // The linked-conversations mark is a service mark like the others, so turning service icons off hides it too.
    public bool ShowNetworkBadge => showNetwork && !ShowNetworkWords;
    public string Preview => Chat.Preview is { IsHidden: false } message
        ? NativeBeeper.Presentation.Services.ChatPreviewText.Format(message.IsOutgoing, MessageItemViewModel.PreviewText(message), PreviewSender(message)) : "";
    // In a group, who wrote the last message: the service's name for them, else the member with that id.
    private string? PreviewSender(ChatMessage message) => IsGroup && !message.IsOutgoing
        ? NativeBeeper.Presentation.Services.ChatPreviewText.Sender(
            message.SenderName ?? Chat.Participants.FirstOrDefault(p => p.Id == message.SenderId && message.SenderId.Length > 0)?.DisplayName,
            Chat.Participants.Select(p => p.DisplayName))
        : null;
    // Unsent text in this conversation's composer, as one line, while no composer has it open; the row's second line then
    // reads "Draft: …" in place of the last message. Set by the list, from ComposerDrafts, and never kept on Chat, so the
    // saved list cache never holds it.
    private string? draftPreview;
    public string? DraftPreview => draftPreview;
    public bool HasDraftPreview => draftPreview is not null;
    public string PreviewLine => draftPreview ?? Preview;
    internal void SetDraft(string? value)
    {
        if (value is { Length: 0 }) value = null;
        if (draftPreview == value) return;
        var wasAccessible = AccessibleName;
        var had = HasDraftPreview;
        draftPreview = value;
        OnPropertyChanged(nameof(DraftPreview));
        if (had != HasDraftPreview) OnPropertyChanged(nameof(HasDraftPreview));
        OnPropertyChanged(nameof(PreviewLine));
        if (wasAccessible != AccessibleName) OnPropertyChanged(nameof(AccessibleName));
    }
    // "typing…" (or who is) in place of the second line, while someone is typing in a conversation that is not the one
    // open. Empty when nobody is. See ListTyping.
    private string typingText = "";
    public string TypingText => typingText;
    public bool IsTyping => typingText.Length > 0;
    internal void SetTyping(string? value)
    {
        value ??= "";
        if (typingText == value) return;
        var was = IsTyping;
        typingText = value;
        OnPropertyChanged(nameof(TypingText));
        if (was != IsTyping) OnPropertyChanged(nameof(IsTyping));
    }
    public string? CallKind => Chat.Preview?.CallKind;
    public bool IsMissedCall => CallKind == "missed";
    public bool IsOtherPhoneCall => CallKind is "incoming" or "outgoing" or "unanswered";
    public bool IsVoicemail => CallKind == "voicemail";
    public string CallIndicatorLabel => CallKind switch
    {
        "missed" => "Missed call",
        "incoming" => "Incoming call",
        "outgoing" => "Outgoing call",
        "unanswered" => "Unanswered call",
        "voicemail" => "Voicemail",
        _ => "",
    };
    private string? shownTime;
    public string Time => shownTime ??= RelativeTime(Chat.LastActivity, DateTimeOffset.Now);
    // The list refreshes every row once a minute, but almost every row shows something that cannot have changed: only
    // the first day's worth counts minutes and hours, and everything older is a fixed date. Recomputing and comparing
    // costs one string; raising the change cost a binding update and a layout pass on every realized row, for all of
    // them, once a minute.
    public void RefreshTime(DateTimeOffset now)
    {
        // A week's mute says its day until the day comes, and then only its time. Only a row with a timed mute looks.
        if (Chat.MutedUntil is { } until)
        {
            var text = "Muted until " + MuteMoment(until, now);
            if (text != shownMuteText)
            {
                shownMuteText = text;
                OnPropertyChanged(nameof(MuteText)); OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(AccessibleName));
            }
        }
        var current = RelativeTime(Chat.LastActivity, now);
        if (current == shownTime) return;
        shownTime = current;
        OnPropertyChanged(nameof(Time));
    }
    public static string RelativeTime(DateTimeOffset? time, DateTimeOffset now)
    {
        if (time is null) return "";
        var age = now - time.Value;
        if (age.TotalMinutes < 1) return "Just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes} min ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} hr ago";
        if (time.Value.LocalDateTime.Date == now.LocalDateTime.Date.AddDays(-1)) return "Yesterday";
        return time.Value.ToLocalTime().ToString(time.Value.LocalDateTime.Year == now.LocalDateTime.Year ? "MMM d" : "MMM d, yyyy");
    }
    public string Unread => Chat.UnreadCount > 99 ? "99+" : Chat.UnreadCount.ToString();
    public bool HasUnread => Chat.UnreadCount > 0;
    // Only a snooze still ahead counts: one already past is a reminder owed, not a quiet conversation.
    public bool IsSnoozed => Chat.SnoozedUntil > DateTimeOffset.UtcNow;
    public string State => string.Join(" · ", new[] { Chat.IsPinned ? "Pinned" : null, Chat.IsMuted ? MuteText : null,
        IsSnoozed ? $"Snoozed until {Chat.SnoozedUntil!.Value.ToLocalTime():t}" : null }.OfType<string>());
    // Every label is read, by name, because the pills beside the time show at most two of them.
    public string AccessibleName => $"{Title}, {Network}, {Chat.UnreadCount} unread{(draftPreview is null ? "" : ", draft: " + draftPreview)}. {State}" +
        (labels.Count == 0 ? "" : (State.Length > 0 ? ". " : "") + "Labels: " + string.Join(", ", labels.Select(label => label.Name)) + ".") +
        (IsLoading ? " Updating." : "");
}
