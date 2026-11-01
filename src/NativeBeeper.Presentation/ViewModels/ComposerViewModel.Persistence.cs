using System.Diagnostics;
using NativeBeeper.Presentation.Services;
namespace NativeBeeper.Presentation.ViewModels;
public sealed partial class ComposerViewModel
{
    private readonly PrivateState? privateState;
    private readonly DraftOwners? owners;
    private readonly SynchronizationContext? home = SynchronizationContext.Current;
    // A Messenger conversation id names no account, so the text and reply held here for one are tied to the account they
    // were written as (DraftOwners). Absent for any other service, and for a Messenger conversation whose account was not
    // known: that is held in memory only, saved nowhere and offered to no account.
    private readonly Dictionary<string, string> draftScopes = [];
    // Words held for a Messenger conversation while its account is not known are kept in a protected row of their own
    // (DraftOwners.KeepUnfiledAsync), named by this composer, so that quitting does not lose them; they are filed to the
    // account once it is known. Rows kept by this composer; and conversations whose saved draft could not be read to join,
    // which stay unfiled however the account reads (filing them would replace what is saved).
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
    /// <summary>The signed-in Messenger account changed, so anything shown for a Messenger conversation may be another account's.</summary>
    public event EventHandler? AccountChanged;
    // The list's index of which conversations have a draft, told after each completed save; null where no list shows drafts.
    private readonly ComposerDrafts? draftIndex;
    // The conversation being switched to holds its row from the click on, before this composer holds it (SelectAsync);
    // a disposed composer takes no hold again, even from a switch still finishing.
    private readonly object opening = new();
    private bool holdsEnded;
    private readonly SemaphoreSlim draftGate = new(1);
    private readonly HashSet<string> loadedDrafts = [];
    // Conversations whose saved draft could not be read when opened. Their empty box is what failed to load, not a draft
    // the user cleared, so it never removes the row; text written there replaces it as usual.
    private readonly HashSet<string> unreadableDrafts = [];
    private CancellationTokenSource? draftDelay;
    private string? draftProfile;
    // The conversation whose draft a pending debounce would write. Handing that conversation to another window, or
    // flushing it outright, must cancel the pending write: it captures an empty draft once the conversation is gone.
    private string? draftPending;
    private long draftSelection;
    private bool restoringDraft;
    public bool DraftsSaved { get; private set; }
    public string DraftStorageStatus { get; private set; } = "";
    // The profile a conversation's draft is filed under. A Messenger conversation's also names the account it was written as,
    // so the same thread under another account is another row; with that account not known there is nowhere to file it.
    private string? FiledUnder(string id) =>
        draftProfile is null ? null
        : owners is null || !DraftOwners.Partitioned(id) ? draftProfile
        : draftScopes.TryGetValue(id, out var scope) ? DraftOwners.FileUnder(draftProfile, id, scope) : null;

    // Words that came back to a Messenger conversation this composer never opened (an undone send) are the signed-in
    // account's, which is looked up now. One that was opened without its account known stays unfiled.
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

    // Whether what is open was written as the account signed in now. Not for a conversation whose id names its account.
    private bool WrittenAsSignedInAccount() => chatId is not { } id || HeldAsSignedInAccount(id);
    private bool HeldAsSignedInAccount(string id) =>
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

    private void OwnerChanged(object? sender, EventArgs e)
    {
        if (home is not null && SynchronizationContext.Current != home) { home.Post(_ => OwnerChanged(sender, e), null); return; }
        if (holdsEnded) return;
        SendCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanSendOrQueue));
        AccountChanged?.Invoke(this, EventArgs.Empty);
        _ = ReselectAndFileAsync();
    }

    private async Task ReselectAndFileAsync()
    {
        // Words waiting to be joined to the account's saved draft try again, as does anything that could not be saved.
        if (chatId is { } id && (!WrittenAsSignedInAccount() || awaitingMerge.ContainsKey(id))) await ReselectAsync(id);
        await FileUnfiledAsync();
        await SaveParkedAsync();
    }

    // Words held for a Messenger conversation while the account was not known: not any account's yet.
    private bool HoldsUnfiled(string id) => (drafts.ContainsKey(id) || replies.ContainsKey(id)) && !draftScopes.ContainsKey(id);

    private static string JoinWords(string saved, string typed) => DraftOwners.JoinWords(saved, typed);

    // The account is known now: what was typed in the conversations not open meanwhile is its own words, so it is joined
    // to what the account has saved for each and saved as its. Typed words are never dropped; one that cannot be read or
    // written is kept in memory and is carried when the conversation is opened.
    private async Task FileUnfiledAsync()
    {
        if (owners?.Messenger is not { } scope || draftProfile is null || privateState is null) return;
        foreach (var id in drafts.Keys.Concat(replies.Keys).Distinct().Where(id => id != chatId && DraftOwners.Partitioned(id) && HoldsUnfiled(id)).ToArray())
        {
            try
            {
                var saved = await ReadSavedAsync(draftProfile, id, scope);
                if (!HoldsUnfiled(id) || id == chatId) continue;
                if (saved is not null)
                {
                    drafts[id] = JoinWords(saved.Text, drafts.GetValueOrDefault(id, ""));
                    if (saved.ReplyId is not null) replies.TryAdd(id, (saved.ReplyId, saved.ReplyPreview ?? "Reply to an earlier message"));
                }
                draftScopes[id] = scope; loadedDrafts.Add(id); unfiledOnly.Remove(id); awaitingMerge.Remove(id);
                await FlushDraftAsync(id);
            }
            catch (Exception) { /* Still in memory, unfiled; opening the conversation carries it then. */ }
        }
    }

    // The open conversation again, as the account signed in now: what was written as the other account is saved under it
    // and let go, and this account's own draft is read.
    private async Task ReselectAsync(string id)
    {
        try { await SelectAsync(id); }
        catch (Exception) { /* Choosing a conversation again starts over. */ }
    }

    // What is held here for a Messenger conversation was written as another account than the one it is opened as now: it is
    // saved under the account it belongs to and let go, so none of it reaches this one. Not let go from memory while that
    // save has failed: it moves to where only its own account finds it (parked) and is saved when storage allows.
    private async Task ForgetOtherAccountAsync(string id, string? scope)
    {
        if (!loadedDrafts.Contains(id) && !drafts.ContainsKey(id) && !replies.ContainsKey(id)) return;
        var owner = draftScopes.GetValueOrDefault(id);
        if (owner == scope) return;
        if (!await FlushDraftAsync(id))
        {
            // Words held for no account are not another account's, and are kept where they are.
            if (owner is null) return;
            parked[(id, owner)] = new(CaptureDraft(id), loadedDrafts.Contains(id));
        }
        drafts.Remove(id); replies.Remove(id); loadedDrafts.Remove(id); unreadableDrafts.Remove(id); draftScopes.Remove(id);
    }

    // The conversation is opened as the account that wrote words which could not be saved when it was left (ParkedDraft): they
    // come back into memory as that account's, as current as when they were left if the draft had been read, otherwise
    // joined to the saved draft as words typed with no account are. True when they are carried to be joined.
    private bool RestoreParked(string id, string scope, bool carried, out bool supersedesSaved)
    {
        supersedesSaved = false;
        if (!parked.Remove((id, scope), out var back)) return carried;
        var held = drafts.ContainsKey(id) || replies.ContainsKey(id);
        if (back.Loaded && !carried && !held)
        {
            if (back.Draft.Text.Length > 0) drafts[id] = back.Draft.Text;
            if (back.Draft.ReplyId is not null) replies[id] = (back.Draft.ReplyId, back.Draft.ReplyPreview ?? "Reply to an earlier message");
            loadedDrafts.Add(id);
            return false;
        }
        // Something is held for it already (words typed with no account, or an undone send): the two are joined, and the
        // saved draft is joined to both. Words that had been read from it already include it.
        supersedesSaved = back.Loaded;
        drafts[id] = JoinWords(back.Draft.Text, drafts.GetValueOrDefault(id, ""));
        if (back.Draft.ReplyId is not null) replies.TryAdd(id, (back.Draft.ReplyId, back.Draft.ReplyPreview ?? "Reply to an earlier message"));
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
            // The conversation is held for that account again by this composer (a send undone, say): they are joined to it.
            if (drafts.ContainsKey(key.Id) && draftScopes.GetValueOrDefault(key.Id) == key.Scope)
            {
                if (key.Id == chatId) continue;
                drafts[key.Id] = JoinWords(back.Draft.Text, drafts[key.Id]);
                if (back.Draft.ReplyId is not null) replies.TryAdd(key.Id, (back.Draft.ReplyId, back.Draft.ReplyPreview ?? "Reply to an earlier message"));
                parked.Remove(key);
                continue;
            }
            try
            {
                var filed = DraftOwners.FileUnder(draftProfile, key.Id, key.Scope);
                if (IsEmpty(back.Draft)) await privateState.RemoveAsync(filed, "draft", key.Id);
                else await privateState.WriteAsync(filed, "draft", key.Id, back.Draft);
                if (parked.TryGetValue(key, out var now) && ReferenceEquals(now, back)) parked.Remove(key);
                draftIndex?.Saved(key.Id, back.Draft.Text, key.Scope);
            }
            catch (Exception) { /* Still parked; the next save, account change or quit tries again. */ }
        }
        return parked.Count == 0;
    }

    private async Task SaveParkedAsync()
    {
        if (parked.Count == 0) return;
        await draftGate.WaitAsync();
        try
        {
            if (!await SaveParkedLockedAsync()) { DraftsSaved = false; DraftStorageStatus = "Could not save text drafts. Keep the app open to preserve them."; }
        }
        finally { draftGate.Release(); OnPropertyChanged(nameof(DraftStorageStatus)); }
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
        var body = id == chatId ? CurrentDraft : drafts.GetValueOrDefault(id, "");
        return replies.TryGetValue(id, out var reply) ? new(body, reply.Id, reply.Preview) : new(body, null, null);
    }
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
    // replaces words in a box the caret is in, or that has been changed since it was loaded (the next save of this composer simply overwrites the other
    // device's); a conversation not open here, or open and untouched, takes the newer draft. Staged attachments stay on the device (StagedByConversation).
    // Everything below is bookkeeping for that and does nothing in the in-process app and the loopback, whose state.db shares nothing (hostRows is null):
    // saves go through the same writes at the same moments there.
    private readonly ISharedRows? hostRows;
    // Conversations whose words were changed here since they were loaded or last saved (the open one stays until it is loaded again).
    private readonly HashSet<string> edited = [];
    // What each draft row held when this composer last read or wrote it (null: none), by stored key; a save of what is held already is not sent again.
    private readonly Dictionary<string, SavedDraft?> held = [];
    // When each row was last written while shared (a Stopwatch timestamp), for SharedPutInterval.
    private readonly Dictionary<string, long> lastPut = [];

    /// <summary>The longest a chat's draft is kept from the server while it is being typed: at most one put of it in this time. Switching conversation and quitting send at once.</summary>
    public TimeSpan SharedPutInterval { get; set; } = TimeSpan.FromSeconds(2);

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
            if (!value && hostRows is not null && chatId is { } id && DraftKey(id) is { } key) _ = ApplyRemoteDraftsAsync([key], blur: true);
        }
    }

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
        held[PrivateState.KeyOf(filed, "draft", id)] = IsEmpty(saved) ? null : saved;
        edited.Remove(id);
    }

    // Whether the store holds this draft already and the server keeps it: saving it again would only send the same words to every device (and, once a quit
    // left it unsent, put them back over what another device wrote since).
    private bool Unchanged(string profile, string id, SavedDraft draft)
    {
        if (hostRows is null || unfiledRows.Contains(id)) return false;
        var key = PrivateState.KeyOf(profile, "draft", id);
        return held.TryGetValue(key, out var was) && SameDraft(was, draft) && hostRows.Shares(key);
    }

    // The draft is in the store now (written, or found there already). What is not open here has nothing unsaved any more.
    private void NotePut(string profile, string id, SavedDraft draft, bool wrote)
    {
        if (hostRows is null) return;
        var key = PrivateState.KeyOf(profile, "draft", id);
        held[key] = IsEmpty(draft) ? null : draft;
        if (wrote && hostRows.Shares(key)) lastPut[key] = Stopwatch.GetTimestamp();
        if (id != chatId) edited.Remove(id);
    }

    // The store took drafts from the server (another device's, or a snapshot). Raised off the interface thread.
    private void OnHostChanged(object? sender, IReadOnlyList<string> keys)
    {
        if (holdsEnded || !keys.Any(key => key.StartsWith("draft:", StringComparison.Ordinal))) return;
        if (home is not null && SynchronizationContext.Current != home) { home.Post(_ => OnHostChanged(sender, keys), null); return; }
        _ = ApplyRemoteDraftsAsync(keys);
    }

    // Each conversation this composer holds a draft of whose row changed: the open one is left alone when the caret is in it or it was changed here (and a
    // message being edited leaves the draft set aside to be replaced the same way); every other takes the stored one, so nothing stale is written back at
    // the next save or at quit. Counts and fixed words only in the log.
    private async Task ApplyRemoteDraftsAsync(IReadOnlyList<string> keys, bool blur = false)
    {
        if (hostRows is null || privateState is null) return;
        var changed = keys.Where(key => key.StartsWith("draft:", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        int applied = 0, kept = 0;
        await draftGate.WaitAsync();
        try
        {
            if (holdsEnded || draftProfile is null) return;
            foreach (var id in loadedDrafts.Concat(chatId is null ? [] : [chatId]).Distinct().ToArray())
            {
                if (FiledUnder(id) is not { } filed || DraftKey(id) is not { } key || !changed.Contains(key)) continue;
                if (unreadableDrafts.Contains(id) || parked.Keys.Any(parkedKey => parkedKey.Id == id)) continue;
                SavedDraft? stored;
                try { stored = await privateState.ReadAsync<SavedDraft>(filed, "draft", id); }
                catch (Exception) { continue; /* The next change, or opening it again, reads it. */ }
                if (holdsEnded) return;
                var open = id == chatId;
                if (!open && !loadedDrafts.Contains(id)) continue;
                // Decided here, with nothing awaited before the words are replaced: what is typed meanwhile is judged by this.
                if (edited.Contains(id) || open && editing is null && InputFocused)
                {
                    // This composer's words stand, and its next save overwrites the other device's; saved even if they read as the stored ones by then.
                    held.Remove(key);
                    if (!blur) kept++;
                    continue;
                }
                if (SameDraft(CaptureDraft(id), stored)) { held[key] = IsEmpty(stored) ? null : stored; continue; }
                var text = stored?.Text ?? "";
                if (open && editing is { } session) session.SavedDraft = text;
                else if (open) SetBoxQuietly(text);
                if (text.Length == 0) drafts.Remove(id); else drafts[id] = text;
                if (stored?.ReplyId is { } replyId) replies[id] = (replyId, stored.ReplyPreview ?? "Reply to an earlier message"); else replies.Remove(id);
                held[key] = IsEmpty(stored) ? null : stored;
                if (open) NotifyReply();
                applied++;
            }
        }
        catch (Exception) { /* A draft that could not be taken is taken at the next change, or when its conversation is opened again. */ }
        finally { draftGate.Release(); }
        if (applied + kept > 0) owners?.Note($"drafts remote applied={applied} kept={kept}");
    }

    private void ScheduleDraftSave()
    {
        if (restoringDraft || editing is not null || privateState is null || chatId is null || draftProfile is null) return;
        // Changed here since it was loaded: another device's draft for it never replaces these words (ApplyRemoteDraftsAsync).
        if (hostRows is not null) edited.Add(chatId);
        DraftsSaved = false; draftDelay?.Cancel(); draftDelay?.Dispose(); draftDelay = new();
        draftPending = chatId;
        _ = DebounceDraftAsync(chatId, draftDelay.Token);
    }
    // Only the conversation being edited can hold an unsaved draft, so the pause in typing saves that one. Writing
    // every draft loaded this session cost one encrypted transaction each, around 8 ms apiece, after every pause: a
    // session that had visited a few dozen conversations spent most of a second on it and the composer stuttered.
    private async Task DebounceDraftAsync(string id, CancellationToken token)
    {
        try { await Task.Delay(400, token); } catch (OperationCanceledException) { return; }
        // Shared with a server: at most one put of a chat's draft in SharedPutInterval while it is being typed. Leaving the conversation and quitting save
        // at once (FlushDraftAsync, FlushDraftsAsync), not through here.
        if (SharedPause(id) is { Ticks: > 0 } more)
        {
            try { await Task.Delay(more, token); } catch (OperationCanceledException) { return; }
        }
        if (draftPending == id) draftPending = null;
        await FlushDraftAsync(id);
    }
    // One conversation's draft, for sending: clearing the draft of the message just sent needs one encrypted write, not
    // one for every conversation that has ever had a draft, which is what held a sent message off screen for a second.
    public async Task<bool> FlushDraftAsync(string id)
    {
        if (privateState is null) return true;
        if (draftProfile is null) return !HasDrafts;
        if (draftPending == id) { draftPending = null; draftDelay?.Cancel(); }
        // A conversation this composer never loaded has no draft here, only the one saved on disk: capturing it would read
        // as empty and the write would erase that saved draft (popping a conversation out from the list menu did).
        if (id != chatId && !loadedDrafts.Contains(id) && !drafts.ContainsKey(id) && !replies.ContainsKey(id)) return true;
        await EnsureOwnersAsync([id]);
        var draft = CaptureDraft(id);
        var profile = FiledUnder(id);
        var owner = draftScopes.GetValueOrDefault(id);
        await draftGate.WaitAsync();
        try
        {
            // As in FlushDraftsAsync: a draft another device wrote may have been installed while the gate was held.
            if (hostRows is not null) draft = CaptureDraft(id);
            if (profile is null)
            {
                // No account to file it under yet: kept for none, and only a failure to keep it is a failure to save.
                var kept = await KeepUnfiledAsync(id, draft);
                if (id == chatId) { DraftsSaved = kept; DraftStorageStatus = kept ? "Text drafts saved." : "Could not save text drafts. Keep the app open to preserve them."; }
                return kept;
            }
            var skipped = Unchanged(profile, id, draft);
            if (!skipped) await SaveAndSettleAsync(profile, id, draft);
            NotePut(profile, id, draft, wrote: !skipped);
            draftIndex?.Saved(id, draft.Text, owner);
            // Words that could not be saved when their conversation was opened as another account are tried again with this.
            var parkedSaved = await SaveParkedLockedAsync();
            // Every other loaded draft was written when its conversation was left, so saving the open one saves them all.
            if (!parkedSaved) { DraftsSaved = false; DraftStorageStatus = "Could not save text drafts. Keep the app open to preserve them."; }
            else if (id == chatId) { DraftsSaved = true; DraftStorageStatus = "Text drafts saved."; }
            return true;
        }
        catch { DraftsSaved = false; DraftStorageStatus = "Could not save text drafts. Keep the app open to preserve them."; return false; }
        finally { draftGate.Release(); OnPropertyChanged(nameof(DraftStorageStatus)); }
    }

    public async Task<bool> FlushDraftsAsync()
    {
        if (privateState is null) return true;
        if (draftProfile is null) return !HasDrafts;
        draftDelay?.Cancel(); draftPending = null;
        var ids = loadedDrafts.Concat(drafts.Keys).Concat(replies.Keys).Concat(chatId is null ? [] : new[] { chatId }).Distinct().ToArray();
        await EnsureOwnersAsync(ids);
        var snapshot = ids.ToDictionary(id => id, CaptureDraft);
        await draftGate.WaitAsync();
        try
        {
            var kept = true;
            foreach (var (id, taken) in snapshot)
            {
                // Shared with a server: another device's draft may have been installed while the gate was held (ApplyRemoteDraftsAsync), so what was captured
                // before waiting for it is the old words, and writing them would delete the new. Captured again here; in process nothing else changes memory.
                var draft = hostRows is null ? taken : CaptureDraft(id);
                if (FiledUnder(id) is not { } profile) { kept &= await KeepUnfiledAsync(id, draft); continue; }
                var skipped = Unchanged(profile, id, draft);
                if (!skipped) await SaveAndSettleAsync(profile, id, draft);
                NotePut(profile, id, draft, wrote: !skipped);
                draftIndex?.Saved(id, draft.Text, draftScopes.GetValueOrDefault(id));
            }
            kept &= await SaveParkedLockedAsync();
            DraftsSaved = kept; DraftStorageStatus = kept ? "Text drafts saved." : "Could not save text drafts. Keep the app open to preserve them.";
            return kept;
        }
        catch { DraftsSaved = false; DraftStorageStatus = "Could not save text drafts. Keep the app open to preserve them."; return false; }
        finally { draftGate.Release(); OnPropertyChanged(nameof(DraftStorageStatus)); }
    }

    // How many conversations have text saved on this PC, for Settings to report and to offer to clear. Rows left by
    // earlier versions, which stored a row for every conversation opened whether or not anything was written in it,
    // are removed as they are counted: they are not drafts and should never have been kept. A row that could not be read
    // (no value) is left alone: this runs at every launch, and a read that fails once must not cost someone their draft.
    public async Task<int> TidySavedDraftsAsync()
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

    // What this composer holds about words kept for no account, for the conversations the test matches.
    private void ForgetUnfiled(Func<string, bool> matches)
    {
        unfiledRows.RemoveWhere(id => matches(id)); unfiledOnly.RemoveWhere(id => matches(id));
        foreach (var id in awaitingMerge.Keys.Where(matches).ToArray()) awaitingMerge.Remove(id);
    }

    // The drafts of the conversations the test matches, saved and held here, except the one open in this composer, whose
    // text belongs to whoever is writing it. Saved rows are found by digest among ids; returns how many rows went.
    public async Task<int> ForgetDraftsAsync(string profile, IEnumerable<string> ids, Func<string, bool> matches)
    {
        if (privateState is null) return 0;
        if (draftPending is { } pending && pending != chatId && matches(pending)) { draftDelay?.Cancel(); draftPending = null; }
        await draftGate.WaitAsync();
        try
        {
            foreach (var id in drafts.Keys.Where(id => id != chatId && matches(id)).ToArray()) drafts.Remove(id);
            foreach (var id in replies.Keys.Where(id => id != chatId && matches(id)).ToArray()) replies.Remove(id);
            loadedDrafts.RemoveWhere(id => id != chatId && matches(id));
            edited.RemoveWhere(id => id != chatId && matches(id)); held.Clear();
            foreach (var id in draftScopes.Keys.Where(id => id != chatId && matches(id)).ToArray()) draftScopes.Remove(id);
            ForgetUnfiled(id => id != chatId && matches(id));
            foreach (var key in parked.Keys.Where(key => matches(key.Id)).ToArray()) parked.Remove(key);
            // The words kept for no account yet are drafts too: left, they would be filed back to the account at the next run.
            if (owners is not null) await owners.ForgetUnfiledWhereAsync(profile, id => id != chatId && matches(id));
            var open = chatId;
            var removed = await privateState.RemoveIdsAsync(profile, "draft", ids.Where(id => id != open));
            foreach (var id in removed) draftIndex?.Saved(id, "");
            return removed.Count;
        }
        finally { draftGate.Release(); }
    }

    // Everything saved on this PC, for every conversation and every profile stored here. Text being written now stays:
    // it belongs to the open composer, not to storage, and is saved again at the next pause.
    public async Task<bool> ClearSavedDraftsAsync()
    {
        if (privateState is null) return true;
        draftDelay?.Cancel(); draftPending = null;
        await draftGate.WaitAsync();
        try
        {
            foreach (var (key, _) in await privateState.ReadCategoryAsync<SavedDraft>("draft")) await privateState.RemoveKeyAsync(key);
            foreach (var id in drafts.Keys.Where(id => id != chatId).ToArray()) drafts.Remove(id);
            foreach (var id in replies.Keys.Where(id => id != chatId).ToArray()) replies.Remove(id);
            loadedDrafts.RemoveWhere(id => id != chatId);
            edited.RemoveWhere(id => id != chatId); held.Clear();
            foreach (var id in draftScopes.Keys.Where(id => id != chatId).ToArray()) draftScopes.Remove(id);
            ForgetUnfiled(id => id != chatId);
            parked.Clear();
            if (owners is not null) await owners.ForgetAllUnfiledAsync();
            draftIndex?.ClearAll();
            DraftStorageStatus = "Saved drafts cleared.";
            return true;
        }
        catch (Exception) { DraftStorageStatus = "Could not clear the saved drafts."; return false; }
        finally { draftGate.Release(); OnPropertyChanged(nameof(DraftStorageStatus)); }
    }
}
