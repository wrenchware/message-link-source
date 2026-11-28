using System.Diagnostics;

namespace MessageLink.Presentation.Services;

/// <summary>
/// A composer as the drafts see it: the conversation its box holds, the words in it, and where to say how saving went. The
/// box's words belong to whoever is writing them; everything else about a conversation's draft is the drafts' own.
/// </summary>
internal interface IDraftBox
{
    /// <summary>The conversation whose words are in this box now, or null (none, or one still being opened).</summary>
    string? Open { get; }
    /// <summary>The box's draft as storage sees it: the words set aside while a sent message is edited, else the box.</summary>
    string Words { get; }
    /// <summary>What the box shows this moment, typed or not; while a conversation opens, what was typed meanwhile is told from it.</summary>
    string Box { get; }
    /// <summary>The caret is in the box with no edit there: another device's draft never replaces these words.</summary>
    bool Guarded { get; }
    /// <summary>Puts words in the box while a conversation opens (not typing: no save follows).</summary>
    void Show(string text);
    /// <summary>Another device's draft for the open conversation: in the box, or in place of the words an edit set aside.</summary>
    void TakeRemote(string text);
    /// <summary>The open conversation's reply may have changed.</summary>
    void RepliesChanged();
    /// <summary>How saving went, for this box's notice; null leaves the text as it is.</summary>
    void ReportStorage(string? status);
    /// <summary>The words of a conversation this box holds came back from a send that did not go (Undo, a refusal).</summary>
    void RestoreSubmitted(string chatId, string submitted);
    /// <summary>A message held in Undo send's wait at quit goes back to the draft of a conversation this box holds.</summary>
    Task<bool> ReturnHeldAsync(OutgoingMessage message);
    /// <summary>The signed-in Messenger account changed; <paramref name="reopen"/> when what the box shows must be opened again as the new one.</summary>
    Task AccountChangedAsync(bool reopen);
}

// Every conversation's unsent words, for every composer of the process at once: what is held in memory for the
// conversations visited, which account owns each (DraftOwners), words kept while that account is not known, words in
// Undo send's wait, the pause before a save and the saves themselves, and another device's drafts arriving. A
// conversation's draft is held once here whichever window writes it; a composer holds only the box's words.
public sealed partial class ComposerDrafts
{
    private const string SavedStatus = "Text drafts saved.", FailedStatus = "Could not save text drafts. Keep the app open to preserve them.";
    private PrivateState? privateState;
    private ISharedRows? hostRows;
    private SynchronizationContext? home;
    private readonly List<IDraftBox> boxes = [];
    private bool ownerHooked;

    /// <summary>
    /// The words held for conversations not open in a box, and the reply chosen in every one (the open ones too). A
    /// composer reads and writes these in place.
    /// </summary>
    internal readonly Dictionary<string, string> Texts = [];
    internal readonly Dictionary<string, (string Id, string Preview)> Replies = [];

    // A Messenger conversation id names no account, so the text and reply held here for one are tied to the account they
    // were written as (DraftOwners). Absent for any other service, and for a Messenger conversation whose account was not
    // known: that is held in memory only, saved nowhere and offered to no account.
    private readonly Dictionary<string, string> draftScopes = [];
    // Words held for a Messenger conversation while its account is not known are kept in a protected row of their own
    // (DraftOwners.KeepUnfiledAsync), named by this process's drafts, so that quitting does not lose them; they are filed to
    // the account once it is known. Rows kept here; and conversations whose saved draft could not be read to join, which
    // stay unfiled however the account reads (filing them would replace what is saved).
    private readonly string writer = Guid.NewGuid().ToString("N");
    private readonly HashSet<string> unfiledRows = [], unfiledOnly = [];
    // Words that were typed while the account was not known, with the account known now but its saved draft for the
    // conversation not read yet: still unfiled (kept in the unfiled row, never saved under the account), though they are
    // the signed-in account's to see and send. Account by conversation; the account is told if it changes before the read.
    private readonly Dictionary<string, string> awaitingMerge = [];
    // Words written as one account whose save failed when the conversation was opened as another (or none): their owner is
    // the account they were written as, so they are held here, out of sight of the account now shown, until they are saved
    // (tried again at every save and at quit) or their account opens the conversation again and they come back from here.
    private readonly Dictionary<(string Id, string Scope), ParkedDraft> parked = [];
    private sealed record ParkedDraft(SavedDraft Draft, bool Loaded);
    private readonly SemaphoreSlim draftGate = new(1);
    private readonly HashSet<string> loadedDrafts = [];
    // Conversations whose saved draft could not be read when opened. Their empty box is what failed to load, not a draft
    // the user cleared, so it never removes the row; text written there replaces it as usual.
    private readonly HashSet<string> unreadableDrafts = [];
    // Conversations whose words are not known to be stored: typed since the last save, or whose save failed.
    private readonly HashSet<string> unsaved = [];
    private string? draftProfile;
    // The pause before each conversation's typed words are saved, and the box that typed them (told how it went).
    private readonly Dictionary<string, (CancellationTokenSource Delay, IDraftBox By)> pending = [];
    private bool ended;

    /// <summary>Whether a composer over this store keeps its drafts here: the store these drafts use, or any while they use none yet.</summary>
    internal bool Accepts(PrivateState? given) => (privateState ?? state) is not { } mine || ReferenceEquals(mine, given);

    /// <summary>
    /// A composer of this process. The store and the account owners it was given are the drafts' when they have none yet;
    /// one process has one store, so a second composer's are the same ones.
    /// </summary>
    internal void Attach(IDraftBox box, PrivateState? state, DraftOwners? draftOwners)
    {
        home ??= SynchronizationContext.Current;
        if (privateState is null && state is not null)
        {
            privateState = state;
            // A client of a server only (state.db shares nothing): another device's drafts reach the conversations held here.
            hostRows = state.Shared;
            if (hostRows is not null) hostRows.HostChanged += OnBookHostChanged;
        }
        if (owners is null && draftOwners is not null) owners = draftOwners;
        if (owners is not null && !ownerHooked) { ownerHooked = true; owners.Changed += OwnerChanged; owners.Attach(writer); }
        boxes.Add(box);
        ended = false;
    }

    /// <summary>
    /// The composer is gone. What its box held stays here, as the conversation's draft. Drafts that belonged to that one
    /// composer alone stop following the store and the account, and the words they kept unfiled are filed at the next look.
    /// </summary>
    internal void Detach(IDraftBox box, bool last)
    {
        boxes.Remove(box);
        if (!last) return;
        ended = true;
        CancelAllPending();
        if (hostRows is not null) hostRows.HostChanged -= OnBookHostChanged;
        if (owners is not null && ownerHooked) { ownerHooked = false; owners.Changed -= OwnerChanged; owners.Detach(writer); }
    }

    /// <summary>The box words of this conversation are in, or null when none holds it.</summary>
    internal IDraftBox? BoxOf(string id)
    {
        foreach (var box in boxes) if (box.Open == id) return box;
        return null;
    }
    private bool Boxed(string id) => BoxOf(id) is not null;

    /// <summary>Whether a save is wanted or failed: words typed and not yet stored, or words of another account not stored.</summary>
    internal bool AllSaved => unsaved.Count == 0 && parked.Count == 0;
    internal bool HasParked => parked.Count > 0;
    internal bool Parks(string id) => parked.Keys.Any(key => key.Id == id);
    internal bool Stores => privateState is not null;
    internal bool Filing => draftProfile is not null;

    // The profile a conversation's draft is filed under. A Messenger conversation's also names the account it was written as,
    // so the same thread under another account is another row; with that account not known there is nowhere to file it.
    private string? FiledUnder(string id) =>
        draftProfile is null ? null
        : owners is null || !DraftOwners.Partitioned(id) ? draftProfile
        : draftScopes.TryGetValue(id, out var scope) ? DraftOwners.FileUnder(draftProfile, id, scope) : null;

    /// <summary>The account a conversation's words were written as, for one whose id names none; null otherwise or not known.</summary>
    internal string? ScopeOf(string id) => draftScopes.GetValueOrDefault(id);

    // Words that came back to a Messenger conversation never opened here (an undone send) are the signed-in account's,
    // which is looked up now. One that was opened without its account known stays unfiled.
    private async Task EnsureOwnersAsync(IEnumerable<string> ids)
    {
        if (owners is null) return;
        string[] unfiled = [.. ids.Where(id => DraftOwners.Partitioned(id) && !draftScopes.ContainsKey(id) && !loadedDrafts.Contains(id) && !unfiledOnly.Contains(id) && !awaitingMerge.ContainsKey(id))];
        if (unfiled.Length == 0) return;
        string? scope;
        try { scope = await owners.RefreshAsync(); } catch (Exception) { return; }
        if (scope is null) return;
        foreach (var id in unfiled) draftScopes.TryAdd(id, scope);
    }

    /// <summary>Whether what is held for this conversation was written as the account signed in now. Always for one whose id names its account.</summary>
    internal bool HeldAsSignedInAccount(string id) =>
        owners is null || !DraftOwners.Partitioned(id) || (draftScopes.GetValueOrDefault(id) ?? awaitingMerge.GetValueOrDefault(id)) == owners.Messenger;

    // Writes (or lets go of) the row for words that have no account to be filed under. Under the draft gate. False only when
    // they could not be written: the words are still held here, and that is the one thing the user is told.
    private async Task<bool> KeepUnfiledAsync(string id, SavedDraft draft)
    {
        var empty = IsEmpty(draft);
        if (empty && !unfiledRows.Contains(id)) return true;
        if (owners is null || draftProfile is null) return empty;
        var kept = await owners.KeepUnfiledAsync(draftProfile, writer, id, draft.Text, draft.ReplyId, draft.ReplyPreview);
        if (kept) { if (empty) unfiledRows.Remove(id); else unfiledRows.Add(id); }
        return kept;
    }

    // The account's row now holds these words: the unfiled row for them is let go.
    private async Task SettleUnfiledAsync(string id)
    {
        if (!unfiledRows.Contains(id) || owners is null || draftProfile is null) return;
        // Still tracked when it could not be removed, so the next save of this conversation tries again.
        if (await owners.ForgetUnfiledAsync(draftProfile, writer, id)) unfiledRows.Remove(id);
    }

    // A conversation's draft saved under its account's row, and the unfiled row for the same words let go once it is. When
    // nothing is left to keep (sent, or cleared) the unfiled row goes even if that save fails: it would otherwise bring the
    // words back at the next run.
    private async Task SaveAndSettleAsync(string profile, string id, SavedDraft draft)
    {
        try { await SaveDraftAsync(profile, id, draft); }
        catch (Exception) when (IsEmpty(draft)) { await SettleUnfiledAsync(id); throw; }
        await SettleUnfiledAsync(id);
    }

    // ---- The account changed ----

    private void OwnerChanged(object? sender, EventArgs e)
    {
        if (home is not null && SynchronizationContext.Current != home) { home.Post(_ => OwnerChanged(sender, e), null); return; }
        if (ended) return;
        _ = ReselectAndFileAsync();
    }

    private async Task ReselectAndFileAsync()
    {
        // Each box first: words waiting to be joined to the account's saved draft try again, as does anything shown as
        // another account; then the words held for conversations no box shows, and anything that could not be saved.
        foreach (var box in boxes.ToArray())
        {
            var id = box.Open;
            try { await box.AccountChangedAsync(id is not null && (!HeldAsSignedInAccount(id) || awaitingMerge.ContainsKey(id))); }
            catch (Exception) { /* Choosing a conversation again starts over. */ }
        }
        await FileUnfiledAsync();
        await SaveParkedAsync();
    }

    // Words held for a Messenger conversation while the account was not known: not any account's yet.
    private bool HoldsUnfiled(string id) => (Texts.ContainsKey(id) || Replies.ContainsKey(id)) && !draftScopes.ContainsKey(id);

    private static string JoinWords(string saved, string typed) => DraftOwners.JoinWords(saved, typed);

    // The account is known now: what was typed in the conversations not open meanwhile is its own words, so it is joined
    // to what the account has saved for each and saved as its. Typed words are never dropped; one that cannot be read or
    // written is kept in memory and is carried when the conversation is opened.
    private async Task FileUnfiledAsync()
    {
        if (owners?.Messenger is not { } scope || draftProfile is null || privateState is null) return;
        foreach (var id in Texts.Keys.Concat(Replies.Keys).Distinct().Where(id => !Boxed(id) && DraftOwners.Partitioned(id) && HoldsUnfiled(id)).ToArray())
        {
            try
            {
                var saved = await ReadSavedAsync(draftProfile, id, scope);
                if (!HoldsUnfiled(id) || Boxed(id)) continue;
                if (saved is not null)
                {
                    Texts[id] = JoinWords(saved.Text, Texts.GetValueOrDefault(id, ""));
                    if (saved.ReplyId is not null) Replies.TryAdd(id, (saved.ReplyId, saved.ReplyPreview ?? "Reply to an earlier message"));
                }
                draftScopes[id] = scope; loadedDrafts.Add(id); unfiledOnly.Remove(id); awaitingMerge.Remove(id);
                await FlushAsync(id, null);
            }
            catch (Exception) { /* Still in memory, unfiled; opening the conversation carries it then. */ }
        }
    }

    // What is held here for a Messenger conversation was written as another account than the one it is opened as now: it is
    // saved under the account it belongs to and let go, so none of it reaches this one. Not let go from memory while that
    // save has failed: it moves to where only its own account finds it (parked) and is saved when storage allows.
    private async Task ForgetOtherAccountAsync(string id, string? scope, IDraftBox box)
    {
        if (!loadedDrafts.Contains(id) && !Texts.ContainsKey(id) && !Replies.ContainsKey(id)) return;
        var owner = draftScopes.GetValueOrDefault(id);
        if (owner == scope) return;
        if (!await FlushAsync(id, box))
        {
            // Words held for no account are not another account's, and are kept where they are.
            if (owner is null) return;
            parked[(id, owner)] = new(CaptureDraft(id), loadedDrafts.Contains(id));
        }
        Texts.Remove(id); Replies.Remove(id); loadedDrafts.Remove(id); unreadableDrafts.Remove(id); draftScopes.Remove(id);
    }

    // The conversation is opened as the account that wrote words which could not be saved when it was left (ParkedDraft): they
    // come back into memory as that account's, as current as when they were left if the draft had been read, otherwise
    // joined to the saved draft as words typed with no account are. True when they are carried to be joined.
    private bool RestoreParked(string id, string scope, bool carried, out bool supersedesSaved)
    {
        supersedesSaved = false;
        if (!parked.Remove((id, scope), out var back)) return carried;
        var held = Texts.ContainsKey(id) || Replies.ContainsKey(id);
        if (back.Loaded && !carried && !held)
        {
            if (back.Draft.Text.Length > 0) Texts[id] = back.Draft.Text;
            if (back.Draft.ReplyId is not null) Replies[id] = (back.Draft.ReplyId, back.Draft.ReplyPreview ?? "Reply to an earlier message");
            loadedDrafts.Add(id);
            return false;
        }
        // Something is held for it already (words typed with no account, or an undone send): the two are joined, and the
        // saved draft is joined to both. Words that had been read from it already include it.
        supersedesSaved = back.Loaded;
        Texts[id] = JoinWords(back.Draft.Text, Texts.GetValueOrDefault(id, ""));
        if (back.Draft.ReplyId is not null) Replies.TryAdd(id, (back.Draft.ReplyId, back.Draft.ReplyPreview ?? "Reply to an earlier message"));
        draftScopes.Remove(id); loadedDrafts.Remove(id);
        return true;
    }

    // Saves what is parked, to the row of the account that wrote it. Under the draft gate. True when nothing is left parked.
    private async Task<bool> SaveParkedLockedAsync()
    {
        if (parked.Count == 0) return true;
        if (draftProfile is null || privateState is null) return false;
        foreach (var (key, back) in parked.ToArray())
        {
            // The conversation is held for that account again here (a send undone, say): they are joined to it.
            if (Texts.ContainsKey(key.Id) && draftScopes.GetValueOrDefault(key.Id) == key.Scope)
            {
                if (Boxed(key.Id)) continue;
                Texts[key.Id] = JoinWords(back.Draft.Text, Texts[key.Id]);
                if (back.Draft.ReplyId is not null) Replies.TryAdd(key.Id, (back.Draft.ReplyId, back.Draft.ReplyPreview ?? "Reply to an earlier message"));
                parked.Remove(key);
                continue;
            }
            try
            {
                var filed = DraftOwners.FileUnder(draftProfile, key.Id, key.Scope);
                if (IsEmpty(back.Draft)) await privateState.RemoveAsync(filed, "draft", key.Id);
                else await privateState.WriteAsync(filed, "draft", key.Id, back.Draft);
                if (parked.TryGetValue(key, out var now) && ReferenceEquals(now, back)) parked.Remove(key);
                Saved(key.Id, back.Draft.Text, key.Scope);
            }
            catch (Exception) { /* Still parked; the next save, account change or quit tries again. */ }
        }
        return parked.Count == 0;
    }

    private async Task SaveParkedAsync()
    {
        if (parked.Count == 0) return;
        await draftGate.WaitAsync();
        bool saved;
        try { saved = await SaveParkedLockedAsync(); }
        finally { draftGate.Release(); }
        foreach (var box in boxes.ToArray()) box.ReportStorage(saved ? null : FailedStatus);
    }

    // The saved draft of a conversation as the account it is opened as. Drafts saved before they were filed by account are
    // handed to the account that had them (DraftOwners.AdoptAsync) the first time that account opens the conversation.
    private async Task<SavedDraft?> ReadSavedAsync(string profile, string id, string? scope)
    {
        if (owners is null || !DraftOwners.Partitioned(id)) return await privateState!.ReadAsync<SavedDraft>(profile, "draft", id);
        if (scope is null) return null;
        var filed = DraftOwners.FileUnder(profile, id, scope);
        var saved = await privateState!.ReadAsync<SavedDraft>(filed, "draft", id);
        if (saved is null && await owners.AdoptAsync(profile, id, scope)) saved = await privateState.ReadAsync<SavedDraft>(filed, "draft", id);
        return saved;
    }

    // Nothing written and nothing replied to: there is no draft, so nothing should be kept for this conversation.
    private static bool IsEmpty(SavedDraft? draft) => draft is null || draft.Text.Length == 0 && draft.ReplyId is null;
    private SavedDraft CaptureDraft(string id)
    {
        var body = DraftShown(id);
        var reply = Replies.TryGetValue(id, out var chosen) ? chosen : default;
        // Another account's words stay out of this account's row, in the same thread under the same id, and out of the row kept for no
        // account, which the next account known would take. They are in the row they were saved to.
        var owner = draftScopes.GetValueOrDefault(id);
        var words = waiting.GetValueOrDefault(id)?.Where(word => word.Scope is null || word.Scope == owner).ToArray() ?? [];
        if (words.Length > 0)
        {
            // Each is a message of its own, so words typed the same as one that waits are still kept.
            var waited = string.Join(Environment.NewLine, words.Select(word => word.Text));
            var sole = words.Length == 1 && body.Length == 0 && reply.Id is null ? words[0] : null;
            body = body.Length == 0 ? waited : waited + Environment.NewLine + body;
            // A draft has one reply. Where the words have several, or some have none, a crash leaves them as plain words rather than a reply to something they don't answer.
            reply = sole?.ReplyId is { } target ? (target, sole.ReplyPreview!) : default;
        }
        return reply.Id is not null ? new(body, reply.Id, reply.Preview) : new(body, null, null);
    }
    private string DraftShown(string id) => BoxOf(id) is { } box ? box.Words : Texts.GetValueOrDefault(id, "");

    /// <summary>Whether anything is held that is a draft: words, a reply, or another account's words not saved yet.</summary>
    internal bool HoldsWords => Replies.Count > 0 || Texts.Values.Any(v => !string.IsNullOrEmpty(v)) || parked.Count > 0;

    // ---- Opening a conversation in a box ----

    /// <summary>
    /// What opening a conversation found: whether the box still opens it (no later choice overtook it), and whether its
    /// words are to be saved at the next pause (carried from no account, or typed while it opened).
    /// </summary>
    internal readonly record struct Opened(bool Current, bool Save);

    /// <summary>
    /// Opens a conversation in a box: as the Messenger account signed in now, with words written as another saved under that
    /// one and let go, words typed while no account was known joined to the account's saved draft, and the saved draft
    /// read the first time. The box shows each step; <paramref name="current"/> says whether it is still the box's choice.
    /// </summary>
    internal async Task<Opened> OpenAsync(IDraftBox box, string? id, string shown, Func<bool> current)
    {
        // <paramref name="shown"/> is what the box was set to show as the choice was made; anything else in it when it is set
        // again was typed (or pasted) meanwhile, and is kept: joined to the draft, as words typed while the account was unknown are.
        var extra = "";
        string Typed() => box.Box == shown ? "" : shown.Length > 0 && box.Box.StartsWith(shown, StringComparison.Ordinal) ? box.Box[shown.Length..] : box.Box;
        // A Messenger conversation is opened as the account signed in now. What is held for it from another account is
        // saved under that one first, and let go.
        string? scope = null;
        var partitioned = owners is not null && id is not null && DraftOwners.Partitioned(id);
        // Words typed while the account was not known (the window reopens a conversation before Messenger's chats load) are
        // the user's own, not another account's: they are kept, joined to the draft the account that is known now has saved.
        var carried = false; var supersedesSaved = false;
        if (partitioned)
        {
            try { scope = await owners!.RefreshAsync(); } catch (Exception) { scope = null; }
            if (!current()) return default;
            if (scope is not null && (HoldsUnfiled(id!) || awaitingMerge.ContainsKey(id!))) { carried = true; loadedDrafts.Remove(id!); }
            else await ForgetOtherAccountAsync(id!, scope, box);
            if (!current()) return default;
            if (scope is null) awaitingMerge.Remove(id!);
            else
            {
                carried = RestoreParked(id!, scope, carried, out supersedesSaved);
                // Carried words are filed to the account only once its saved draft has been read and joined to them (below):
                // until then a save of them would replace a draft that could not be read with only these words.
                if (carried) awaitingMerge[id!] = scope;
                else { draftScopes[id!] = scope; unfiledOnly.Remove(id!); awaitingMerge.Remove(id!); }
            }
        }
        extra = Typed();
        box.Show(shown = DraftOwners.JoinWords(id is not null && Texts.TryGetValue(id, out var draft) ? draft : "", extra));
        // Nothing is stored, so there is no draft to join them to and nothing for a save to replace.
        if (carried && privateState is null) { draftScopes[id!] = scope!; awaitingMerge.Remove(id!); unfiledOnly.Remove(id!); }
        try
        {
            if (privateState is not null && id is not null && !loadedDrafts.Contains(id))
            {
                var profile = await privateState.ProfileAsync();
                if (!current()) return default;
                draftProfile = profile;
                var saved = profile is null ? null : await ReadSavedAsync(profile, id, scope);
                if (!current()) return default;
                if (profile is not null) NoteRead(profile, id, scope, saved);
                loadedDrafts.Add(id);
                unreadableDrafts.Remove(id);
                // The saved draft was read (a conversation with none reads as none): the carried words are the account's now.
                if (carried && profile is not null) { draftScopes[id] = scope!; awaitingMerge.Remove(id); unfiledOnly.Remove(id); }
                if (saved is not null)
                {
                    var joined = carried && !supersedesSaved ? JoinWords(saved.Text, Texts.GetValueOrDefault(id, "")) : carried ? Texts.GetValueOrDefault(id, "") : saved.Text;
                    var later = Typed();
                    if (later.Length > 0) extra = DraftOwners.JoinWords(extra, later);
                    joined = DraftOwners.JoinWords(joined, extra);
                    box.Show(shown = joined); Texts[id] = joined;
                    if (saved.ReplyId is not null && !(carried && Replies.ContainsKey(id))) Replies[id] = (saved.ReplyId, saved.ReplyPreview ?? "Reply to an earlier message");
                    box.RepliesChanged();
                }
            }
        }
        catch
        {
            if (id is not null) unreadableDrafts.Add(id);
            box.ReportStorage("Couldn't bring back the saved draft: Message Link couldn't read it on this PC.");
        }
        return new(current(), carried || extra.Length > 0);
    }

    /// <summary>
    /// A box let go of a conversation that is now another box's to open (a window of its own): its words were saved. Drafts
    /// that are one composer's alone forget it, so a later save there can't overwrite what the other window writes; shared
    /// ones keep it, for the other box to open from.
    /// </summary>
    internal async Task ReleasedAsync(string id, bool alone)
    {
        // Saved as it was let go: nothing typed here since stands against another device's draft.
        edited.Remove(id);
        if (!alone) return;
        Texts.Remove(id); Replies.Remove(id); loadedDrafts.Remove(id); draftScopes.Remove(id); unsaved.Remove(id);
        // Words kept for no account are no longer held here, so they are no longer these drafts' to file.
        if (unfiledRows.Remove(id) && owners is not null && draftProfile is not null) await owners.LetGoUnfiledAsync(draftProfile, writer, id);
        unfiledOnly.Remove(id); awaitingMerge.Remove(id);
    }

    /// <summary>Drafts made for one composer alone, which it cleared: nothing held is kept (what is stored stays).</summary>
    internal void Reset()
    {
        CancelAllPending();
        draftProfile = null; loadedDrafts.Clear(); draftScopes.Clear(); edited.Clear(); heldRows.Clear(); waiting.Clear(); Texts.Clear(); Replies.Clear(); unsaved.Clear();
    }

    /// <summary>Words that come back after a draft, as Restore puts them: on a line of their own after it.</summary>
    internal static string Follow(string draft, string words) => draft.Length == 0 ? words : draft + Environment.NewLine + words;

    /// <summary>
    /// Words a send took and then definitely did not send, for a conversation <paramref name="from"/>'s box doesn't hold:
    /// into the box that holds it (a window of its own), or after the draft held for it, which is saved at once.
    /// </summary>
    internal void Restore(IDraftBox from, string id, string submitted)
    {
        if (BoxOf(id) is { } other && !ReferenceEquals(other, from)) { other.RestoreSubmitted(id, submitted); other.RepliesChanged(); return; }
        Texts[id] = Follow(Texts.GetValueOrDefault(id, ""), submitted);
        _ = FlushAsync(id, from);
    }

    /// <summary>
    /// Undo send at quit (HeldSends): a held message's words back in its conversation's draft, which <paramref name="from"/>'s
    /// box doesn't hold. Into the box that does, or after the draft held for it (its saved one read first, so they join it),
    /// with the message they replied to; then that draft is written. False when it could not be read or written.
    /// </summary>
    internal async Task<bool> ReturnHeldAsync(IDraftBox from, OutgoingMessage message)
    {
        var id = message.ChatId;
        if (BoxOf(id) is { } other && !ReferenceEquals(other, from)) return await other.ReturnHeldAsync(message);
        if (!await LoadForReturnAsync(id)) return false;
        if (message.ReplyToId is not null) Replies.TryAdd(id, (message.ReplyToId, "Reply to an earlier message"));
        Texts[id] = Follow(Texts.GetValueOrDefault(id, ""), message.Text);
        return await FlushAsync(id, from);
    }

    /// <summary>
    /// Undo send at quit puts a held message's words back in a conversation's draft that no box holds. Never loaded here,
    /// its saved draft is read first, so the words join it rather than replace it. A Messenger conversation whose account is
    /// not known has no draft to join: the words are kept for no account. False when the saved draft could not be read.
    /// </summary>
    internal async Task<bool> LoadForReturnAsync(string id)
    {
        if (Boxed(id) || privateState is null || loadedDrafts.Contains(id) || Texts.ContainsKey(id)) return true;
        var partitioned = owners is not null && DraftOwners.Partitioned(id);
        // The message is out of the outbox already, so it is never dropped here: whatever cannot be joined to the saved
        // draft now is kept in a row of its own (DraftOwners.KeepUnfiledAsync) and joined when it can be.
        try
        {
            var profile = draftProfile ?? await privateState.ProfileAsync();
            if (profile is null) return true;
            draftProfile ??= profile;
            string? scope = partitioned ? await owners!.RefreshAsync() : null;
            // Nor may the draft be filed under an account that becomes known meanwhile without the saved one read first.
            if (scope is null && partitioned) { unfiledOnly.Add(id); return true; }
            var saved = await ReadSavedAsync(profile, id, scope);
            if (Boxed(id) || loadedDrafts.Contains(id) || Texts.ContainsKey(id)) return true; // Loaded meanwhile: memory is current.
            if (saved is not null && saved.Text.Length > 0) Texts[id] = saved.Text;
            if (saved?.ReplyId is { } savedReply) Replies.TryAdd(id, (savedReply, saved.ReplyPreview ?? "Reply to an earlier message"));
            loadedDrafts.Add(id);
            if (scope is not null) draftScopes[id] = scope;
            return true;
        }
        catch (Exception) { if (partitioned) { unfiledOnly.Add(id); return true; } return false; }
    }

    // ---- Undo send's wait ----
    //
    // Sends in Undo send's wait, by conversation, oldest first. Their words are not in the box, and they stay in the saved draft (CaptureDraft), so that a crash
    // or a power cut in the wait leaves an ordinary draft with nothing to recover, under whichever account and profile it was written. They leave it just before the send goes.
    private readonly Dictionary<string, List<WaitingWords>> waiting = [];
    /// <param name="LocalId">The send's id in the outbox: two sends can have the same words.</param>
    internal sealed record WaitingWords(string LocalId, string Text, string? ReplyId, string? ReplyPreview, string? Scope);

    /// <summary>Words of a send entering Undo send's wait, kept in the saved draft until it ends. Null when there is nowhere to keep them.</summary>
    internal WaitingWords? Wait(string id, string body, string? replyId, string? replyPreview)
    {
        if (privateState is null || draftProfile is null) return null;
        var words = new WaitingWords("local:" + Guid.NewGuid().ToString("N"), body, replyId, replyPreview, draftScopes.GetValueOrDefault(id));
        if (!waiting.TryGetValue(id, out var already)) waiting[id] = already = [];
        already.Add(words);
        return words;
    }

    /// <summary>The send did not get going: its words are not waiting any more (the caller puts them back).</summary>
    internal void Unwait(string id, WaitingWords words) => waiting.GetValueOrDefault(id)?.Remove(words);

    /// <summary>The wait is over: the saved draft goes back to what is held. True once that is written.</summary>
    internal async Task<bool> LetGoOfWaitingAsync(string id, WaitingWords words)
    {
        if (words.Scope is { } scope && draftScopes.GetValueOrDefault(id) != scope && draftProfile is { } profile && privateState is not null)
            return await LetGoOfOtherAccountAsync(profile, id, scope, words);
        if (waiting.TryGetValue(id, out var left) && left.Remove(words) && left.Count == 0) waiting.Remove(id);
        return await FlushAsync(id, null);
    }

    // The account that wrote them has been replaced in the wait. Their copy is in that account's row, which no save of this account's reaches, and left there it would come
    // back as a draft of a message that went. A row changed since (not holding the words as they were) is left as it is. False when it can't be written: the send doesn't go.
    private async Task<bool> LetGoOfOtherAccountAsync(string profile, string id, string scope, WaitingWords words)
    {
        var filed = DraftOwners.FileUnder(profile, id, scope);
        await draftGate.WaitAsync();
        try
        {
            SavedDraft? before = null, after = null;
            var wasParked = parked.TryGetValue((id, scope), out var back);
            if (wasParked) parked[(id, scope)] = back! with { Draft = WithoutWords(back.Draft, words) ?? back.Draft };
            else
            {
                before = await privateState!.ReadAsync<SavedDraft>(filed, "draft", id);
                after = before is null ? null : WithoutWords(before, words);
                if (after is not null) await WriteRowAsync(after);
            }
            // Undone while that was written: the words stay in the row after all.
            if (waiting.GetValueOrDefault(id)?.Contains(words) != true)
            {
                if (wasParked) parked[(id, scope)] = back!;
                else if (after is not null) await WriteRowAsync(before!);
                return true;
            }
            if (waiting[id].Remove(words) && waiting[id].Count == 0) waiting.Remove(id);
            return true;

            async Task WriteRowAsync(SavedDraft draft)
            {
                await SaveDraftAsync(filed, id, draft);
                NotePut(filed, id, draft, wrote: true);
                Saved(id, draft.Text, scope);
            }
        }
        catch (Exception)
        {
            if (waiting.TryGetValue(id, out var left) && left.Remove(words) && left.Count == 0) waiting.Remove(id);
            return false;
        }
        finally { draftGate.Release(); }
    }

    // The draft without these words, each message being a line of its own in it (CaptureDraft); null when it doesn't hold them so.
    private static SavedDraft? WithoutWords(SavedDraft draft, WaitingWords words)
    {
        var line = Environment.NewLine;
        string? rest = null;
        if (draft.Text == words.Text) rest = "";
        else if (draft.Text.StartsWith(words.Text + line, StringComparison.Ordinal)) rest = draft.Text[(words.Text.Length + line.Length)..];
        else if (draft.Text.IndexOf(line + words.Text + line, StringComparison.Ordinal) is >= 0 and var at) rest = draft.Text.Remove(at, line.Length + words.Text.Length);
        else if (draft.Text.EndsWith(line + words.Text, StringComparison.Ordinal)) rest = draft.Text[..^(line.Length + words.Text.Length)];
        if (rest is null) return null;
        return draft.ReplyId is not null && draft.ReplyId == words.ReplyId ? new(rest, null, null) : draft with { Text = rest };
    }

    /// <summary>In the wait, and written as another account than the one this conversation is held for now.</summary>
    internal bool WaitingAsOtherAccount(OutgoingMessage message) =>
        waiting.GetValueOrDefault(message.ChatId)?.FirstOrDefault(word => word.LocalId == message.LocalId)?.Scope is { } scope && scope != draftScopes.GetValueOrDefault(message.ChatId);

    /// <summary>A send has come back from its wait: true when its words were waiting here, and so are in the saved draft already.</summary>
    internal bool ForgetWaiting(OutgoingMessage message)
    {
        if (!waiting.TryGetValue(message.ChatId, out var left) || left.FirstOrDefault(word => word.LocalId == message.LocalId) is not { } words) return false;
        left.Remove(words);
        if (left.Count == 0) waiting.Remove(message.ChatId);
        return true;
    }
    internal bool Waits(string id) => waiting.ContainsKey(id);

    // Storing a draft that is empty leaves a row behind for every conversation ever opened; removing it keeps the saved
    // drafts to the conversations that actually have one.
    private Task SaveDraftAsync(string profile, string id, SavedDraft draft)
    {
        if (!IsEmpty(draft)) { unreadableDrafts.Remove(id); return privateState!.WriteAsync(profile, "draft", id, draft); }
        return unreadableDrafts.Contains(id) ? Task.CompletedTask : privateState!.RemoveAsync(profile, "draft", id);
    }

    // ---- Drafts shared with a server's other clients (SWITCHBOARD.md stage 6, slice 6) ----
    //
    // In a client of a Messages Server the protected state is the replica of the server's, and a chat's draft is one of its shared rows (last writer wins): what
    // is typed here reaches the other devices, and what they type reaches the conversations held here. The person typing now wins: an arriving draft never
    // replaces words in a box the caret is in, or that has been changed since it was loaded (the next save here simply overwrites the other device's); a
    // conversation not open here, or open and untouched, takes the newer draft. Staged attachments stay on the device (StagedByConversation).
    // Everything below is bookkeeping for that and does nothing in the in-process app and the loopback, whose state.db shares nothing (hostRows is null):
    // saves go through the same writes at the same moments there.
    // Conversations whose words were changed here since they were loaded or last saved (an open one stays until it is loaded again).
    private readonly HashSet<string> edited = [];
    // What each draft row held when it was last read or written here (null: none), by stored key; a save of what is held already is not sent again.
    private readonly Dictionary<string, SavedDraft?> heldRows = [];
    // When each row was last written while shared (a Stopwatch timestamp), for SharedPutInterval.
    private readonly Dictionary<string, long> lastPut = [];

    /// <summary>The longest a chat's draft is kept from the server while it is being typed: at most one put of it in this time. Switching conversation and quitting send at once.</summary>
    public TimeSpan SharedPutInterval { get; set; } = TimeSpan.FromSeconds(2);

    private static bool SameDraft(SavedDraft? a, SavedDraft? b) => IsEmpty(a) ? IsEmpty(b) : !IsEmpty(b) && a == b;

    private string? DraftKey(string id) => FiledUnder(id) is { } profile ? PrivateState.KeyOf(profile, "draft", id) : null;

    // How much longer a draft being typed waits before it is saved: the rest of SharedPutInterval since its last put, when the server keeps it.
    private TimeSpan SharedPause(string id)
    {
        if (hostRows is null || DraftKey(id) is not { } key || !hostRows.Shares(key) || !lastPut.TryGetValue(key, out var at)) return TimeSpan.Zero;
        return SharedPutInterval - Stopwatch.GetElapsedTime(at);
    }

    // The draft was read from the store when its conversation was opened.
    private void NoteRead(string profile, string id, string? scope, SavedDraft? saved)
    {
        if (hostRows is null) return;
        var filed = owners is null || !DraftOwners.Partitioned(id) ? profile : scope is null ? null : DraftOwners.FileUnder(profile, id, scope);
        if (filed is null) return;
        heldRows[PrivateState.KeyOf(filed, "draft", id)] = IsEmpty(saved) ? null : saved;
        edited.Remove(id);
    }

    // Whether the store holds this draft already and the server keeps it: saving it again would only send the same words to every device (and, once a quit
    // left it unsent, put them back over what another device wrote since).
    private bool Unchanged(string profile, string id, SavedDraft draft)
    {
        if (hostRows is null || unfiledRows.Contains(id)) return false;
        var key = PrivateState.KeyOf(profile, "draft", id);
        return heldRows.TryGetValue(key, out var was) && SameDraft(was, draft) && hostRows.Shares(key);
    }

    // The draft is in the store now (written, or found there already). What is not open in a box has nothing unsaved any more.
    private void NotePut(string profile, string id, SavedDraft draft, bool wrote)
    {
        if (hostRows is null) return;
        var key = PrivateState.KeyOf(profile, "draft", id);
        heldRows[key] = IsEmpty(draft) ? null : draft;
        if (wrote && hostRows.Shares(key)) lastPut[key] = Stopwatch.GetTimestamp();
        if (!Boxed(id)) edited.Remove(id);
    }

    // The store took drafts from the server (another device's, or a snapshot). Raised off the interface thread.
    private void OnBookHostChanged(object? sender, IReadOnlyList<string> keys)
    {
        if (ended || !keys.Any(key => key.StartsWith("draft:", StringComparison.Ordinal))) return;
        if (home is not null && SynchronizationContext.Current != home) { home.Post(_ => OnBookHostChanged(sender, keys), null); return; }
        _ = ApplyRemoteDraftsAsync(keys);
    }

    /// <summary>The caret left this box: another device's draft that came meanwhile is taken, unless words were changed here by then.</summary>
    internal void Blurred(IDraftBox box)
    {
        if (hostRows is not null && box.Open is { } id && DraftKey(id) is { } key) _ = ApplyRemoteDraftsAsync([key], blur: true);
    }

    // Each conversation held here whose row changed: one open in a box is left alone when the caret is in it or it was changed here (and a message being
    // edited leaves the draft set aside to be replaced the same way); every other takes the stored one, so nothing stale is written back at the next save or
    // at quit. Counts and fixed words only in the log.
    private async Task ApplyRemoteDraftsAsync(IReadOnlyList<string> keys, bool blur = false)
    {
        if (hostRows is null || privateState is null) return;
        var changed = keys.Where(key => key.StartsWith("draft:", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        int applied = 0, kept = 0;
        await draftGate.WaitAsync();
        try
        {
            if (ended || draftProfile is null) return;
            foreach (var id in loadedDrafts.Concat(boxes.Select(box => box.Open).OfType<string>()).Distinct().ToArray())
            {
                if (FiledUnder(id) is not { } filed || DraftKey(id) is not { } key || !changed.Contains(key)) continue;
                if (unreadableDrafts.Contains(id) || parked.Keys.Any(parkedKey => parkedKey.Id == id)) continue;
                SavedDraft? stored;
                try { stored = await privateState.ReadAsync<SavedDraft>(filed, "draft", id); }
                catch (Exception) { continue; /* The next change, or opening it again, reads it. */ }
                if (ended) return;
                var box = BoxOf(id);
                if (box is null && !loadedDrafts.Contains(id)) continue;
                // Decided here, with nothing awaited before the words are replaced: what is typed meanwhile is judged by this.
                if (edited.Contains(id) || box is not null && box.Guarded)
                {
                    // The words here stand, and the next save overwrites the other device's; saved even if they read as the stored ones by then.
                    heldRows.Remove(key);
                    if (!blur) kept++;
                    continue;
                }
                if (SameDraft(CaptureDraft(id), stored)) { heldRows[key] = IsEmpty(stored) ? null : stored; continue; }
                var text = stored?.Text ?? "";
                box?.TakeRemote(text);
                if (text.Length == 0) Texts.Remove(id); else Texts[id] = text;
                if (stored?.ReplyId is { } replyId) Replies[id] = (replyId, stored.ReplyPreview ?? "Reply to an earlier message"); else Replies.Remove(id);
                heldRows[key] = IsEmpty(stored) ? null : stored;
                box?.RepliesChanged();
                applied++;
            }
        }
        catch (Exception) { /* A draft that could not be taken is taken at the next change, or when its conversation is opened again. */ }
        finally { draftGate.Release(); }
        if (applied + kept > 0) owners?.Note($"drafts remote applied={applied} kept={kept}");
    }

    // ---- Saving ----

    /// <summary>Words were typed (or a reply chosen) in a box: its conversation's draft is saved at the next pause.</summary>
    internal void Typed(IDraftBox box, string id)
    {
        if (privateState is null || draftProfile is null) return;
        // Changed here since it was loaded: another device's draft for it never replaces these words (ApplyRemoteDraftsAsync).
        if (hostRows is not null) edited.Add(id);
        unsaved.Add(id);
        CancelPending(id);
        var delay = new CancellationTokenSource();
        pending[id] = (delay, box);
        _ = DebounceDraftAsync(id, box, delay);
    }

    private void CancelPending(string id)
    {
        if (!pending.Remove(id, out var was)) return;
        was.Delay.Cancel(); was.Delay.Dispose();
    }

    /// <summary>A pause that this box's typing started, for a conversation it no longer holds, waits no more (its words were captured already).</summary>
    internal void CancelPendingOf(IDraftBox box)
    {
        foreach (var (id, was) in pending.ToArray()) if (ReferenceEquals(was.By, box)) CancelPending(id);
    }

    private void CancelAllPending() { foreach (var id in pending.Keys.ToArray()) CancelPending(id); }

    // Only the conversation being edited can hold an unsaved draft, so the pause in typing saves that one. Writing
    // every draft loaded this session cost one encrypted transaction each, around 8 ms apiece, after every pause: a
    // session that had visited a few dozen conversations spent most of a second on it and the composer stuttered.
    private async Task DebounceDraftAsync(string id, IDraftBox box, CancellationTokenSource delay)
    {
        var token = delay.Token;
        try { await Task.Delay(400, token); } catch (OperationCanceledException) { return; }
        // Shared with a server: at most one put of a chat's draft in SharedPutInterval while it is being typed. Leaving the conversation and quitting save
        // at once (FlushAsync, FlushAllAsync), not through here.
        if (SharedPause(id) is { Ticks: > 0 } more)
        {
            try { await Task.Delay(more, token); } catch (OperationCanceledException) { return; }
        }
        if (pending.TryGetValue(id, out var now) && ReferenceEquals(now.Delay, delay)) { pending.Remove(id); delay.Dispose(); }
        await FlushAsync(id, box);
    }

    /// <summary>
    /// One conversation's draft, for sending or leaving it: clearing the draft of the message just sent needs one encrypted
    /// write, not one for every conversation that has ever had a draft, which is what held a sent message off screen for a
    /// second. <paramref name="by"/> is told how it went.
    /// </summary>
    internal async Task<bool> FlushAsync(string id, IDraftBox? by)
    {
        if (privateState is null) return true;
        if (draftProfile is null) return !HoldsWords && !boxes.Any(box => box.Words.Length > 0);
        CancelPending(id);
        // A conversation never loaded here has no draft here, only the one saved on disk: capturing it would read as empty
        // and the write would erase that saved draft (popping a conversation out from the list menu did).
        if (!Boxed(id) && !loadedDrafts.Contains(id) && !Texts.ContainsKey(id) && !Replies.ContainsKey(id) && !waiting.ContainsKey(id)) return true;
        await EnsureOwnersAsync([id]);
        var draft = CaptureDraft(id);
        var profile = FiledUnder(id);
        var owner = draftScopes.GetValueOrDefault(id);
        var report = by is not null && by.Open == id;
        await draftGate.WaitAsync();
        string? status = null;
        try
        {
            // As in FlushAllAsync: a draft another device wrote may have been installed while the gate was held.
            if (hostRows is not null) draft = CaptureDraft(id);
            if (profile is null)
            {
                // No account to file it under yet: kept for none, and only a failure to keep it is a failure to save.
                var kept = await KeepUnfiledAsync(id, draft);
                Note(id, kept);
                if (report) status = kept ? SavedStatus : FailedStatus;
                return kept;
            }
            var skipped = Unchanged(profile, id, draft);
            if (!skipped) await SaveAndSettleAsync(profile, id, draft);
            NotePut(profile, id, draft, wrote: !skipped);
            Note(id, true);
            // Words in their wait are not a draft the person has: the list doesn't call them one.
            Saved(id, waiting.ContainsKey(id) ? DraftShown(id) : draft.Text, owner);
            // Words that could not be saved when their conversation was opened as another account are tried again with this.
            var parkedSaved = await SaveParkedLockedAsync();
            // Every other loaded draft was written when its conversation was left, so saving the open one saves them all.
            if (!parkedSaved) status = FailedStatus;
            else if (report) status = SavedStatus;
            return true;
        }
        catch { Note(id, false); status = FailedStatus; return false; }
        finally
        {
            draftGate.Release();
            // A failure is said in the box that asked, or in every box when nothing asked (a pause, a wait ending).
            if (status is not null && (by is not null || status == FailedStatus)) foreach (var box in by is not null ? [by] : boxes.ToArray()) box.ReportStorage(status);
            else by?.ReportStorage(null);
        }
    }

    // A write of words captured before more were typed stores the older words: those wait for their own pause.
    private void Note(string id, bool stored) { if (!stored) unsaved.Add(id); else if (!pending.ContainsKey(id)) unsaved.Remove(id); }

    /// <summary>Every draft held here, and what every box holds: at quit, before a merge, a reset or an update.</summary>
    internal async Task<bool> FlushAllAsync(IDraftBox? by)
    {
        if (privateState is null) return true;
        if (draftProfile is null) return !HoldsWords && !boxes.Any(box => box.Words.Length > 0);
        CancelAllPending();
        var ids = loadedDrafts.Concat(Texts.Keys).Concat(Replies.Keys).Concat(boxes.Select(box => box.Open).OfType<string>()).Distinct().ToArray();
        await EnsureOwnersAsync(ids);
        var snapshot = ids.ToDictionary(id => id, CaptureDraft);
        await draftGate.WaitAsync();
        var kept = true;
        try
        {
            foreach (var (id, taken) in snapshot)
            {
                // Shared with a server: another device's draft may have been installed while the gate was held (ApplyRemoteDraftsAsync), so what was captured
                // before waiting for it is the old words, and writing them would delete the new. Captured again here; in process nothing else changes memory.
                var draft = hostRows is null ? taken : CaptureDraft(id);
                if (FiledUnder(id) is not { } profile) { var one = await KeepUnfiledAsync(id, draft); Note(id, one); kept &= one; continue; }
                var skipped = Unchanged(profile, id, draft);
                if (!skipped) await SaveAndSettleAsync(profile, id, draft);
                NotePut(profile, id, draft, wrote: !skipped);
                Note(id, true);
                Saved(id, waiting.ContainsKey(id) ? DraftShown(id) : draft.Text, draftScopes.GetValueOrDefault(id));
            }
            kept &= await SaveParkedLockedAsync();
            return kept;
        }
        catch { kept = false; foreach (var id in ids) unsaved.Add(id); return false; }
        finally
        {
            draftGate.Release();
            by?.ReportStorage(kept ? SavedStatus : FailedStatus);
        }
    }

    // How many conversations have text saved on this PC, for Settings to report and to offer to clear. Rows left by
    // earlier versions, which stored a row for every conversation opened whether or not anything was written in it,
    // are removed as they are counted: they are not drafts and should never have been kept. A row that could not be read
    // (no value) is left alone: this runs at every launch, and a read that fails once must not cost someone their draft.
    internal async Task<int> TidySavedAsync()
    {
        if (privateState is null) return 0;
        var saved = 0;
        await draftGate.WaitAsync();
        try
        {
            foreach (var (key, draft) in await privateState.ReadCategoryAsync<SavedDraft>("draft"))
            {
                if (draft is null) continue;
                if (!IsEmpty(draft)) { saved++; continue; }
                try { await privateState.RemoveKeyAsync(key); } catch (Exception) { /* A row that will not delete is still not a draft. */ }
            }
            return saved;
        }
        catch (Exception) { return saved; }
        finally { draftGate.Release(); }
    }

    // What is held about words kept for no account, for the conversations the test matches.
    private void ForgetUnfiled(Func<string, bool> matches)
    {
        unfiledRows.RemoveWhere(id => matches(id)); unfiledOnly.RemoveWhere(id => matches(id));
        foreach (var id in awaitingMerge.Keys.Where(matches).ToArray()) awaitingMerge.Remove(id);
    }

    // Everything held for the conversations the test matches, bar the reply and words of the ones open in a box, whose
    // text belongs to whoever is writing it.
    private void ForgetHeld(Func<string, bool> matches)
    {
        foreach (var id in Texts.Keys.Where(matches).ToArray()) Texts.Remove(id);
        foreach (var id in Replies.Keys.Where(matches).ToArray()) Replies.Remove(id);
        loadedDrafts.RemoveWhere(id => matches(id));
        edited.RemoveWhere(id => matches(id)); heldRows.Clear();
        foreach (var id in draftScopes.Keys.Where(matches).ToArray()) draftScopes.Remove(id);
        unsaved.RemoveWhere(id => matches(id));
        ForgetUnfiled(matches);
    }

    /// <summary>
    /// The drafts of the conversations the test matches, saved and held here, except the ones open in a box, whose text
    /// belongs to whoever is writing it. Saved rows are found by digest among ids; returns how many rows went.
    /// </summary>
    internal async Task<int> ForgetAsync(string profile, IEnumerable<string> ids, Func<string, bool> matches)
    {
        if (privateState is null) return 0;
        bool Forgets(string id) => !Boxed(id) && matches(id);
        foreach (var id in pending.Keys.Where(Forgets).ToArray()) CancelPending(id);
        await draftGate.WaitAsync();
        try
        {
            ForgetHeld(Forgets);
            foreach (var key in parked.Keys.Where(key => matches(key.Id)).ToArray()) parked.Remove(key);
            // The words kept for no account yet are drafts too: left, they would be filed back to the account at the next run.
            if (owners is not null) await owners.ForgetUnfiledWhereAsync(profile, Forgets);
            var removed = await privateState.RemoveIdsAsync(profile, "draft", ids.Where(id => !Boxed(id)));
            foreach (var id in removed) Saved(id, "");
            return removed.Count;
        }
        finally { draftGate.Release(); }
    }

    /// <summary>
    /// Everything saved on this PC, for every conversation and every profile stored here. Text being written now stays: it
    /// belongs to the open box, not to storage, and is saved again at the next pause.
    /// </summary>
    internal async Task<bool> ClearSavedAsync()
    {
        if (privateState is null) return true;
        foreach (var id in pending.Keys.ToArray()) CancelPending(id);
        await draftGate.WaitAsync();
        try
        {
            foreach (var (key, _) in await privateState.ReadCategoryAsync<SavedDraft>("draft")) await privateState.RemoveKeyAsync(key);
            ForgetHeld(id => !Boxed(id));
            parked.Clear();
            if (owners is not null) await owners.ForgetAllUnfiledAsync();
            ClearAll();
            // Words in Undo send's wait aren't drafts the person has: this took their only safe copy. Written again once the gate is free.
            foreach (var id in waiting.Keys.ToArray()) _ = FlushAsync(id, null);
            return true;
        }
        finally { draftGate.Release(); }
    }
}
