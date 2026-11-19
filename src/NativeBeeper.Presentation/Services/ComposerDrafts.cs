using System.Globalization;
using System.Text;

namespace NativeBeeper.Presentation.Services;

// One conversation's saved draft, as the composer stores it in the protected "draft" rows. Shared so the list's index
// can read the same rows; the stored shape is unchanged from when it was the composer's own.
internal sealed record SavedDraft(string Text, string? ReplyId, string? ReplyPreview);

/// <summary>
/// Which conversations have unsent text, for the list's "Draft: …" line and its Drafts view. Read once from the
/// protected draft rows when the list loads, then kept current by every composer: a completed draft save (never a
/// keystroke), a send, a reset or a clear. Draft text lives here only in memory, as a one-line preview; it is never put
/// on a <c>ChatSummary</c>, so the saved list cache never holds it, and it is never logged.
/// A conversation open anywhere — in a composer, being switched to, or open in the main window before a merged
/// conversation has chosen its service — shows no draft line: its text belongs to whoever is writing it.
/// </summary>
public sealed class ComposerDrafts
{
    public const int PreviewLength = 80;
    private readonly DraftOwners? owners;
    private readonly object gate = new();
    // Loaded rows, by stored key (a keyed hash: the rows name no conversation); what composers reported since, by id.
    private readonly Dictionary<string, string> byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> reported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> keys = new(StringComparer.Ordinal);
    private readonly Dictionary<object, string> held = new(ReferenceEqualityComparer.Instance);
    private string? profile;
    private string? loadedProfile;
    // Where the rows were read from, to read them again when the Messenger account changes; and how many such reads run.
    private PrivateState? state;
    private int reloading;

    /// <param name="owners">Which account owns a Messenger conversation's draft; without it every row is read as it always was.</param>
    public ComposerDrafts(DraftOwners? owners = null)
    {
        this.owners = owners;
        if (owners is not null) owners.Changed += (_, _) => AccountChanged();
    }

    /// <summary>Raised with a conversation id whose draft line may have changed; an empty id means every row.</summary>
    public event EventHandler<string>? Changed;
    /// <summary>The profile the saved rows were read for, or null before the first successful read.</summary>
    public string? Profile { get { lock (gate) return loadedProfile; } }
    /// <summary>
    /// Whether any conversation might have a draft: a stored row was read, or a composer reported one since. The stored
    /// rows are every profile's (a row's key is a keyed hash naming neither profile nor conversation), so which of them
    /// are this profile's is known only by asking <see cref="For"/> about a conversation; the list counts those itself.
    /// </summary>
    public bool HasAny
    {
        get
        {
            lock (gate)
            {
                if (byKey.Count > 0) return true;
                foreach (var preview in reported.Values) if (preview.Length > 0) return true;
                return false;
            }
        }
    }

    /// <summary>Reads the saved draft rows once per profile. A failed read leaves the index as it was, to be read again.</summary>
    public async Task LoadAsync(PrivateState state, string profile, bool again = false)
    {
        if (!again && Profile == profile) return;
        var rows = await state.ReadCategoryAsync<SavedDraft>("draft");
        var previews = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, draft) in rows)
            if (draft is not null && Preview(draft.Text) is { Length: > 0 } preview) previews[key] = preview;
        lock (gate)
        {
            // Another profile's reports are not this one's drafts.
            if (this.profile is not null && this.profile != profile) { reported.Clear(); keys.Clear(); }
            this.profile = profile;
            loadedProfile = profile;
            this.state = state;
            byKey.Clear();
            foreach (var (key, preview) in previews) byKey[key] = preview;
        }
        Follow(state);
    }

    // ---- Drafts another device wrote (stage 6, slice 6) ----
    //
    // In a client of a Messages Server the draft rows are shared, so another device's draft changes a line the list shows: the lines it replaces are let go
    // and the rows are read again. Only a store that shares rows (state.Shared) is followed: in the in-process app and the loopback nothing subscribes.
    private ISharedRows? following;
    private int hostReading, hostAgain;

    private void Follow(PrivateState state)
    {
        if (state.Shared is not { } rows) return;
        lock (gate)
        {
            if (ReferenceEquals(following, rows)) return;
            following = rows;
        }
        rows.HostChanged += OnHostChanged;
    }

    private void OnHostChanged(object? sender, IReadOnlyList<string> keys)
    {
        var changed = keys.Where(key => key.StartsWith("draft:", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (changed.Count == 0) return;
        lock (gate)
        {
            if (profile is null) return;
            // What a composer reported since the rows were read is older than the row that came.
            foreach (var id in reported.Keys.ToArray())
                if (KeyFor(id) is { } key && changed.Contains(key)) reported.Remove(id);
        }
        _ = ReloadForHostAsync();
    }

    // A burst of drafts is one read; one that comes while it runs makes it go round once more.
    private async Task ReloadForHostAsync()
    {
        if (Interlocked.Exchange(ref hostReading, 1) == 1) { Volatile.Write(ref hostAgain, 1); return; }
        while (true)
        {
            try
            {
                do
                {
                    Volatile.Write(ref hostAgain, 0);
                    PrivateState? read; string? forProfile;
                    lock (gate) { read = state; forProfile = loadedProfile; }
                    if (read is null || forProfile is null) break;
                    try { await LoadAsync(read, forProfile, again: true); }
                    catch (Exception) { /* The next draft, or the next load of the list, reads them again. */ }
                    Changed?.Invoke(this, "");
                } while (Volatile.Read(ref hostAgain) == 1);
            }
            finally { Volatile.Write(ref hostReading, 0); }
            // A change that came after the last look at hostAgain and before the release found the read still running, and went.
            if (Volatile.Read(ref hostAgain) == 0 || Interlocked.Exchange(ref hostReading, 1) == 1) return;
        }
    }

    /// <summary>
    /// Once the list shows Messenger's conversations: drafts saved before they were filed by account go to the account
    /// signed in at the first look (<see cref="DraftOwners.ClaimAsync"/>), and the rows are read again if any moved.
    /// </summary>
    public async Task<bool> ClaimUnownedAsync(PrivateState state, string profile, IEnumerable<string> listed)
    {
        if (owners is null) return false;
        var ids = listed.ToArray();
        // Messenger's conversations are listed, so its account can be told; the rows' lines wait for it (For).
        if (owners.Messenger is null && ids.Any(DraftOwners.Partitioned)) await owners.RefreshAsync();
        if (!await owners.ClaimAsync(profile, ids)) return false;
        await LoadAsync(state, profile, again: true);
        return true;
    }

    /// <summary>The one-line preview of a conversation's draft, or null. Whether it is open is <see cref="IsOpen"/>.</summary>
    public string? For(string chatId)
    {
        lock (gate)
        {
            return ForUnlocked(chatId);
        }
    }

    /// <summary>
    /// A composer saved this conversation's draft; empty text means it has none now. <paramref name="owner"/> is the account
    /// the draft was filed under, for a conversation whose id does not name one: a save for an account that is no longer
    /// the signed-in one is not what the list shows.
    /// </summary>
    public void Saved(string chatId, string text, string? owner = null)
    {
        var preview = Preview(text);
        lock (gate)
        {
            if (owners is not null && DraftOwners.Partitioned(chatId) && (owner is null || owner != owners.Messenger)) return;
            var before = ForUnlocked(chatId);
            reported[chatId] = preview;
            if (before == (preview.Length == 0 ? null : preview)) return;
        }
        Changed?.Invoke(this, chatId);
    }

    /// <summary>Every saved draft was removed from this PC (Settings). Text still open in a composer is saved again at its next pause.</summary>
    public void ClearAll()
    {
        lock (gate)
        {
            byKey.Clear();
            foreach (var id in reported.Keys.ToArray()) reported[id] = "";
        }
        Changed?.Invoke(this, "");
    }

    /// <summary>
    /// Which conversation a holder has open now, or null when it has none. A holder is any object: a composer, the
    /// conversation a composer is switching to, or the main window's open conversation. Raises for the one let go and
    /// the one taken; a row's line changes only when no holder is left on it.
    /// </summary>
    public void Holding(object holder, string? chatId)
    {
        string? previous;
        lock (gate)
        {
            previous = held.GetValueOrDefault(holder);
            if (previous == chatId) return;
            if (chatId is null) held.Remove(holder); else held[holder] = chatId;
        }
        if (previous is not null) Changed?.Invoke(this, previous);
        if (chatId is not null) Changed?.Invoke(this, chatId);
    }

    /// <summary>Whether any holder has this conversation open.</summary>
    public bool IsOpen(string chatId)
    {
        lock (gate)
        {
            foreach (var id in held.Values) if (id == chatId) return true;
            return false;
        }
    }

    /// <summary>
    /// The draft's first line with its whitespace collapsed, at most <see cref="PreviewLength"/> text elements, so an emoji
    /// is never split; an empty string when there is nothing written.
    /// </summary>
    public static string Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(part => !string.IsNullOrWhiteSpace(part)) ?? "";
        var collapsed = new StringBuilder(line.Length);
        var space = false;
        foreach (var c in line.Trim())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && collapsed.Length > 0) collapsed.Append(' ');
            space = false;
            collapsed.Append(c);
        }
        var value = collapsed.ToString();
        var starts = StringInfo.ParseCombiningCharacters(value);
        if (starts.Length <= PreviewLength) return value;
        return value[..starts[PreviewLength - 1]].TrimEnd() + "…";
    }

    private string? ForUnlocked(string chatId)
    {
        if (reported.TryGetValue(chatId, out var known)) return known.Length == 0 ? null : known;
        if (reloading > 0 && owners is not null && DraftOwners.Partitioned(chatId)) return null;
        return byKey.Count == 0 || profile is null || KeyFor(chatId) is not { } key ? null : byKey.GetValueOrDefault(key);
    }

    // Hashing is the cost of a lookup, so each conversation's key is worked out once. A Messenger conversation's row is
    // filed under the signed-in account, so until the account is known it has none to find.
    private string? KeyFor(string chatId)
    {
        var partitioned = owners is not null && DraftOwners.Partitioned(chatId);
        var owner = partitioned ? owners!.Messenger : null;
        if (partitioned && owner is null) return null;
        var cacheKey = owner is null ? chatId : chatId + "\n" + owner;
        if (keys.TryGetValue(cacheKey, out var key)) return key;
        return keys[cacheKey] = PrivateState.KeyOf(owner is null ? profile! : DraftOwners.FileUnder(profile!, chatId, owner), "draft", chatId);
    }

    // The signed-in Messenger account is not the one every Messenger draft line here was worked out for: none of them is
    // known any more, and every row looks again. The rows read at launch are older than anything sent or cleared since, so
    // they are read again for the account now signed in; until they are, a draft that is gone would show from its stale line.
    private void AccountChanged()
    {
        PrivateState? read; string? forProfile;
        lock (gate)
        {
            foreach (var id in reported.Keys.Where(DraftOwners.Partitioned).ToArray()) reported.Remove(id);
            read = state; forProfile = loadedProfile;
            // The stale rows are not offered for Messenger's conversations while they are read again.
            if (read is not null && forProfile is not null) reloading++;
        }
        Changed?.Invoke(this, "");
        if (read is not null && forProfile is not null) _ = ReloadAsync(read, forProfile);
    }

    private async Task ReloadAsync(PrivateState read, string forProfile)
    {
        try { await LoadAsync(read, forProfile, again: true); }
        catch (Exception) { /* The next change of account, or the next load of the list, reads them again. */ }
        finally { lock (gate) reloading--; }
        Changed?.Invoke(this, "");
    }
}
