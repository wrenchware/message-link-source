using MessageLink.Presentation.Services;
namespace MessageLink.Presentation.ViewModels;

// The composer's side of drafts. What is held for every conversation, who owns it, when it is saved and what another
// device's draft does to it are the drafts' (ComposerDrafts, one for the process); the composer holds the words in its
// box and says when they change, and the drafts read them from it (IDraftBox).
public sealed partial class ComposerViewModel : IDraftBox
{
    private readonly ComposerDrafts book;
    // Drafts made for this composer alone (none were given): a conversation it lets go of is forgotten there, since no
    // other composer would open it from them.
    private readonly bool alone;
    /// <summary>The signed-in Messenger account changed, so anything shown for a Messenger conversation may be another account's.</summary>
    public event EventHandler? AccountChanged;
    // The conversation being switched to holds its row from the click on, before this composer holds it (SelectAsync);
    // a disposed composer takes no hold again, even from a switch still finishing.
    private readonly object opening = new();
    private bool holdsEnded;
    private long draftSelection;
    private bool restoringDraft;
    /// <summary>Whether every draft is stored: nothing typed since its last save, and no save that failed.</summary>
    public bool DraftsSaved => book.AllSaved;
    public string DraftStorageStatus { get; private set; } = "";

    // The words held for conversations not in the box, and the reply chosen in each (the drafts' own, read in place).
    private Dictionary<string, string> drafts => book.Texts;
    private Dictionary<string, (string Id, string Preview)> replies => book.Replies;

    // Whether what is open was written as the account signed in now. Not for a conversation whose id names its account.
    private bool WrittenAsSignedInAccount() => chatId is not { } id || book.HeldAsSignedInAccount(id);

    /// <summary>The longest a chat's draft is kept from the server while it is being typed: at most one put of it in this time. Switching conversation and quitting send at once.</summary>
    public TimeSpan SharedPutInterval { get => book.SharedPutInterval; set => book.SharedPutInterval = value; }

    private bool inputFocused;

    /// <summary>
    /// The caret is in this composer's box (its view says so): another device's draft never replaces what is in it. One that came meanwhile is taken once the
    /// caret is gone, unless words were changed here by then.
    /// </summary>
    public bool InputFocused
    {
        get => inputFocused;
        set
        {
            if (inputFocused == value) return;
            inputFocused = value;
            if (!value) book.Blurred(this);
        }
    }

    // ---- What the drafts ask of the box ----

    string? IDraftBox.Open => chatId;
    string IDraftBox.Words => CurrentDraft;
    string IDraftBox.Box => Text;
    bool IDraftBox.Guarded => editing is null && InputFocused;
    void IDraftBox.Show(string text) => Text = text;
    void IDraftBox.TakeRemote(string text)
    {
        if (editing is { } session) session.SavedDraft = text;
        else SetBoxQuietly(text);
    }
    void IDraftBox.RepliesChanged() => NotifyReply();
    void IDraftBox.ReportStorage(string? status)
    {
        if (status is not null) DraftStorageStatus = status;
        OnPropertyChanged(nameof(DraftStorageStatus));
    }
    void IDraftBox.RestoreSubmitted(string chatId, string submitted) => RestoreSubmittedText(chatId, submitted);
    Task<bool> IDraftBox.ReturnHeldAsync(OutgoingMessage message) => ReturnHeldAsync(message);
    async Task IDraftBox.AccountChangedAsync(bool reopen)
    {
        if (holdsEnded) return;
        SendCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanSendOrQueue));
        AccountChanged?.Invoke(this, EventArgs.Empty);
        // The open conversation again, as the account signed in now: what was written as the other account is saved under
        // it and let go, and this account's own draft is read.
        if (reopen && chatId is { } id) await SelectAsync(id);
    }

    /// <summary>Whether this composer and that one keep their drafts in the same place, so words one holds the other holds too.</summary>
    public bool SharesDraftsWith(ComposerViewModel other) => ReferenceEquals(book, other.book);

    /// <summary>In the wait, and written as another account than the one this conversation is held for now.</summary>
    public bool WaitingAsOtherAccount(OutgoingMessage message) => book.WaitingAsOtherAccount(message);

    /// <summary>A send of this composer's has come back from its wait: true when its words were waiting, and so are in the saved draft already.</summary>
    public bool ForgetWaiting(OutgoingMessage message) { Waited(message.LocalId); return book.ForgetWaiting(message); }

    // The sends of this composer's in Undo send's wait. A disposed composer keeps hearing of undone sends until its last one is over.
    private readonly HashSet<string> ownWaits = [];
    private void Waited(string localId)
    {
        if (ownWaits.Remove(localId) && holdsEnded && ownWaits.Count == 0) outbox.Undone -= OutboxUndone;
    }

    private void ScheduleDraftSave()
    {
        if (restoringDraft || editing is not null || chatId is null) return;
        book.Typed(this, chatId);
    }

    /// <summary>One conversation's draft, for sending: one encrypted write, not one for every conversation that has ever had a draft.</summary>
    public Task<bool> FlushDraftAsync(string id) => book.FlushAsync(id, this);

    /// <summary>Every draft held, and what every composer's box holds.</summary>
    public Task<bool> FlushDraftsAsync() => book.FlushAllAsync(this);

    /// <summary>How many conversations have text saved on this PC, for Settings; rows of earlier versions that hold nothing are removed as they are counted.</summary>
    public Task<int> TidySavedDraftsAsync() => book.TidySavedAsync();

    /// <summary>
    /// The drafts of the conversations the test matches, saved and held, except the ones open in a composer, whose text
    /// belongs to whoever is writing it. Saved rows are found by digest among ids; returns how many rows went.
    /// </summary>
    public Task<int> ForgetDraftsAsync(string profile, IEnumerable<string> ids, Func<string, bool> matches) => book.ForgetAsync(profile, ids, matches);

    /// <summary>Everything saved on this PC, for every conversation and every profile stored here. Text being written now stays.</summary>
    public async Task<bool> ClearSavedDraftsAsync()
    {
        try
        {
            await book.ClearSavedAsync();
            if (book.Stores) DraftStorageStatus = "Saved drafts cleared.";
            return true;
        }
        catch (Exception) { DraftStorageStatus = "Could not clear the saved drafts."; return false; }
        finally { OnPropertyChanged(nameof(DraftStorageStatus)); }
    }
}
