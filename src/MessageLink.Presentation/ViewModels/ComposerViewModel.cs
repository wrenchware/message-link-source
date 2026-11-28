using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

public sealed partial class ComposerViewModel : ObservableObject, INoticeSource, IDisposable
{
    private readonly IMessageSender sender;
    private readonly Outbox outbox;
    private readonly IMessageActions? messageActions;
    // A reply chosen before an edit began is kept, out of sight, and is there again when the edit ends.
    public bool HasReply => chatId is not null && editing is null && replies.ContainsKey(chatId);
    public bool CanChooseRoute => !HasReply && editing is null;
    public string ReplyPreview => chatId is not null && replies.TryGetValue(chatId, out var reply) ? reply.Preview : "";
    public bool SelectReply(string targetChat, string messageId, string preview)
    {
        if (targetChat != chatId || messageActions is null || IsSavingEdit) return false;
        // Choosing Reply while editing leaves the edit: the reply is for a new message.
        EndEdit();
        replies[targetChat] = (messageId, preview); NotifyReply(); ScheduleDraftSave(); return true;
    }
    public Func<string, Task<bool>>? PrepareReplyRouteAsync { get; set; }
    public async Task<bool> SelectReplyAsync(string targetChat, string messageId, string preview)
    {
        if (targetChat != chatId && (PrepareReplyRouteAsync is null || !await PrepareReplyRouteAsync(targetChat))) return false;
        return SelectReply(targetChat, messageId, preview);
    }
    private void NotifyReply() { NoteQueuedReplyChange(); OnPropertyChanged(nameof(HasReply)); OnPropertyChanged(nameof(ReplyPreview)); OnPropertyChanged(nameof(CanChooseRoute)); }
    [RelayCommand]
    private void CancelReply() { if (chatId is not null) replies.Remove(chatId); NotifyReply(); ScheduleDraftSave(); }
    private CancellationTokenSource? permissionRequest;
    private string? chatId;
    public string? ChatId => chatId;
    private long generation;
    private bool allowed;
    private int maxLength = 65536;
    [ObservableProperty] private string text = "";
    [ObservableProperty] private string status = "Select a conversation to write a message.";
    // See INoticeSource: shown unless it is progress or success, which the conversation itself already reports.
    public bool StatusIsAdvisory { get; private set; }
    private bool markingAdvisory;
    partial void OnStatusChanging(string value) { if (!markingAdvisory) StatusIsAdvisory = false; }
    private void Advisory(string text)
    {
        markingAdvisory = true; StatusIsAdvisory = true;
        try { Status = text; } finally { markingAdvisory = false; }
    }
    [ObservableProperty] private bool isLive;
    [ObservableProperty] private bool isChecking;
    [ObservableProperty] private bool hasConversation;
    // A conversation can already be visible while its saved draft is still being restored. Keep the editor and every
    // send path closed until that restore belongs to the current selection and has completed.
    [ObservableProperty] private bool isReady;
    // Read-only leaves WinUI's enabled/focus visuals alone while a conversation-to-conversation switch restores its
    // draft. Toggling IsEnabled made the composer visibly flash even though the safety lock lasted only a disk write.
    public bool IsReadOnly => !IsReady || IsSavingEdit;
    partial void OnIsReadyChanged(bool value) => OnPropertyChanged(nameof(IsReadOnly));
    public bool HasDrafts => book.HoldsWords || CurrentDraft.Length > 0;
    private readonly InterruptedSends? interruptedSends;
    public ComposerViewModel(IMessageSender sender, Outbox outbox, IMessageActions? messageActions = null, PrivateState? privateState = null,
        InterruptedSends? interruptedSends = null, ComposerDrafts? draftIndex = null, ScheduledSends? scheduledSends = null,
        DraftOwners? draftOwners = null)
    {
        this.sender = sender; this.outbox = outbox;
        this.messageActions = messageActions;
        this.interruptedSends = interruptedSends;
        this.scheduledSends = scheduledSends;
        // One process keeps every conversation's draft in one place (the list's, given); a composer given none, or over another store, keeps its own.
        alone = draftIndex is null || !draftIndex.Accepts(privateState);
        book = alone ? new ComposerDrafts(draftOwners) : draftIndex!;
        book.Attach(this, privateState, draftOwners);
        outbox.Changed += OutboxChanged;
        outbox.Undone += OutboxUndone;
        if (interruptedSends is not null) interruptedSends.Changed += OutboxChanged;
    }
    private void OutboxChanged(object? sender, string id)
    {
        if (id != chatId && id.Length != 0) return;
        NotifyRecoveryState();
    }
    public bool HasRejectedMessage => CanRestore();
    // Sends this conversation still had open when the app last closed, which no lookup has since confirmed.
    public bool HasInterruptedSend => chatId is not null && interruptedSends?.ForChat(chatId).Count > 0;
    public string InterruptedSendTitle => InterruptedSendTitleOf(chatId is null || interruptedSends is null ? [] : interruptedSends.ForChat(chatId));
    public static string InterruptedSendTitleOf(IReadOnlyList<InterruptedSend> sends) =>
        sends.Count > 0 && sends.All(send => send.OnServer && send.Refused) ? (sends.Count == 1 ? "Message not sent" : "Messages not sent") : "Message may not have sent";
    public string InterruptedSendText => InterruptedSendTextOf(chatId is null || interruptedSends is null ? [] : interruptedSends.ForChat(chatId));
    public static string InterruptedSendTextOf(IReadOnlyList<InterruptedSend> sends)
    {
        // Sends a server made that may not have gone (stage 6 slice 11): the server's word, not "before Messages closed". Since its ordinary sends are
        // recorded there too (from this PC or the phone), only the ones it fired from the schedule say "scheduled".
        if (sends.Count > 0 && sends.All(send => send.OnServer))
        {
            var kind = sends.All(send => send.Scheduled) ? "scheduled message" : "message";
            // A send its service refused went nowhere; saying it may have gone made a plain refusal read as a doubt (owner's screenshot, 2026-10-29).
            if (sends.All(send => send.Refused))
                return sends.Count == 1
                    ? $"A {kind} sent {SentWhen(sends[0].CreatedAt)} was turned down by its service, so nothing was sent. You can send it again."
                    : $"{sends.Count} {kind}s were turned down by their service, so nothing was sent. You can send them again.";
            return sends.Count == 1
                ? $"A {kind} sent {SentWhen(sends[0].CreatedAt)} may not have gone through. Check the conversation before sending it again."
                : $"{sends.Count} {kind}s may not have gone through. Check the conversation before sending them again.";
        }
        return sends.Count switch
        {
            0 => "",
            1 => $"A message sent {SentWhen(sends[0].CreatedAt)} wasn't confirmed before Message Link closed. Check the conversation before sending it again.",
            _ => $"{sends.Count} messages sent before Message Link closed weren't confirmed. Check the conversation before sending them again."
        };
    }
    // A warning is kept up to a week, so a bare time can point at the wrong day (owner, 2026-10-26: one "sent at 5:19 PM" was days old).
    public static string SentWhen(DateTimeOffset at, DateTimeOffset? now = null)
    {
        var local = at.ToLocalTime();
        var today = (now ?? DateTimeOffset.Now).ToLocalTime().Date;
        if (local.Date == today) return $"at {local:t}";
        if (local.Date == today.AddDays(-1)) return $"yesterday at {local:t}";
        return $"on {local:ddd, MMM d} at {local:t}";
    }
    [RelayCommand]
    private async Task DismissInterruptedSendAsync() { if (chatId is not null && interruptedSends is not null) await interruptedSends.DismissAsync(chatId); }
    private void NotifyRecoveryState()
    {
        RestoreRejectedCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasRejectedMessage));
        OnPropertyChanged(nameof(HasInterruptedSend)); OnPropertyChanged(nameof(InterruptedSendText)); OnPropertyChanged(nameof(InterruptedSendTitle));
    }
    public string LengthHint => Text.Length > maxLength ? $"Message exceeds this conversation's {maxLength:N0} character limit." : "";
    partial void OnTextChanged(string value)
    {
        // Measured in two disjoint parts so a keystroke that costs too much can be attributed: what the composer itself
        // does in reaction, and what scheduling the delayed draft save costs. See UiActivity.
        var reacting = MessageLink.Presentation.Services.UiActivity.Start();
        if (queuedSend is { } queued && queued.Text != value) queuedSend = null;
        SendCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(LengthHint)); OnPropertyChanged(nameof(CanSendOrQueue));
        MessageLink.Presentation.Services.UiActivity.Composer(reacting);
        var saving = MessageLink.Presentation.Services.UiActivity.Start();
        ScheduleDraftSave();
        MessageLink.Presentation.Services.UiActivity.Draft(saving);
    }
    partial void OnIsLiveChanged(bool value)
    {
        if (!value) allowed = false;
        SendCommand.NotifyCanExecuteChanged();
        if (value) _ = TrySendQueuedAsync();
    }
    // Enter while this conversation's send check is still running (right after launch or a reconnect) sends once the
    // check allows it. It is the one send the user asked for, dropped if the text or conversation changes first. The reply
    // chosen at Enter is kept with it: changed, cancelled or newly chosen meanwhile, the send is dropped and the draft stays,
    // since sending would quote a message the user didn't press Enter on (#86).
    private (string ChatId, string Text, string? ReplyId, long QueuedAt, string WaitingFor)? queuedSend;
    public const string QueuedReplyChangedStatus = "The reply changed, so the message wasn't sent. Press Enter to send it.";
    public bool CanSendOrQueue => !SchedulingHere && IsReady && editing is null && chatId is not null && !string.IsNullOrWhiteSpace(Text) && Text.Length <= maxLength;
    // Durations only, for Settings > Troubleshooting.
    public string SendDelaySummary { get; private set; } = "No sends from the composer measured yet this session.";
    public async Task SendOrQueueAsync()
    {
        if (CanSend()) { queuedSend = null; SendDelaySummary = "Last composer send started immediately after Enter."; await SendCommand.ExecuteAsync(null); return; }
        if (!CanSendOrQueue) return;
        queuedReplyChanged = false;
        queuedSend = (chatId!, Text, SelectedReplyId(chatId!), System.Diagnostics.Stopwatch.GetTimestamp(), !IsLive ? "connection" : "send check");
        Advisory("Sending as soon as this conversation is ready…");
        // A check that already failed (for example while the phone was still connecting) runs again.
        if (!IsChecking && !allowed) await RefreshPermissionAsync();
    }
    private async Task TrySendQueuedAsync()
    {
        if (queuedSend is not { } queued || IsChecking || !IsLive || !allowed) return;
        queuedSend = null;
        if (queued.ChatId != chatId || queued.Text != Text || !CanSend()) return;
        if (queuedReplyChanged || queued.ReplyId != SelectedReplyId(queued.ChatId)) { Status = QueuedReplyChangedStatus; return; }
        SendDelaySummary = $"Last composer send waited {System.Diagnostics.Stopwatch.GetElapsedTime(queued.QueuedAt).TotalSeconds:0.0} s after Enter (first waiting for the {queued.WaitingFor}).";
        await SendCommand.ExecuteAsync(null);
    }
    private string? SelectedReplyId(string target) => replies.TryGetValue(target, out var reply) ? reply.Id : null;
    // Marked the moment the reply changes, so cancelling it and choosing the same message again does not bring the send back;
    // said when the check allows the send, after the check's own hint.
    private bool queuedReplyChanged;
    private void NoteQueuedReplyChange()
    {
        if (queuedSend is { } queued && queued.ReplyId != SelectedReplyId(queued.ChatId)) queuedReplyChanged = true;
    }
    partial void OnIsCheckingChanged(bool value) => SendCommand.NotifyCanExecuteChanged();
    private bool CanSend() => !SchedulingHere && IsReady && editing is null && allowed && IsLive && !IsChecking && !string.IsNullOrWhiteSpace(Text) && Text.Length <= maxLength && WrittenAsSignedInAccount();
    private bool CanRestore() => chatId is not null && editing is null && outbox.ForChat(chatId).Any(m => m.State == OutgoingState.Rejected && m.CanRestoreText);

    private bool reopening;
    public async Task SelectAsync(string? id)
    {
        var selectionVersion = ++draftSelection;
        queuedSend = null;
        // Switching conversation ends an edit; the draft it set aside is what is kept for the conversation being left.
        EndEdit();
        // Capture the old draft and revoke its target before the first await. The history pane may already show the
        // next chat, so leaving chatId/allowed live even for one asynchronous write can send to the previous person.
        var previous = chatId;
        if (previous is not null)
        {
            if (Text.Length == 0) drafts.Remove(previous); else drafts[previous] = Text;
        }
        chatId = null;
        IsReady = false;
        allowed = false;
        generation++;
        permissionRequest?.Cancel();
        IsChecking = false;
        HasConversation = id is not null;
        restoringDraft = true;
        // The next conversation's draft goes in now, when it is already known, rather than after the previous one is
        // written: the box stood empty for that write, and the composer dropped to one line and grew back between two
        // multi-line drafts. The write reads the previous draft from drafts, not from the box, since chatId is cleared.
        // What is held for a Messenger conversation is shown only under the account it was written as (the lookup below
        // settles which account is signed in; this is the last one known).
        Text = id is not null && book.HeldAsSignedInAccount(id) && drafts.TryGetValue(id, out var known) ? known : "";
        // What the box shows from here on is set by the drafts as they open it; anything else in it then was typed meanwhile, and is kept.
        var shown = Text;
        NotifyReply();
        SendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendOrQueue));
        // The list's draft line. The conversation opened stops showing its draft now, in the same turn as its text goes
        // into the box; the one left keeps its hold until its draft is written below, then shows it in one change.
        if (!holdsEnded) book.Holding(opening, id);
        // Only the conversation being left can have unsaved text, so the switch waits for that one write.
        if (previous is not null) await FlushDraftAsync(previous); else book.CancelPendingOf(this);
        if (selectionVersion != draftSelection) return;
        if (!holdsEnded) { book.Holding(this, id); book.Holding(opening, null); }
        // The drafts open it: as the Messenger account signed in now, with the saved draft read the first time.
        var opened = await book.OpenAsync(this, id, shown, () => selectionVersion == draftSelection);
        if (selectionVersion != draftSelection) return;
        chatId = id;
        restoringDraft = false;
        IsReady = id is not null;
        NotifyReply();
        SendCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSendOrQueue));
        // The Messenger account changed while the draft was read: what is shown was written as the one before, so it is
        // opened again as this one (once: an account that cannot be read stays as it is).
        if (id is not null && !reopening && !book.HeldAsSignedInAccount(id))
        {
            reopening = true;
            try { await SelectAsync(id); } finally { reopening = false; }
            return;
        }
        // What was carried over, or typed while this ran, is now the known account's, and saved as its own at the next pause.
        if (opened.Save) ScheduleDraftSave();
        await RefreshPermissionAsync();
    }
    // Before another window opens this conversation: save its draft and let go of the box, so the other window opens it
    // from the drafts (ComposerDrafts.ReleasedAsync) and nothing here writes over what it writes.
    //
    // The list's hold on it (ComposerDrafts.Holding) is deliberately kept here. The conversation is on its way to another
    // composer, which takes the hold as its window opens; letting go now would show the row's draft line for the moments
    // in between and then take it away again. The hold ends when this composer selects something else, is cleared or is
    // disposed — which is how a conversation window closing hands its draft back to the list.
    public async Task<bool> ReleaseAsync(string id)
    {
        var releasingCurrent = id == chatId;
        var releaseVersion = draftSelection;
        if (releasingCurrent)
        {
            EndEdit();
            queuedSend = null;
            IsReady = false;
            allowed = false;
            generation++;
            permissionRequest?.Cancel();
            IsChecking = false;
            if (Text.Length == 0) drafts.Remove(id); else drafts[id] = Text;
            SendCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSendOrQueue));
        }
        var saved = await FlushDraftAsync(id);
        // Words written as another account that could not be saved are this composer's to keep, not another window's to miss.
        if (book.Parks(id)) saved = false;
        if (!saved || releaseVersion != draftSelection)
        {
            if (releasingCurrent && releaseVersion == draftSelection && chatId == id)
            {
                IsReady = true;
                await RefreshPermissionAsync();
            }
            return false;
        }
        if (releasingCurrent)
        {
            draftSelection++;
            chatId = null;
            HasConversation = false;
            restoringDraft = true;
            Text = "";
            restoringDraft = false;
            NotifyReply();
        }
        await book.ReleasedAsync(id, alone);
        return true;
    }
    // Whether it took anything: text edited since it was submitted stays, and then there is nothing to put back. Words
    // typed after it while the send got going (a photo's checks) are not an edit: the submitted words go from the front
    // and what was typed stays, instead of the whole box staying as a draft of words already sent (2026-10-03).
    public bool ClearSubmittedText(string id, string submitted)
    {
        var taken = false;
        if (chatId == id && editing is { } session) { if (session.SavedDraft == submitted) { session.SavedDraft = ""; taken = true; } }
        else if (chatId == id && Remainder(Text, submitted) is { } rest) { Text = rest; taken = true; }
        if (drafts.TryGetValue(id, out var stored) && Remainder(stored, submitted) is { } left)
        { if (left.Length == 0) drafts.Remove(id); else drafts[id] = left; taken = true; }
        return taken;
    }
    private static string? Remainder(string current, string submitted) =>
        current == submitted ? "" :
        submitted.Length > 0 && current.StartsWith(submitted, StringComparison.Ordinal) ? current[submitted.Length..].TrimStart() : null;
    // Text a send took out of the composer and then definitely did not send (a rejected captioned photo). It goes back
    // where it came from: into the box, after anything typed meanwhile, as Restore does; or into that conversation's
    // draft when another one is open now. Never for an uncertain send, which may already have arrived.
    public void RestoreSubmittedText(string id, string submitted)
    {
        if (string.IsNullOrEmpty(submitted)) return;
        // While an edit is in the box the words join the draft it set aside, which comes back when the edit ends.
        if (chatId == id && editing is { } session)
        {
            session.SavedDraft = session.SavedDraft.Length == 0 ? submitted : session.SavedDraft + Environment.NewLine + submitted;
            _ = FlushDraftAsync(id);
            return;
        }
        if (chatId == id) { Text = Text.Length == 0 ? submitted : Text + Environment.NewLine + submitted; return; }
        book.Restore(this, id, submitted);
    }
    // Undo send: a send this composer made came back before reaching the provider. Its words go back where they came
    // from (RestoreSubmittedText), with the message they replied to.
    private void OutboxUndone(object? sender, Outbox.UndoneSend undone)
    {
        if (!ReferenceEquals(undone.Owner, this)) return;
        var message = undone.Message;
        // Written as an account replaced since: the words are in its draft, where it finds them when it is back. This box is another account's.
        var otherAccount = WaitingAsOtherAccount(message);
        var waited = ForgetWaiting(message);
        if (otherAccount) return;
        if (message.ReplyToId is not null && !replies.ContainsKey(message.ChatId))
        { replies[message.ChatId] = (message.ReplyToId, "Reply to an earlier message"); NotifyReply(); }
        // This composer's window has closed since: the words go where the conversation's draft is now (another box, or the one
        // held). Drafts of its own alone kept them in the saved row, where the next composer reads them.
        if (holdsEnded) { if (!alone) book.Restore(this, message.ChatId, message.Text); return; }
        RestoreSubmittedText(message.ChatId, message.Text);
        // At once rather than at the next pause: an Undo as the wait ended may meet its draft being cleared, and the gate puts this write after that one.
        if (!waited && chatId == message.ChatId) _ = FlushDraftAsync(message.ChatId);
    }
    /// <summary>
    /// Undo send at quit (2026-10-04): a message still in its wait when Messages quits goes back to its conversation's
    /// draft rather than being sent (HeldSends). The words follow anything already there, as Restore puts them, with the
    /// message they replied to; then that one draft is written, since nothing after a quit writes it. A conversation this
    /// composer never loaded has its saved draft read first, so the words join it instead of replacing it. False when the
    /// draft could not be written, or its saved copy could not be read (left as it is rather than overwritten).
    /// </summary>
    public async Task<bool> ReturnHeldAsync(OutgoingMessage message)
    {
        var id = message.ChatId;
        // Not in this box: the drafts put them where the conversation's words are (another box, or the draft held).
        if (id != chatId) return await book.ReturnHeldAsync(this, message);
        if (message.ReplyToId is not null && !replies.ContainsKey(id))
        { replies[id] = (message.ReplyToId, "Reply to an earlier message"); NotifyReply(); }
        if (editing is { } session) session.SavedDraft = ComposerDrafts.Follow(session.SavedDraft, message.Text);
        else Text = ComposerDrafts.Follow(Text, message.Text);
        return await FlushDraftAsync(id);
    }
    public async Task RefreshPermissionAsync()
    {
        if (holdsEnded) return; // Disposed: a switch still finishing saves its draft but asks no provider anything.
        var version = ++generation;
        permissionRequest?.Cancel(); permissionRequest?.Dispose(); permissionRequest = new();
        allowed = false; SendCommand.NotifyCanExecuteChanged();
        NotifyRecoveryState();
        if (chatId is null) { Advisory("Select a conversation to write a message."); IsChecking = false; return; }
        IsChecking = true; Advisory("Checking send permissions…");
        if (sender is IProviderConnectionState connections) IsLive = connections.IsConnected(chatId);
        // A refusal that is not "still connecting" means nothing is coming that would let the queued message go, so it
        // must not wait for a connection somebody may make much later, when the user no longer expects it to send.
        var refusedOutright = false;
        try
        {
            var permission = await sender.GetSendPermissionAsync(chatId, permissionRequest.Token);
            if (version != generation) return;
            refusedOutright = !permission.Allowed && !permission.Pending;
            allowed = permission.Allowed; maxLength = Math.Min(permission.MaxTextLength ?? 65536, 65536);
            OnPropertyChanged(nameof(LengthHint));
            // The keyboard hint is for screen readers and the tooltip. In the notice bar it was the same sentence on every
            // conversation, forever — and a reason the conversation cannot be written to is the opposite case.
            // A caution is the exception: something the user should know before sending here (Google Messages when the
            // phone says it is not the default SMS app), so it is a notice rather than the quiet hint.
            if (permission.Allowed && !string.IsNullOrWhiteSpace(permission.Caution)) Status = permission.Caution;
            else if (permission.Allowed) Advisory("Enter to send · Shift+Enter for a new line");
            // A provider still connecting refreshes this when it is ready; the conversation already shows its progress.
            else if (permission.Pending) Advisory(permission.Reason);
            else Status = permission.Reason;
        }
        catch (OperationCanceledException) { }
        // A provider still connecting, or renewing its sign-in, is not a refusal: said quietly like a pending answer, and
        // checked again when it connects. As a notice, "renewing its sign-in" appeared at launch and went seconds later.
        catch (MessageLink.Core.Backend.BackendException ex) when (ex.Waits) { if (version == generation) Advisory(ex.Message); }
        catch (MessageLink.Core.Backend.BackendException ex) { if (version == generation) Status = ex.Message; }
        catch (Exception) { if (version == generation) Status = "This service is temporarily unavailable. Sending will return when the connection recovers."; }
        finally { if (version == generation) { IsChecking = false; SendCommand.NotifyCanExecuteChanged(); } }
        if (version == generation)
        {
            if (IsLive && !allowed || refusedOutright) queuedSend = null;
            await TrySendQueuedAsync();
        }
    }
    /// <summary>
    /// Raised with the conversation a message from this composer is about to go to, just before it reaches the outbox. The
    /// conversation view it belongs to tells its history (a send from here shows every service of a filtered merged
    /// conversation again); sends from anywhere else — Send later, the chat console — raise nothing here.
    /// </summary>
    public event EventHandler<string>? Sending;
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (!CanSend() || chatId is null) return;
        if (Status == QueuedReplyChangedStatus) Advisory("Enter to send · Shift+Enter for a new line");
        var target = chatId; var body = Text;
        var reply = replies.TryGetValue(target, out var selectedReply) ? selectedReply : default;
        // With Undo send on the words stay in the saved draft until the wait ends: a crash in it lost them when the draft was cleared here (audit, 2026-11-01).
        var words = outbox.Hold > TimeSpan.Zero ? book.Wait(target, body, reply.Id, reply.Preview) : null;
        if (words is not null) ownWaits.Add(words.LocalId);
        Func<Task<bool>>? holdEnding = words is null ? null : async () => { try { return await book.LetGoOfWaitingAsync(target, words); } finally { Waited(words.LocalId); } };
        replies.Remove(target); NotifyReply();
        Text = ""; drafts.Remove(target);
        // The pending message is added to the outbox — and so to the conversation — before anything is awaited, so it
        // appears the moment Enter is pressed. Clearing the saved draft runs alongside rather than in front of it.
        var cleared = FlushDraftAsync(target);
        Sending?.Invoke(this, target);
        try
        {
            if (reply.Id is not null && messageActions is not null)
                await outbox.SendOperationAsync(target, body, ct => messageActions.ReplyAsync(target, reply.Id, body, ct), true, reply.Id, holdFor: this, localId: words?.LocalId, holdEnding: holdEnding);
            else await outbox.SendAsync(target, body, this, holdEnding, words?.LocalId);
            await cleared;
        }
        catch (Exception) { if (words is not null) { book.Unwait(target, words); Waited(words.LocalId); } drafts[target] = body; if (reply.Id is not null) replies.TryAdd(target, reply); NotifyReply(); if (chatId == target && Text.Length == 0) Text = body; Status = "Review pending sends before sending more messages."; }
    }
    [RelayCommand(CanExecute = nameof(CanRestore))]
    private void RestoreRejected()
    {
        if (chatId is null) return;
        var restored = outbox.TakeRejectedOperation(chatId);
        if (restored is not null)
        {
            Text = Text.Length == 0 ? restored.Text : Text + Environment.NewLine + restored.Text;
            if (restored.ReplyToId is not null) replies[chatId] = (restored.ReplyToId, "Reply to an earlier message");
            NotifyReply();
        }
    }
    /// <summary>
    /// Edit on a refused message's row: its words (and its reply) back in the draft of the conversation it was sent to, as Send later's
    /// Edit does. In a merged conversation whose box is set to another service they wait in that service's draft rather than going out
    /// through the other one, and an edit of a sent message in the box ends first, so they can't be published as that edit.
    /// </summary>
    public bool EditNotSent(string localId)
    {
        if (IsSavingEdit || outbox.TakeRejectedText(localId) is not { } restored) return false;
        if (restored.ChatId == chatId) EndEdit();
        if (restored.ReplyToId is not null && !replies.ContainsKey(restored.ChatId))
        { replies[restored.ChatId] = (restored.ReplyToId, "Reply to an earlier message"); NotifyReply(); }
        RestoreSubmittedText(restored.ChatId, restored.Text);
        if (restored.ChatId == chatId) ScheduleDraftSave();
        return restored.ChatId == chatId;
    }
    public void Clear()
    {
        draftSelection++; book.CancelPendingOf(this); queuedSend = null; editing = null;
        // Drafts of this composer's alone go with it; shared ones are every window's, and stay.
        if (alone) book.Reset();
        generation++; permissionRequest?.Cancel(); chatId = null; Text = ""; allowed = false; HasConversation = false; IsReady = false;
        NotifyEditing();
        IsChecking = false; Advisory("Select a conversation to write a message."); SendCommand.NotifyCanExecuteChanged();
        book.Holding(this, null); book.Holding(opening, null);
    }
    public void Dispose()
    {
        holdsEnded = true; book.Holding(this, null); book.Holding(opening, null);
        // A restore still reading for this composer opens nothing now: it would write into the drafts every window shares.
        draftSelection++;
        // What the box holds stays the conversation's draft, for whichever composer opens it next.
        if (chatId is { } open) { var words = CurrentDraft; if (words.Length == 0) drafts.Remove(open); else drafts[open] = words; }
        // Cleared, not just disposed: a switch arriving during session end cancelled the disposed source and threw before
        // it had written the draft being left (2026-09-27, which also kept Setup's Restart Manager from closing the app).
        book.CancelPendingOf(this); permissionRequest?.Cancel(); permissionRequest?.Dispose(); permissionRequest = null;
        outbox.Changed -= OutboxChanged;
        // A send of this composer's still in Undo send's wait can still be undone: its words come back after the window has gone (OutboxUndone).
        if (ownWaits.Count == 0) outbox.Undone -= OutboxUndone;
        book.Detach(this, last: alone);
        if (interruptedSends is not null) interruptedSends.Changed -= OutboxChanged;
    }
}
