using MessageLink.Core.Messaging;
using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

// Editing a sent message in the composer, as Messenger, Slack and Teams do: Up in an empty composer or Edit on a message
// puts that message's text in the box, and Enter saves it through the message actions' one edit call. Whatever the box
// held before (text and a chosen reply) is set aside and comes back when the edit ends, however it ends: saved,
// cancelled, or the composer switching to another conversation. While editing, the box's words are the edit, never the
// draft: the saved draft on disk stays what it was, and nothing in the box is sent, scheduled or used as a caption.
//
// The edit goes to the conversation that owns the message (its own ChatId), which in a combined conversation need not be
// the composer's sending route. Only one edit call is ever made per Enter; a rejected one keeps the editing state so the
// words can be fixed, and one whose outcome is unknown ends it (the message actions refuse another change to that message
// and keep the words for this session, so nothing is resent).
public sealed partial class ComposerViewModel
{
    private sealed class EditSession(ChatMessage message, string? profile, int maxLength, string original, string savedDraft)
    {
        public ChatMessage Message { get; } = message;
        public string? Profile { get; } = profile;
        public int MaxLength { get; } = maxLength;
        public string Original { get; } = original;
        // What the box held when editing began; it goes back when editing ends. Kept current when a send's words come
        // back to this conversation meanwhile (RestoreSubmittedText).
        public string SavedDraft { get; set; } = savedDraft;
    }
    private EditSession? editing;
    public const string EditEmpty = "An edit can't leave the message empty. Press Esc to keep the message as it is.";
    public bool IsEditing => editing is not null;
    public ChatMessage? EditingMessage => editing?.Message;
    // The edit whose call is running. Only that edit, while it is still the open one, makes the box read-only: a switch
    // during the call ends it, and the next conversation's composer must not wait out a call made for another (up to the
    // helper's budget). A new edit still waits for it, so one message never has two calls running.
    private EditSession? savingEdit;
    public bool IsSavingEdit => savingEdit is not null && ReferenceEquals(savingEdit, editing);
    public bool CanSaveEdit => editing is { } session && !IsSavingEdit && IsReady && !string.IsNullOrWhiteSpace(Text) && Text.Length <= session.MaxLength;

    /// <summary>
    /// Puts <paramref name="message"/>'s text in the box for editing, setting aside the box's draft. False when the composer
    /// has no conversation ready or is saving an edit already. <paramref name="savedEdit"/> is words an earlier attempt at
    /// this edit left with the message actions, which come back in place of the message's own.
    /// </summary>
    public bool BeginEdit(ChatMessage message, string? profile, int maxLength, string? savedEdit = null)
    {
        if (!IsReady || chatId is null || savingEdit is not null) return false;
        if (editing is { } current)
        {
            if (current.Message.ChatId == message.ChatId && current.Message.Id == message.Id) return true;
            EndEdit();
        }
        queuedSend = null;
        var original = MessageText.PlainText(message.Text ?? "");
        editing = new(message, profile, Math.Min(maxLength, 65536), original, Text);
        SetBoxQuietly(savedEdit ?? original);
        NotifyEditing();
        return true;
    }

    /// <summary>Leaves editing without saving; the draft set aside comes back. Nothing is sent.</summary>
    public void CancelEdit() { if (editing is not null && !IsSavingEdit) EndEdit(); }

    /// <summary>
    /// Enter while editing. Null when nothing was asked of the service: empty or too long (the state stays and Status
    /// says why), or unchanged (editing ends, as the dialog's Save did with unchanged text).
    /// </summary>
    public async Task<MessageMutationOutcome?> SaveEditAsync(MessageActionsViewModel actions)
    {
        if (editing is not { } session || savingEdit is not null || !IsReady) return null;
        var text = Text;
        if (string.IsNullOrWhiteSpace(text)) { Status = EditEmpty; return null; }
        if (text.Length > session.MaxLength) { Status = $"An edit can be at most {session.MaxLength:N0} characters here."; return null; }
        if (text == session.Original) { EndEdit(); return null; }
        savingEdit = session; NotifyEditing();
        MessageMutationOutcome outcome;
        try { outcome = await actions.EditAsync(session.Message, text, session.Profile); }
        finally { savingEdit = null; NotifyEditing(); }
        // A switch while the call ran has already put the other conversation's draft in the box and ended this edit.
        if (!ReferenceEquals(editing, session)) return outcome;
        // Accepted: the bubble changing is the report. Unknown: the actions say so, refuse a second try and keep the words.
        // Rejected: the actions say why, and the words stay here to be fixed or cancelled.
        if (outcome != MessageMutationOutcome.Rejected) EndEdit();
        return outcome;
    }

    private void EndEdit()
    {
        if (editing is not { } session) return;
        editing = null;
        SetBoxQuietly(session.SavedDraft);
        NotifyEditing();
    }

    // Text that is not typing: no draft save is scheduled for it, since the draft on disk is already what it should be.
    private void SetBoxQuietly(string value)
    {
        var wasRestoring = restoringDraft;
        restoringDraft = true;
        try { Text = value; } finally { restoringDraft = wasRestoring; }
    }

    // The box's draft as storage and the conversation list should see it.
    private string CurrentDraft => editing?.SavedDraft ?? Text;

    private void NotifyEditing()
    {
        OnPropertyChanged(nameof(IsEditing)); OnPropertyChanged(nameof(EditingMessage)); OnPropertyChanged(nameof(IsSavingEdit));
        OnPropertyChanged(nameof(CanSaveEdit)); OnPropertyChanged(nameof(IsReadOnly)); OnPropertyChanged(nameof(CanSchedule));
        NotifyReply();
        SendCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanSendOrQueue));
        NotifyRecoveryState();
    }
}
