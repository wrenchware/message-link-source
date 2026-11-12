using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.Backup;

/// <summary>Where the local stores a backup reads and restores live. The app passes its own; the harness passes
/// synthetic ones in a temporary folder.</summary>
internal sealed record LocalStores(IPrivateStateStore State, IStateProtector Protector, string ChatPreferences, string ContactNames,
    Func<JsonObject?> ReadAppPreferences, Func<JsonObject, (int Added, int Skipped)> RestoreAppPreferences);

/// <summary>One row of the protected store: its own key, and the id it was stored under when this PC could name it. A Messenger
/// draft is filed under the account that wrote it (DraftOwners): its row names that account's scope (a one-way value, never an
/// id) and the conversation, and leaves Id empty, so a restore that does not know owners can only refuse it, never file it
/// under no account.</summary>
internal sealed record StateEntry(string Key, string? Id, byte[] Value, string? Owner = null, string? OwnerId = null);
/// <summary>One pin, mute or unread mark file, with the conversation it is for when this PC could name it.</summary>
internal sealed record ChatPreferenceEntry(string File, string? ChatId, byte[] Value);
/// <summary>One name given to a person here, unsealed, with the identity it is for when this PC could name it.</summary>
internal sealed record ContactNameEntry(string File, string? Identity, string Name);

/// <summary>
/// Everything local a backup carries. Profile is a one-way tag of the profile the rows are keyed by, so a restore can
/// tell whether their keys are already right here; it lives inside the encrypted payload only.
/// </summary>
internal sealed record LocalStateSnapshot(string? Profile, StateEntry[] State, ChatPreferenceEntry[] ChatPreferences,
    ContactNameEntry[] ContactNames, JsonObject? AppPreferences, int Unreadable);

/// <summary>What a restore did. Held counts drafts, labels, names and Saved Messages left out because this PC already
/// keeps some of its own of that kind. SameProfile says the backup's keys were used as they were; Rekeyed counts rows placed
/// under this PC's profile instead, from the id the backup named them by.</summary>
internal sealed record LocalRestoreResult(int Added, int Skipped, int Refused, int Held, int Rekeyed, bool SameProfile);

/// <summary>
/// Reads the user's own local choices out of this PC's stores for a backup, and adds them back without replacing
/// anything already here.
/// </summary>
/// <remarks>
/// Every local store is keyed by a digest of the signed-in profile and the conversation or person, so a row names
/// neither. When the profile here is the one the backup was made under, the keys are used as they are. Otherwise a
/// row can move only if the backup could name what it was for, which it learns by matching the conversations loaded
/// when it ran; a row it could not name stays out rather than landing somewhere wrong.
/// </remarks>
internal static class LocalStateBackup
{
    // The protected store's categories, as of this writing, and what becomes of each:
    //   draft, preferences, merges, labels, avatar-v2, avatar, saved   carried (the allowlist below)
    //   cache                rebuilt from the providers, so left out
    //   outbox               sends whose outcome is unknown (InterruptedSends): never replayed elsewhere
    //   incoming-unarchive   unarchive attempts already made (IncomingMessageUnarchiver): the same
    //   scheduled            Send later's messages (ScheduledSends): sends waiting on this PC, never replayed elsewhere
    // A saved message's reminder time travels with it (saved); one restored after its time is raised once, late.
    // This is an allowlist. A category not named here is never exported and never restored, so a new category that
    // holds the user's own choices has to be added here to be backed up, with any fixed ids it is stored under.
    // Rows stored per conversation or person need no ids listed: they are matched against the loaded conversations.
    internal static readonly IReadOnlyDictionary<string, string[]> Categories = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["draft"] = [],
        // Who a Messenger draft belongs to (DraftOwners): the attribution marker, the conversations taken over from the drafts
        // saved before accounts owned them ("taken:" + the conversation, named below), and the accounts seen, so the drafts
        // filed under each can be named. They travel with the drafts and are held back with them. Words kept for no account
        // yet (draft-unfiled) are transient, filed at the next look at the account, and never carried.
        [DraftOwners.OwnerCategory] = ["marker", DraftOwners.KnownScopesId],
        // quick-replies: the composer's saved replies (QuickReplies), kept under preferences like the emoji choices.
        ["preferences"] = ["pinned-order", "emoji-recents", "emoji-tone", "quick-replies"],
        ["merges"] = ["groups"],
        ["labels"] = ["book"],
        // Which conversations a server has put the Marketplace label on, and which label it made (HostMarketplaceLabels), so a restored server does not
        // put it back on conversations it was taken off, or make it again after it was deleted. Only a server's own state holds it.
        ["auto-labels"] = ["marketplace"],
        // Custom photos for a conversation or person (LocalAvatarStore), and older releases' photos it moves on first read.
        ["avatar-v2"] = [],
        ["avatar"] = [],
        // Saved Messages: keyed by the account's scope rather than the profile, so their keys hold anywhere.
        [Saved] = [],
    };
    internal static readonly string[] Excluded = ["cache", "outbox", "incoming-unarchive"];
    private const string Saved = "saved", Draft = "draft", Merges = "merges", MergesId = "groups";
    internal const int MaxPreferenceFile = 1024;
    private static readonly Regex HashPattern = new("^[0-9A-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex SavedKey = new("^saved:[0-9A-F]{64}:[0-9A-F]{64}$", RegexOptions.CultureInvariant);
    internal static readonly Regex PreferenceFile = new(@"^([0-9A-F]{64})\.(Pinned|Muted|MarkedUnread|Archived)\.json$", RegexOptions.CultureInvariant);
    internal static readonly Regex NameFile = new(@"^([0-9A-F]{64})\.name$", RegexOptions.CultureInvariant);

    public static string ProfileTag(string profile) => NotificationIdentity.Hash("messages-backup\n" + profile);

    /// <summary>Every id a local row could be stored under for these conversations: the conversation, its members,
    /// and the identities custom photos and names use for it and its people.</summary>
    public static IEnumerable<string> CandidateIds(IEnumerable<ChatSummary> chats)
    {
        foreach (var chat in chats)
        {
            yield return chat.Id;
            yield return "chat:" + chat.Id;
            yield return AvatarOverrideIdentity.ForChat(chat);
            foreach (var member in chat.MemberChatIds) yield return member;
            if (chat.DefaultMemberChatId is { } member1) yield return member1;
            if (chat.MergedIntoChatId is { } parent) yield return parent;
            foreach (var person in chat.Participants)
                if (!string.IsNullOrWhiteSpace(person.Id)) yield return AvatarOverrideIdentity.ForParticipant(chat, person.Id);
        }
    }

    public static async Task<LocalStateSnapshot> CollectAsync(LocalStores stores, string? profile, IEnumerable<string> candidates,
        CancellationToken token = default)
    {
        // Each store keys a row by Hash(profile + "\n" + id) behind its own prefix, so one index serves them all.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var unreadable = 0;
        // A Messenger draft is filed under the account that wrote it, so its digest names the account's scope as well as the
        // conversation: tried for every account this PC has seen sign in (a conversation id alone names none of them).
        var owned = new Dictionary<string, (string Owner, string Id)>(StringComparer.Ordinal);
        if (profile is not null)
        {
            var conversations = candidates.ToArray();
            // "taken:" + the conversation is how DraftOwners notes a draft saved before accounts owned them was taken over.
            foreach (var id in conversations.Concat(conversations.Where(DraftOwners.Partitioned).Select(id => "taken:" + id)).Concat(Categories.Values.SelectMany(ids => ids)))
                names.TryAdd(NotificationIdentity.Hash(profile + "\n" + id), id);
            string[] owners;
            try { owners = await OwnerScopesAsync(stores.State, profile, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { unreadable++; owners = []; }
            foreach (var owner in owners)
                foreach (var id in conversations.Where(DraftOwners.Partitioned).Distinct(StringComparer.Ordinal))
                    owned.TryAdd(NotificationIdentity.Hash(DraftOwners.FileUnder(profile, id, owner) + "\n" + id), (owner, id));
        }
        string? Name(string digest) => names.GetValueOrDefault(digest);

        var state = new List<StateEntry>();
        foreach (var category in Categories.Keys)
            foreach (var key in await stores.State.KeysAsync(category + ":", token))
            {
                if (!IsStateKey(category, key)) continue;
                byte[]? value;
                try { value = await stores.State.ReadAsync(key, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { unreadable++; continue; }
                if (value is null) continue;
                var digest = category == Saved ? null : key[(category.Length + 1)..];
                if (category == Draft && digest is not null && owned.TryGetValue(digest, out var mine)) state.Add(new(key, null, value, mine.Owner, mine.Id));
                else state.Add(new(key, category == Saved ? null : Name(digest!), value));
            }

        var preferences = new List<ChatPreferenceEntry>();
        foreach (var path in Files(stores.ChatPreferences))
        {
            if (PreferenceFile.Match(Path.GetFileName(path)) is not { Success: true } match) continue;
            try
            {
                if (new FileInfo(path).Length > MaxPreferenceFile) { unreadable++; continue; }
                var bytes = await File.ReadAllBytesAsync(path, token);
                if (IsPreference(bytes)) preferences.Add(new(Path.GetFileName(path), Name(match.Groups[1].Value), bytes));
                else unreadable++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { unreadable++; }
        }

        var contactNames = new List<ContactNameEntry>();
        foreach (var path in Files(stores.ContactNames))
        {
            if (NameFile.Match(Path.GetFileName(path)) is not { Success: true } match) continue;
            byte[]? plain = null;
            try
            {
                plain = await stores.Protector.UnprotectAsync(await File.ReadAllBytesAsync(path, token));
                var name = Encoding.UTF8.GetString(plain);
                if (IsName(name)) contactNames.Add(new(Path.GetFileName(path), Name(match.Groups[1].Value), name));
                else unreadable++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { unreadable++; }
            finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
        }

        JsonObject? app = null;
        try { app = stores.ReadAppPreferences() is { } read ? Preferences(read) : null; }
        catch (Exception) { unreadable++; }
        return new(profile is null ? null : ProfileTag(profile), [.. state], [.. preferences], [.. contactNames], app, unreadable);
    }

    // The Messenger accounts this PC has seen sign in (DraftOwners notes each), and the one the first drafts were attributed to.
    private static async Task<string[]> OwnerScopesAsync(IPrivateStateStore store, string profile, CancellationToken token)
    {
        string Key(string id) => DraftOwners.OwnerCategory + ":" + NotificationIdentity.Hash(profile + "\n" + id);
        var scopes = new List<string>();
        if (await store.ReadAsync(Key(DraftOwners.KnownScopesId), token) is { } seen && JsonSerializer.Deserialize<List<string>>(seen) is { } list) scopes.AddRange(list);
        if (await store.ReadAsync(Key("marker"), token) is { } marker && JsonNode.Parse(marker) is JsonObject { } node && node["Owner"]?.GetValue<string>() is { } owner) scopes.Add(owner);
        return [.. scopes.Where(IsOwnerScope).Distinct(StringComparer.Ordinal)];
    }

    // An account's scope as the host names it: "messenger:" and a one-way value. Whatever else is not filed under any account.
    private static bool IsOwnerScope(string? scope) => scope is { Length: > 10 and <= 256 } && scope.StartsWith("messenger:", StringComparison.Ordinal) && scope.All(c => c > ' ' && c != '\n');

    public static async Task<LocalRestoreResult> RestoreAsync(LocalStores stores, LocalStateSnapshot backup, string? profile,
        CancellationToken token = default)
    {
        var same = profile is not null && backup.Profile is not null && backup.Profile == ProfileTag(profile);
        int added = 0, skipped = 0, refused = 0, held = 0, rekeyed = 0;
        // Drafts, names and Saved Messages are deleted outright when the user sends, clears or unsaves them, so on a PC
        // that keeps some of its own a missing one may be one the user took away since the backup, and adding it back
        // would undo that. They are restored only to a PC that has none of that kind yet, such as a new one. Labels are
        // held the same way (RestoreLabelsAsync).
        var draftsHere = (await stores.State.KeysAsync(Draft + ":", token)).Count > 0;
        var savedHere = (await stores.State.KeysAsync(Saved + ":", token)).Count > 0;
        var namesHere = Files(stores.ContactNames).Any(path => NameFile.IsMatch(Path.GetFileName(path)));
        void Count(bool? stored, bool moved)
        {
            if (stored is null) refused++;
            else if (stored.Value) { added++; if (moved) rekeyed++; }
            else skipped++;
        }

        // The row's key here: as it was when the profile is the same or the row is not keyed by a profile, and from
        // the id it was stored under otherwise. Null when there is nowhere right to put it.
        // A draft filed under an account (owner) goes under the same account on this PC, from its conversation.
        string? Place(string digest, string? id, string? owner = null, string? ownerId = null)
        {
            if (profile is null) return null;
            if (owner is not null || ownerId is not null)
            {
                if (!IsOwnerScope(owner) || ownerId is null || !DraftOwners.Partitioned(ownerId)) return null;
                return same ? digest : NotificationIdentity.Hash(DraftOwners.FileUnder(profile, ownerId, owner!) + "\n" + ownerId);
            }
            if (same) return digest;
            return id is null ? null : NotificationIdentity.Hash(profile + "\n" + id);
        }

        // Labels last, so the merges row they are matched against is this PC's as the restore leaves it.
        foreach (var entry in (backup.State ?? []).OrderBy(entry => entry.Key?.StartsWith(ChatLabels.Category + ":", StringComparison.Ordinal) == true))
        {
            token.ThrowIfCancellationRequested();
            var category = entry.Key.Split(':')[0];
            if (!Categories.ContainsKey(category) || !IsStateKey(category, entry.Key) || entry.Value is null) { refused++; continue; }
            if ((category == Draft || category == DraftOwners.OwnerCategory) && draftsHere || category == Saved && savedHere) { held++; continue; }
            // Only a draft is filed under an account; the owner named on any other row is not a reason to place it.
            var owner = category == Draft ? entry.Owner : null; var ownerId = category == Draft ? entry.OwnerId : null;
            var target = category == Saved ? entry.Key : Place(entry.Key[(category.Length + 1)..], entry.Id, owner, ownerId) is { } digest ? category + ":" + digest : null;
            if (target is null) { refused++; continue; }
            bool? stored;
            var holds = false;
            try
            {
                if (category == ChatLabels.Category) (stored, holds) = await RestoreLabelsAsync(stores.State, backup, target, entry.Value, profile!, token);
                else stored = await stores.State.AddAsync(target, entry.Value, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { stored = null; }
            if (holds) held++;
            else Count(stored, target != entry.Key);
        }

        foreach (var entry in backup.ChatPreferences ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (entry.File is null || entry.Value is null || PreferenceFile.Match(entry.File) is not { Success: true } match ||
                entry.Value.Length > MaxPreferenceFile || !IsPreference(entry.Value) || Place(match.Groups[1].Value, entry.ChatId) is not { } digest)
            { refused++; continue; }
            var file = digest + "." + match.Groups[2].Value + ".json";
            Count(await AddFileAsync(stores.ChatPreferences, file, entry.Value, token), file != entry.File);
        }

        foreach (var entry in backup.ContactNames ?? [])
        {
            token.ThrowIfCancellationRequested();
            if (entry.File is null || !IsName(entry.Name) || NameFile.Match(entry.File) is not { Success: true } match) { refused++; continue; }
            if (namesHere) { held++; continue; }
            if (Place(match.Groups[1].Value, entry.Identity) is not { } digest) { refused++; continue; }
            var file = digest + ".name";
            var plain = Encoding.UTF8.GetBytes(entry.Name);
            bool? stored;
            try { stored = await AddFileAsync(stores.ContactNames, file, await stores.Protector.ProtectAsync(plain), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { stored = null; }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Count(stored, file != entry.File);
        }

        if (backup.AppPreferences is { } app && Preferences(app) is { Count: > 0 } offered)
        {
            try
            {
                var (appAdded, appSkipped) = stores.RestoreAppPreferences(offered);
                added += appAdded; skipped += appSkipped;
            }
            catch (Exception) { refused += offered.Count; }
        }
        return new(added, skipped, refused, held, rekeyed, same);
    }

    /// <summary>A preference this PC has set is kept; one it has never set, or left unset, is taken from the backup. A
    /// map is filled a name at a time, because each of its names is a choice of its own: a PC that chose only
    /// Messenger's bubble colour still takes the backup's colour for SMS.</summary>
    public static (JsonObject Merged, int Added, int Skipped) MergeMissing(JsonObject? local, JsonObject backup)
    {
        var merged = local?.DeepClone().AsObject() ?? new JsonObject();
        int added = 0, skipped = 0;
        foreach (var (name, value) in Preferences(backup))
        {
            merged.TryGetPropertyValue(name, out var existing);
            if (existing is JsonObject here && value is JsonObject offered)
            {
                var filled = false;
                foreach (var (key, item) in offered)
                {
                    if (here.TryGetPropertyValue(key, out var held) && held is not null) continue;
                    here[key] = item!.DeepClone();
                    filled = true;
                }
                if (filled) added++; else skipped++;
                continue;
            }
            if (existing is not null) { skipped++; continue; }
            merged[name] = value!.DeepClone();
            added++;
        }
        return (merged, added, skipped);
    }

    /// <summary>How many of each kind a snapshot holds, for the manifest.</summary>
    public static Dictionary<string, int> Counts(LocalStateSnapshot snapshot)
    {
        var counts = Categories.Keys.ToDictionary(category => category, _ => 0, StringComparer.Ordinal);
        foreach (var entry in snapshot.State) counts[entry.Key.Split(':')[0]]++;
        counts["chat-preferences"] = snapshot.ChatPreferences.Length;
        counts["contact-names"] = snapshot.ContactNames.Length;
        counts["app-preferences"] = snapshot.AppPreferences?.Count ?? 0;
        return counts;
    }

    // The label book is one row, so it is added whole. A book with no labels in it is not this PC's own — making a label
    // and deleting it leaves one — and gives way to the backup's. One with labels holds the backup's back (Held), as
    // drafts are held, because a label missing here may be one the user deleted since the backup. An unreadable one is
    // never written over: the read throws, and the row is counted as refused.
    private static async Task<(bool? Stored, bool Held)> RestoreLabelsAsync(IPrivateStateStore store, LocalStateSnapshot backup, string key,
        byte[] book, string profile, CancellationToken token)
    {
        if (LabelCount(book) is not { } offered) return (null, false);
        var placed = book;
        try
        {
            var mergesHere = await store.ReadAsync(Merges + ":" + NotificationIdentity.Hash(profile + "\n" + MergesId), token);
            var theirs = backup.State.FirstOrDefault(row => row.Id == MergesId && row.Key?.StartsWith(Merges + ":", StringComparison.Ordinal) == true);
            placed = Rehome(book, MergeGroups(theirs?.Value), MergeGroups(mergesHere).Keys.ToHashSet(StringComparer.Ordinal));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { /* This PC's merges can't be read, so which merged rows it lacks is unknown: the book goes as it is. */ }
        var here = await store.ReadAsync(key, token);
        if (here is null) return (await store.AddAsync(key, placed, token), false);
        if (offered == 0 || here.AsSpan().SequenceEqual(book) || here.AsSpan().SequenceEqual(placed)) return (false, false);
        if (LabelCount(here) is not 0) return (false, true);
        await store.WriteAsync(key, placed, token);
        return (true, false);
    }

    // How many labels a saved book holds; null when it is not a book at all.
    private static int? LabelCount(byte[] book)
    {
        try { return JsonNode.Parse(book) is JsonObject saved ? (saved["Labels"] as JsonArray)?.Count ?? 0 : null; }
        catch (Exception) { return null; }
    }

    // The merged rows a saved merges row holds, each with the ids of the conversations in it. Empty when there is none.
    private static Dictionary<string, string[]> MergeGroups(byte[]? row)
    {
        var groups = new Dictionary<string, string[]>(StringComparer.Ordinal);
        try
        {
            if (row is null || JsonNode.Parse(row) is not JsonArray saved) return groups;
            foreach (var group in saved.OfType<JsonObject>())
                if (Text(group["Id"]) is { } id && group["Members"] is JsonArray members)
                    groups[id] = [.. members.OfType<JsonObject>().Select(member => Text(member["Id"])).OfType<string>()];
        }
        catch (Exception) { }
        return groups;
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    // A restore adds merges and labels independently, so the backup's labels can name a merged row this PC does not
    // have — its own merges were kept instead. Those labels go to the conversations the backup had merged there; once
    // the list reads them again they show on those conversations, or move to the row this PC merged them into
    // (ChatListViewModel.ReloadRestoredAsync). Otherwise they would be kept where nothing ever shows them.
    private static byte[] Rehome(byte[] book, IReadOnlyDictionary<string, string[]> theirs, IReadOnlySet<string> here)
    {
        if (theirs.Count == 0) return book;
        try
        {
            if (JsonNode.Parse(book) is not JsonObject parsed || parsed["Assignments"] is not JsonObject assignments) return book;
            var gone = assignments.Select(pair => pair.Key).Where(id => !here.Contains(id) && theirs.ContainsKey(id)).ToArray();
            if (gone.Length == 0) return book;
            foreach (var merged in gone)
            {
                var ids = assignments[merged] is JsonArray labels ? labels.Select(Text).OfType<string>().ToArray() : [];
                assignments.Remove(merged);
                foreach (var member in theirs[merged])
                {
                    var kept = assignments[member] is JsonArray existing ? existing.Select(Text).OfType<string>() : [];
                    assignments[member] = new JsonArray([.. kept.Concat(ids).Distinct(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id))]);
                }
            }
            return Encoding.UTF8.GetBytes(parsed.ToJsonString());
        }
        catch (Exception) { return book; }
    }

    private static bool IsStateKey(string category, string key) =>
        category == Saved ? SavedKey.IsMatch(key)
        : key.Length == category.Length + 65 && key.StartsWith(category + ":", StringComparison.Ordinal) && HashPattern.IsMatch(key[(category.Length + 1)..]);

    internal static bool IsName(string? name) => name is { Length: > 0 and <= LocalContactNameStore.MaxNameLength } && name.Trim() == name;

    // The only shape LocalChatPreferenceStore writes: a version and a boolean, and for a timed mute or an archive kept on
    // this PC one moment in milliseconds (Until, At). Nothing else.
    internal static bool IsPreference(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                json.RootElement.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.Number && version.GetInt32() == 1 &&
                json.RootElement.TryGetProperty("Value", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                json.RootElement.EnumerateObject().All(field => field.Name is "Version" or "Value" ||
                    field.Name is "At" or "Until" && field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetInt64(out _));
        }
        catch (Exception) { return false; }
    }

    // App preferences are switches, plain values under short names, and small maps of short names to short words, such
    // as each service's bubble colour. Anything else is not something this file holds. What a map's words mean is
    // checked where they are read (BubbleColours.Clean), as for the file itself.
    private const int MaxPreferenceMap = 16, MaxPreferenceWord = 64;
    private static JsonObject Preferences(JsonObject source)
    {
        var kept = new JsonObject();
        foreach (var (name, value) in source)
            if (name.Length is > 0 and <= 64 && (IsPlainPreference(value) || IsPreferenceMap(value)))
                kept[name] = value!.DeepClone();
        return kept;
    }

    private static bool IsPlainPreference(JsonNode? value) =>
        value is JsonValue && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String;

    private static bool IsPreferenceMap(JsonNode? value) =>
        value is JsonObject map && map.Count is > 0 and <= MaxPreferenceMap && map.All(pair => pair.Key.Length is > 0 and <= 64 &&
            pair.Value is JsonValue word && word.GetValueKind() == JsonValueKind.String && word.GetValue<string>().Length <= MaxPreferenceWord);

    private static IEnumerable<string> Files(string directory)
    {
        try { return Directory.Exists(directory) ? Directory.GetFiles(directory) : []; }
        catch (Exception) { return []; }
    }

    // Written beside its final name and moved into place without replacing, so an existing file always wins.
    private static async Task<bool?> AddFileAsync(string directory, string file, byte[] content, CancellationToken token)
    {
        var path = Path.Combine(directory, file);
        if (File.Exists(path)) return false;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(temporary, content, token);
            File.Move(temporary, path, overwrite: false);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (IOException) when (File.Exists(path)) { return false; }
        catch (Exception) { return null; }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { } }
    }
}
