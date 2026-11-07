using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

// Written once the drafts saved before accounts owned them were attributed: to the Messenger account that was the only one
// signed in when Messenger's conversations were first listed. Its presence ends the looking.
internal sealed record DraftOwnerMarker(string Owner);

/// <summary>
/// Which signed-in account owns a conversation's unsent work (text, the message being replied to, staged attachments).
/// A Messenger conversation id is the thread id alone, so a group both of two Facebook accounts are in has the same id
/// under each, and what was written there as one account must never be offered to, or sent as, the other. Google
/// Messages is one paired phone whose identity is already in the profile every row is stored under, and Voice and
/// Instagram ids carry their account, so Messenger is the one service whose rows are filed by account as well.
/// </summary>
public sealed class DraftOwners
{
    // MessengerModels.Account; the Backends project is not visible from here, and a harness check holds the two together.
    public const string MessengerAccount = "messenger:me";
    /// <summary>The category of the rows noting who owns what (the attribution marker, the conversations taken over, the accounts seen); the id of the last.</summary>
    public const string OwnerCategory = "draft-owner", KnownScopesId = "scopes";
    private const string MarkerCategory = OwnerCategory, MarkerId = "marker";
    private readonly IHostIdentity identity;
    private readonly PrivateState? state;
    private readonly Action<string>? log;
    private readonly SemaphoreSlim claiming = new(1);
    private string? scope;
    private string? decidedFor, absentLook;
    private bool hiddenLogged;
    private DraftOwnerMarker? marker;

    public DraftOwners(IHostIdentity identity, IMessengerAccount? messenger = null, PrivateState? state = null, Action<string>? log = null)
    {
        this.identity = identity; this.state = state; this.log = log;
        // Any change of a sign-in or of the conversations is a reason to look again; only a different answer is announced.
        if (messenger is not null) messenger.Changed += (_, _) => _ = SafeRefreshAsync();
        identity.Changed += (_, _) => _ = SafeRefreshAsync();
    }

    /// <summary>A line for the log, counts and fixed words only (the composers' and the draft index's, which hold these owners already).</summary>
    internal void Note(string line) => log?.Invoke(line);

    /// <summary>Whether drafts for this conversation are filed by account: its id does not name the account.</summary>
    public static bool Partitioned(string chatId) => chatId.StartsWith("messenger:", StringComparison.Ordinal) && NativeBeeper.Core.Messaging.AccountNamespace.KeyOf(chatId) is null;

    /// <summary>
    /// The Messenger account as last read (a one-way scope, never an id), or null when none is signed in or the account could
    /// not be told apart (the host names an account only when exactly one is signed in).
    /// </summary>
    public string? Messenger => Volatile.Read(ref scope);

    /// <summary>The Messenger account changed: a sign-in, a sign-out or a different account. Raised on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the Messenger account now. Answers it, and announces it when it is not what was last known.</summary>
    public async Task<string?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var now = (await identity.ReadAsync(cancellationToken)).SavedScopes.FirstOrDefault(s => s.AccountId == MessengerAccount)?.Scope;
        var before = Interlocked.Exchange(ref scope, now);
        // Words kept for no account yet are filed to it before anyone is told or reads its rows, so a caller that has the
        // answer finds them there.
        var filed = now is not null && await FileUnfiledAsync(now);
        if (before != now || filed) Changed?.Invoke(this, EventArgs.Empty);
        return now;
    }

    // Conversations change with every message, so a look already under way is asked to go round once more, not started again.
    private int refreshing, again;
    private async Task SafeRefreshAsync()
    {
        if (Interlocked.Exchange(ref refreshing, 1) == 1) { Volatile.Write(ref again, 1); return; }
        try
        {
            do
            {
                Volatile.Write(ref again, 0);
                try { await RefreshAsync(); }
                catch (Exception) { /* The next change, or the next conversation opened, reads it again. */ }
            } while (Volatile.Read(ref again) == 1);
        }
        finally { Volatile.Write(ref refreshing, 0); }
    }

    /// <summary>The "profile" a conversation's rows are filed under: the profile itself, or the profile and the account that owns it.</summary>
    public static string FileUnder(string profile, string chatId, string? owner) =>
        Partitioned(chatId) ? profile + "\nowner:" + owner : profile;

    // ---- Words kept while the account is not known ----
    //
    // A composer holds a Messenger conversation's words in memory while the account is not told apart yet (the window reopens a
    // conversation before Messenger's chats load, or the account is signed out). Quitting then must not lose them, and no
    // account may be handed them before it is known. So they are kept in a protected row under the profile alone, naming the
    // conversation and the composer that holds them, and are filed to the account at the first moment it is known: by the
    // composer itself while it lives, and by this class for a composer that has gone or a run that has ended.

    /// <summary>One composer's words for one Messenger conversation, kept for no account yet.</summary>
    internal sealed record UnfiledDraft(string ChatId, string Writer, string Text, string? ReplyId, string? ReplyPreview, bool Held = true);

    private const string UnfiledCategory = "draft-unfiled";
    private readonly object writersGate = new();
    private readonly HashSet<string> writers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim filing = new(1);
    private string? filedFor;

    /// <summary>A composer that keeps unfiled words says it is alive; its rows are its own to file until it is let go.</summary>
    public void Attach(string writer) { lock (writersGate) writers.Add(writer); }

    /// <summary>The composer is gone. Words it left unfiled are filed at the next look at the account.</summary>
    public void Detach(string writer)
    {
        lock (writersGate) writers.Remove(writer);
        Volatile.Write(ref filedFor, null);
    }

    /// <summary>
    /// Keeps these words unfiled, or lets go of the row when there are none. False when they could not be written (nowhere to
    /// keep them, or the write failed): the caller still holds them and says so.
    /// </summary>
    public async Task<bool> KeepUnfiledAsync(string profile, string writer, string chatId, string text, string? replyId, string? replyPreview)
    {
        if (state is null) return false;
        var id = chatId + "\n" + writer;
        try
        {
            if (text.Length == 0 && replyId is null) await state.RemoveAsync(profile, UnfiledCategory, id);
            else await state.WriteAsync(profile, UnfiledCategory, id, new UnfiledDraft(chatId, writer, text, replyId, replyPreview));
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The composer no longer holds these words (the conversation went to another window): the row is no longer its to file.</summary>
    public async Task LetGoUnfiledAsync(string profile, string writer, string chatId)
    {
        if (state is null) return;
        try
        {
            var id = chatId + "\n" + writer;
            if (await state.ReadAsync<UnfiledDraft>(profile, UnfiledCategory, id) is { } row)
                await state.WriteAsync(profile, UnfiledCategory, id, row with { Held = false });
            Volatile.Write(ref filedFor, null);
        }
        catch (Exception) { /* Filed with the next run, once its composer is gone. */ }
    }

    /// <summary>The composer filed its words to the account itself, or they were sent or cleared. False when the row could not be removed.</summary>
    public async Task<bool> ForgetUnfiledAsync(string profile, string writer, string chatId)
    {
        if (state is null) return true;
        try { await state.RemoveAsync(profile, UnfiledCategory, chatId + "\n" + writer); return true; }
        catch (Exception) { return false; /* Left, it is filed again and joined to words already there, which does not repeat them. */ }
    }

    /// <summary>
    /// The words kept unfiled for conversations whose drafts were cleared (a reset) are let go, whichever composer kept them:
    /// filed at the next look at the account they would bring the cleared words back. Throws when a row cannot be removed.
    /// </summary>
    public async Task ForgetUnfiledWhereAsync(string profile, Func<string, bool> matches)
    {
        if (state is null) return;
        foreach (var (key, row) in await state.ReadCategoryAsync<UnfiledDraft>(UnfiledCategory))
            if (row is not null && matches(row.ChatId) && key == PrivateState.KeyOf(profile, UnfiledCategory, row.ChatId + "\n" + row.Writer))
                await state.RemoveKeyAsync(key);
    }

    /// <summary>Every row of unfiled words goes, as clearing every saved draft does (one that cannot be read among them). Throws when a row cannot be removed.</summary>
    public async Task ForgetAllUnfiledAsync()
    {
        if (state is null) return;
        foreach (var (key, _) in await state.ReadCategoryAsync<UnfiledDraft>(UnfiledCategory)) await state.RemoveKeyAsync(key);
    }

    // The words other runs, and composers no longer open, kept for no account: joined to what the account has saved for
    // each conversation and saved as its, then let go. Every account seen is also noted for a backup, which cannot name the
    // rows of an account that is not signed in otherwise. True when it filed any. Once per account, until a composer goes.
    private async Task<bool> FileUnfiledAsync(string owner)
    {
        if (state is null || Volatile.Read(ref filedFor) == owner) return false;
        await filing.WaitAsync();
        try
        {
            if (Volatile.Read(ref filedFor) == owner) return false;
            if (await state.ProfileAsync() is not { } profile) return false;
            var moved = 0;
            string[] live;
            lock (writersGate) live = [.. writers];
            Volatile.Write(ref filedFor, owner);
            foreach (var (key, row) in await state.ReadCategoryAsync<UnfiledDraft>(UnfiledCategory))
            {
                // A row that cannot be read is left; one under another profile is not this one's.
                if (row is null || row.Held && live.Contains(row.Writer) || key != PrivateState.KeyOf(profile, UnfiledCategory, row.ChatId + "\n" + row.Writer)) continue;
                try
                {
                    var filed = FileUnder(profile, row.ChatId, owner);
                    var held = await state.ReadAsync<SavedDraft>(filed, "draft", row.ChatId);
                    if (held is null && await AdoptAsync(profile, row.ChatId, owner)) held = await state.ReadAsync<SavedDraft>(filed, "draft", row.ChatId);
                    await state.WriteAsync(filed, "draft", row.ChatId, new SavedDraft(JoinWords(held?.Text ?? "", row.Text), held?.ReplyId ?? row.ReplyId,
                        held?.ReplyId is not null ? held.ReplyPreview : row.ReplyPreview));
                    await state.RemoveKeyAsync(key);
                    moved++;
                }
                catch (Exception) { Volatile.Write(ref filedFor, null); /* Kept as it was; the next look files it. */ }
            }
            await NoteScopeAsync(profile, owner);
            if (moved > 0) log?.Invoke($"drafts unfiled filed={moved}");
            return moved > 0;
        }
        catch (Exception) { Volatile.Write(ref filedFor, null); return false; }
        finally { filing.Release(); }
    }

    // Every Messenger account that has signed in here, so a backup can name the draft rows filed under each.
    private async Task NoteScopeAsync(string profile, string owner)
    {
        var known = await state!.ReadAsync<List<string>>(profile, OwnerCategory, KnownScopesId) ?? [];
        if (known.Contains(owner, StringComparer.Ordinal)) return;
        known.Add(owner);
        await state.WriteAsync(profile, OwnerCategory, KnownScopesId, known);
    }

    /// <summary>Joins words typed to those saved, without repeating them when they are the same.</summary>
    internal static string JoinWords(string saved, string typed) =>
        typed.Length == 0 || typed == saved ? saved : saved.Length == 0 ? typed : saved + Environment.NewLine + typed;

    // ---- Drafts saved before they were filed by account ----
    //
    // Those rows sit under the profile alone, so nothing says whose they were. They are attributed once, to the Messenger
    // account signed in the first time Messenger's conversations are listed while exactly one is (the host names none
    // otherwise): until then they wait, hidden, and every run and every sign-in looks again. Once attributed they are not
    // attributed again. They are never deleted: the originals stay stored, and a note beside each says it was taken over, so
    // a draft the account has since sent or cleared does not come back from the original.

    /// <summary>
    /// Attributes the saved drafts of these listed conversations to the account signed in, the first time that is possible,
    /// and logs how many were and how many stay hidden. True when it changed what is saved.
    /// </summary>
    public async Task<bool> ClaimAsync(string profile, IEnumerable<string> listed)
    {
        if (state is null || decidedFor == profile) return false;
        var ids = listed.Where(Partitioned).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return false;
        // Who is signed in is read outside the lock: reading may file words kept for no account, which takes it.
        await claiming.WaitAsync();
        try { if (await MarkerExistsAsync(profile)) return false; }
        finally { claiming.Release(); }
        var owner = await RefreshAsync();
        await claiming.WaitAsync();
        try
        {
            if (decidedFor == profile || await MarkerExistsAsync(profile)) return false;
            if (owner is null)
            {
                // Not told apart yet (signed out, still connecting, or more than one): they wait, and are said once per run.
                if (!hiddenLogged && (await state.ExistingIdsAsync(profile, "draft", ids)).Count is var waiting and > 0)
                { hiddenLogged = true; log?.Invoke($"drafts migrated owner=0 hidden={waiting}"); }
                return false;
            }
            return await DecideAsync(profile, owner, ids);
        }
        finally { claiming.Release(); }
    }

    /// <summary>
    /// A conversation the listing did not include (older than the page it read) is opened as <paramref name="owner"/>: its
    /// draft saved before drafts were filed by account is that account's when the attribution is theirs, or has not been
    /// made yet. True when a draft was taken over.
    /// </summary>
    public async Task<bool> AdoptAsync(string profile, string id, string owner)
    {
        if (state is null || !Partitioned(id)) return false;
        await claiming.WaitAsync();
        try
        {
            // Before any attribution was made, a conversation opened as the one account signed in is in its list, which is
            // what the attribution asks; the rest of the listing is attributed with the next look at it.
            var undecided = !await MarkerExistsAsync(profile);
            return (undecided || marker?.Owner == owner) && await AdoptCoreAsync(profile, id, owner) == true;
        }
        finally { claiming.Release(); }
    }

    // Under the claiming lock. True when a marker for this profile exists, which ends the looking. Nothing but this class
    // writes the marker (bar a backup restore, which the next run finds), so one found absent stays absent for the run: it is
    // looked for again only when the Messenger account is not the one it was looked for under. Before this the row was read
    // at every load of the list, for ever on a profile that has no Messenger conversation to attribute.
    private async Task<bool> MarkerExistsAsync(string profile)
    {
        if (decidedFor == profile) return true;
        var look = profile + "\n" + Messenger;
        if (absentLook == look) return false;
        marker = await state!.ReadAsync<DraftOwnerMarker>(profile, MarkerCategory, MarkerId);
        if (marker is null) { absentLook = look; return false; }
        decidedFor = profile;
        return true;
    }

    private async Task<bool> DecideAsync(string profile, string owner, string[] ids)
    {
        int migrated = 0, hidden = 0;
        foreach (var id in await state!.ExistingIdsAsync(profile, "draft", ids))
        {
            var moved = await AdoptCoreAsync(profile, id, owner);
            if (moved == true) migrated++;
            else if (moved == false) hidden++;
        }
        await state.WriteAsync(profile, MarkerCategory, MarkerId, marker = new(owner));
        decidedFor = profile;
        log?.Invoke($"drafts migrated owner={migrated} hidden={hidden}");
        return migrated > 0;
    }

    // True when the saved draft was copied to the owner, false when it stays hidden (the owner has written one since), null
    // when it is not a draft or was taken over before (a row with nothing written and nothing replied to is not one).
    private async Task<bool?> AdoptCoreAsync(string profile, string id, string owner)
    {
        var legacy = await state!.ReadAsync<SavedDraft>(profile, "draft", id);
        if (legacy is null || legacy.Text.Length == 0 && legacy.ReplyId is null) return null;
        var taken = "taken:" + id;
        if (await state.ReadAsync<bool?>(profile, MarkerCategory, taken) is not null) return null;
        var filed = FileUnder(profile, id, owner);
        var copied = await state.ReadAsync<SavedDraft>(filed, "draft", id) is null;
        if (copied) await state.WriteAsync(filed, "draft", id, legacy);
        await state.WriteAsync<bool?>(profile, MarkerCategory, taken, true);
        return copied;
    }
}
