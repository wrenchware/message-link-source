using System.Text.Json;
using System.Text.Json.Serialization;

namespace MessageLink.Protocol;

// The shared state (SWITCHBOARD.md stage 6, docs/research/switchboard-stage6.md; protocol version 2): one replicated key–value space per host, holding
// the user's choices that must look the same on every device. The host keeps the one authoritative copy (SharedStateStore, <root>\Server\shared.db);
// each client keeps a replica it draws from and writes into, and sends its writes on behind it (ReplicatedStateStore).
//
//   state.sync  read     What changed since a position {epoch, rev}, a page at a time; from nowhere, another epoch or below the horizon it is a snapshot
//                        (reset). A page holds at most SharedLimits.PageRows rows and SharedLimits.PageBytes of values; a value over
//                        SharedLimits.InlineBytes, or past the page's budget, comes as its size only and is read with state.get.
//   state.get   read     The current rows of some keys, for the values that came as a size only.
//   state.put   write    Writes, each a value or a delete (null), in one commit with one rev. Deduped by clientOpId in the host's own `applied` table, not
//                        the OperationJournal: these writes come every few seconds and would push the sends' entries out, and retrying one is safe (it
//                        is not a provider mutation). So it is not a catalogue mutation, and its params do not derive from MutationParams.
//   event `shared`        Each commit, in the host's event sequence, so a client that resumes is given what it missed; a resync makes it call state.sync.
//
// Keys keep the formats the client's state.db already uses (`<category>:<digest>`), and only the keys SharedCategories names may be stored: any other
// write is answered `refused` and never stored, so a per-device row (cache:, draft-unfiled:, emoji recents) can never leave the client.
// The set of categories grows only with the protocol version (SharedCategory.Since): a host gives a connection only the rows of the categories its
// negotiated version knows (and no `shared` event at all below version 2), and a client keeps only the rows it would share itself, so a row an older
// client does not know (scheduled:, outbox:, draft:, later) can never reach or replace one of its own.
// Values are the rows' own JSON. They are never logged; at rest the host seals them with its IStateProtector.
public static partial class ProtocolMethods
{
    public const string StateSync = "state.sync";
    public const string StateGet = "state.get";
    public const string StatePut = "state.put";

    private static Entry[] SharedStateEntries() =>
    [
        new(StateSync, "ISharedState.SyncAsync", typeof(StateSyncParams), typeof(StateSyncResult), ProtocolVersion.SharedState),
        new(StateGet, "ISharedState.GetAsync", typeof(StateGetParams), typeof(StateGetResult), ProtocolVersion.SharedState),
        new(StatePut, "ISharedState.PutAsync", typeof(StatePutParams), typeof(StatePutResult), ProtocolVersion.SharedState),
    ];
}

/// <summary>Where a replica stands: the space's <paramref name="Epoch"/> (new only when the host's file was made anew) and the last commit it holds whole.</summary>
public sealed record SharedPosition(string Epoch, long Rev);

/// <summary>
/// <paramref name="Since"/> absent asks for a snapshot. <paramref name="Snapshot"/> says the client is still reading a snapshot it began (an earlier page said
/// <c>reset</c> and <c>more</c>): it has no row older than the snapshot, so the horizon does not apply to it, and the pages go on from <paramref name="Since"/>.
/// </summary>
public sealed record StateSyncParams(SharedPosition? Since = null, int? Limit = null, bool Snapshot = false);

/// <summary>
/// One row as the host holds it. <paramref name="Value"/> is the row's JSON, absent when the row is deleted (<paramref name="Deleted"/>) or when it came as its
/// <paramref name="Size"/> only (in bytes; 0 for a deleted row), in which case state.get reads it.
/// </summary>
public sealed record SharedRow(string Key, long Rev, JsonElement? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Deleted = false, int Size = 0)
{
    /// <summary>The value did not come with the row: read it with <see cref="ProtocolMethods.StateGet"/>.</summary>
    [JsonIgnore]
    public bool SizeOnly => !Deleted && Value is null;

    /// <summary>
    /// The protocol version that brought the row's category (<see cref="SharedCategory.Since"/>), as the host filed it. The host's alone, never on the wire: its
    /// dispatcher gives a connection only the rows its negotiated version knows.
    /// </summary>
    [JsonIgnore]
    public int Since { get; init; }
}

/// <summary>
/// A page of changes. <paramref name="Rev"/> is the position the client holds once it has applied the page: every commit up to it is whole in what it has
/// read (a page never splits a commit). <paramref name="Reset"/>: the rows are a snapshot of the live rows, and the client replaces what it held from this
/// host with them. <paramref name="More"/>: ask again from <paramref name="Rev"/>.
/// </summary>
public sealed record StateSyncResult(string Epoch, long Rev, bool Reset, IReadOnlyList<SharedRow> Rows, bool More);

public sealed record StateGetParams(IReadOnlyList<string> Keys);

/// <summary>The rows asked for that the host holds, deleted ones included; a value past the answer's budget comes as its size only, to ask for again.</summary>
public sealed record StateGetResult(IReadOnlyList<SharedRow> Rows);

/// <summary>
/// One write: <paramref name="Value"/> is the row's new JSON, or absent (or null) to delete it. <paramref name="IfRev"/> makes it conditional: it is written
/// only while the row's rev is that one (0: only while there is no live row); left out, the last writer in the host's order wins.
/// </summary>
public sealed record SharedWrite(string Key, JsonElement? Value = null, long? IfRev = null);

/// <summary>
/// <paramref name="ClientOpId"/> names this batch of writes, made and kept by the client when it queued them: a repeat is answered with the first answer and
/// writes nothing again, and the same id with other writes is refused. <paramref name="Epoch"/> is the space the <see cref="SharedWrite.IfRev"/> values were read in:
/// revs are numbered again in a space made anew (<c>--reset-space</c>), so a rev from another one can match a different row, and when it is not the space's own every write
/// with an <c>IfRev</c> answers <c>conflict</c> and keeps nothing. Left out (an older client), the revs are taken as the space's own; an older host ignores it.
/// </summary>
public sealed record StatePutParams(string ClientOpId, IReadOnlyList<SharedWrite> Writes, string? Epoch = null);

/// <summary>
/// What became of one write. <c>written</c>: in the commit. <c>conflict</c>: its <c>ifRev</c> no longer held, and <c>current</c> is the row now.
/// <c>refused</c>: the host does not keep this key (not a shared category at the caller's version, or not under the profile the host files keys under now), or
/// the value is larger than a row may be (<see cref="SharedLimits.ValueBytes"/>). Nothing was stored for it, and sending it again changes nothing.
/// </summary>
public enum SharedWriteStatus { Written, Conflict, Refused }

/// <summary><paramref name="Rev"/> is the row's rev after the call: the commit's for a written row, the row's own otherwise (0 when there is none).</summary>
public sealed record SharedWriteResult(string Key, SharedWriteStatus Status, long Rev, SharedRow? Current = null);

/// <summary><paramref name="Rev"/> is the commit's rev when anything was written, otherwise the host's latest.</summary>
public sealed record StatePutResult(long Rev, IReadOnlyList<SharedWriteResult> Results);

/// <summary>
/// The payload of a <c>shared</c> event: one commit (<paramref name="Rev"/>) in the space <paramref name="Epoch"/>, with every row it wrote. A value over
/// <see cref="SharedLimits.InlineBytes"/>, or past <see cref="SharedLimits.EventBytes"/> in one event, comes as its size only.
/// </summary>
public sealed record SharedChange(string Epoch, long Rev, IReadOnlyList<SharedRow> Rows);

/// <summary>
/// The shared state as the dispatcher serves it (<c>state.*</c>): the host's store, or a client's proxy to it. <c>protocol</c> is the version the caller speaks:
/// the host reads and writes only the categories that version knows. The dispatcher passes its connection's negotiated version; a proxy ignores it (the host
/// knows its connection's).
/// </summary>
public interface ISharedState
{
    Task<StateSyncResult> SyncAsync(StateSyncParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default);
    Task<StateGetResult> GetAsync(StateGetParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default);
    Task<StatePutResult> PutAsync(StatePutParams request, int protocol = ProtocolVersion.Current, CancellationToken cancellationToken = default);
}

/// <summary>The sizes the host holds the shared state to. Each keeps a frame well under the transports' 4 MiB.</summary>
public static class SharedLimits
{
    /// <summary>A value larger than this is refused. The largest shared row now, the quick replies, is at most about 400 KB.</summary>
    public const int ValueBytes = 1024 * 1024;
    /// <summary>A value larger than this travels by its size and is read with state.get.</summary>
    public const int InlineBytes = 64 * 1024;
    public const int PageRows = 500;
    public const int PageBytes = 2 * 1024 * 1024;
    /// <summary>The inline values one <c>shared</c> event carries; the rest of its rows come by size.</summary>
    public const int EventBytes = 1024 * 1024;
    /// <summary>Writes in one state.put. Below <see cref="PageRows"/>, so one commit always fits in a page of state.sync.</summary>
    public const int PutWrites = 64;
    public const int GetKeys = 256;
    public const int KeyLength = 256;
}

/// <summary>How the rows of a shared category settle two writes (docs/research/switchboard-stage6.md, "Conflict rule per kind").</summary>
public enum SharedConflictPolicy
{
    /// <summary>The later in the host's commit order wins, whole row.</summary>
    LastWriterWins,
    /// <summary>
    /// Each write is sent with the rev its value was made from (<c>ifRev</c>). One that loses (<c>conflict</c>) is made again by its writer from the host's row
    /// and what that writer changed since, and put again (at most <c>ReplicatedStateStore.ReapplyLimit</c> times). For a row kept whole whose parts depend on
    /// each other (a chat is in at most one merge), where the later writer winning would undo another device's change.
    /// </summary>
    CompareAndSet,
}

/// <summary>
/// One kind of shared row. With a <paramref name="Row"/> it is the one row <c>PrivateState</c> files as <c>&lt;category&gt;:H(profile, row)</c>, which shares its
/// category with rows that stay on each device (the quick replies beside the emoji recents), so it is told apart by recomputing its digest under the profile.
/// Without one, every key in the category would be shared. <paramref name="Since"/> is the protocol version that brought it: a category is added only together
/// with a new <see cref="ProtocolVersion"/>, so a peer of an older version is never handed a row it would not keep to itself.
/// </summary>
/// <param name="HostOwned">
/// Only the host writes these rows (the scheduled sends, slice 11): a <c>state.put</c> for one is answered <c>refused</c> whatever its value, and a client's
/// replica never queues a write for one. A client reads them like any other row and changes them only through the typed method that owns them
/// (<c>scheduled.*</c>), which is the host's to carry out. This is what keeps a replicated row from being a way to make the host act.
/// </param>
public sealed record SharedCategory(string Category, string? Row, SharedConflictPolicy Policy, int Since, bool HostOwned = false);

/// <summary>
/// The allowlist of shared keys (SWITCHBOARD.md stage 6: slice 1 the quick replies, slice 3 the chat preferences, names, snoozes and pinned order, slice 5 the
/// merges and labels, slice 6 the drafts and who owns them, slice 7 the read-here marks and the private reading choice, slice 8 the saved messages and the custom avatars). The host refuses every other key and a
/// client keeps every other key to itself, so anything not named here stays on the device that wrote it. Adding a category means raising
/// <see cref="ProtocolVersion.Current"/> and giving the category that version (a harness check holds every category's version to at most the current one, and
/// the newest to the last version that brought categories, <see cref="ProtocolVersion.SharedSaved"/>; version 4 brought none).
/// </summary>
public static class SharedCategories
{
    /// <summary>One chat preference: <c>chatpref:&lt;H(profile, chat)&gt;.&lt;setting&gt;</c> (<see cref="SharedRows.ChatPreferenceKey"/>).</summary>
    public const string ChatPreference = "chatpref";
    /// <summary>A name kept for a person or a group: <c>name:&lt;H(profile, identity)&gt;</c>.</summary>
    public const string ContactName = "name";
    /// <summary>A snooze: <c>snooze:&lt;H(profile, chat)&gt;</c>, whose value names its conversation (the reminder fires with nothing else to go on).</summary>
    public const string Snooze = "snooze";
    /// <summary>The merges: <c>merges:&lt;H(profile, "groups")&gt;</c>, one <c>MergedConversation[]</c> (MergedConversations).</summary>
    public const string Merges = "merges";
    public const string MergesRow = "groups";
    /// <summary>The labels: <c>labels:&lt;H(profile, "book")&gt;</c>, one <c>LabelBook</c> (LabelStore).</summary>
    public const string Labels = "labels";
    public const string LabelsRow = "book";
    /// <summary>The protected state's category the rows below share with the per-device ones (the emoji recents, the pinned order).</summary>
    public const string Preferences = "preferences";
    /// <summary>
    /// The read-here marks (slice 7): <c>preferences:&lt;H(profile, "read-here")&gt;</c>, one <c>chat id → moment</c> object (ReadHereMarks), at most 2,000 entries.
    /// </summary>
    public const string ReadHereRow = "read-here";
    /// <summary>
    /// The private reading choice, one row a service (slice 7): <c>preferences:&lt;H(profile, "private-reading.&lt;word&gt;")&gt;</c> holding a JSON boolean, <c>true</c> for
    /// private. The words are <c>PrivateReading.Word</c>'s; the harness holds the two together, since this library cannot reference it. No row means no device has
    /// chosen, which every client reads as private (SharedPrivateReading).
    /// </summary>
    public const string PrivateReadingRowPrefix = "private-reading.";
    public static readonly IReadOnlyList<string> PrivateReadingWords = ["google-messages", "messenger", "instagram", "google-voice"];
    /// <summary>A chat's draft: <c>draft:&lt;H(profile, chat)&gt;</c>, the text and the reply it answers (ComposerViewModel). A Messenger chat's is filed under the profile and the account that wrote it.</summary>
    public const string Draft = "draft";
    /// <summary>The rows noting who owns what (DraftOwners): <c>draft-owner:&lt;H(profile, id)&gt;</c>, the attribution marker, the conversations taken over and the accounts seen.</summary>
    public const string DraftOwner = "draft-owner";
    /// <summary>
    /// A scheduled send (slice 11): <c>scheduled:&lt;H(profile, id)&gt;</c>, one <c>ScheduledSend</c> a row, written by the host alone (<see cref="SharedCategory.HostOwned"/>).
    /// The in-process schedule's one row, <c>scheduled:&lt;H(profile, "all")&gt;</c> (a whole list, kept by the PC that fires it itself), is no part of it: <see cref="Match"/>
    /// leaves it out, so a replica never sends it, never drops it for a snapshot and never takes a host's row over it.
    /// </summary>
    public const string Scheduled = "scheduled";
    public const string ScheduledLegacyRow = "all";
    /// <summary>
    /// A send of unknown outcome or failed on the server (slice 11): <c>outbox:&lt;H(profile, operation id)&gt;</c>, one <c>SharedUnsettledSend</c> a row, written by the host alone and removed by
    /// its <c>outbox.dismiss</c>. The in-process app's own record, <c>outbox:&lt;H(profile, "unsettled")&gt;</c> (a whole list), shares the prefix and is no part of it (<see cref="Match"/>).
    /// </summary>
    public const string Outbox = "outbox";
    public const string OutboxLegacyRow = "unsettled";
    /// <summary>
    /// A saved message (slice 8): <c>saved:&lt;sha(account scope)&gt;:&lt;sha(["chat","message"])&gt;</c>, one <c>SavedMessage</c> with its reminder time (SavedMessages). Filed by account
    /// scope, not by profile, so it needs no profile to be told from the rows beside it.
    /// </summary>
    public const string Saved = "saved";
    /// <summary>A custom avatar (slice 8): <c>avatar-v2:&lt;H(profile, "chat:" + id | "contact:" + network + ":" + person)&gt;</c>, a 256 px picture or the choice to use the provider's (LocalAvatarRows).</summary>
    public const string Avatar = "avatar-v2";
    /// <summary>A repeating scheduled send (protocol 31): <c>repeat:&lt;H(profile, id)&gt;</c>, one <c>RepeatingSend</c> a row, written by the host alone (HostRepeats). Hermes threads only.</summary>
    public const string Repeat = "repeat";

    public static IReadOnlyList<SharedCategory> All { get; } =
    [
        // QuickReplyBook: Category "preferences", RowId "quick-replies"; read when shown, never cached.
        new("preferences", "quick-replies", SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedState),
        // ChatListViewModel's pinned order, a state.db row beside the per-device emoji rows.
        new("preferences", "pinned-order", SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedPreferences),
        // The three stores that were files on each PC (LocalChatPreferenceStore, LocalContactNameStore) or on the host (the ChatSnoozes-v1 stores). Every key in
        // them is shared: the profile is inside the digest, which neither side can take apart, and a row under another profile is simply never asked for.
        // Each setting of a chat is its own key, so last writer wins per field.
        new(ChatPreference, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedPreferences),
        new(ContactName, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedPreferences),
        new(Snooze, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedPreferences),
        // Each one row kept whole, because its parts depend on each other: a chat is in at most one merge, across groups, and an assignment names a label of
        // the same book. A write that lost to another device's is therefore made again from the host's row, never written over it.
        new(Merges, MergesRow, SharedConflictPolicy.CompareAndSet, ProtocolVersion.SharedMerges),
        new(Labels, LabelsRow, SharedConflictPolicy.CompareAndSet, ProtocolVersion.SharedMerges),
        // Slice 7. The marks are one row whose entries are independent facts ("read here through this moment"), merged by the later moment per chat; compare-
        // and-set so that a write made from an older row is made again over the host's (ReadHereMarks), never over it. Each service's private reading is its own
        // row, last writer wins: one row for all four would lose a toggle of another service made at the same time, and a toggle is whole by itself.
        new(Preferences, ReadHereRow, SharedConflictPolicy.CompareAndSet, ProtocolVersion.SharedReading),
        .. PrivateReadingWords.Select(word => new SharedCategory(Preferences, PrivateReadingRowPrefix + word, SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedReading)),
        // Every chat's draft is its own row, and the person typing now wins (the composer's editing guard decides what a remote draft may replace; nothing
        // here is compared). The words kept for no account yet ("draft-unfiled:") name this device's composer and stay on it: no category, so no key of
        // theirs is ever sent, whatever digest it has.
        new(Draft, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedDrafts),
        new(DraftOwner, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedDrafts),
        // The server's scheduled sends. Every row is the host's own write and the policy is never consulted (a client cannot put one); last writer is the host's.
        new(Scheduled, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.HostScheduled, HostOwned: true),
        // A send of unknown outcome, or failed, on the server (slice 11): one row, written and removed by the host alone; every client draws it as "may not have sent".
        new(Outbox, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.HostScheduled, HostOwned: true),
        // Slice 8. A saved message is one row of its own. Saving it is a create-only write (ifRev 0: "already saved" counts as done, as it does on one device) made
        // by SavedMessages through ISharedRows.WriteIfAsync, and unsaving deletes; the reminder time and the moment it was raised are written without a condition,
        // last writer wins, except that the mark "raised" is conditional on the rev it was made from, so a stale mark never undoes a time set meanwhile. The
        // avatars are last writer wins; one over 64 KiB comes by its size and is read with state.get, which the replica does for any row.
        new(Saved, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedSaved),
        new(Avatar, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.SharedSaved),
        // Protocol 31. The server's repeats, written by it alone like the scheduled sends; a client changes them only through repeats.create and repeats.cancel.
        new(Repeat, null, SharedConflictPolicy.LastWriterWins, ProtocolVersion.Hermes, HostOwned: true),
    ];

    /// <summary>
    /// Whether every key under the category's prefix is shared (it has no fixed row), which a key alone then tells without the profile: the drafts, the saved
    /// messages and the avatars. The chat preferences, names and snoozes are shared the same way, but their stores are not rows a follower or a composer
    /// holds in memory, so only a store that does asks (ISharedRows.Shares).
    /// </summary>
    public static bool SharesEveryKey(SharedCategory category) =>
        category.Row is null && category.Category is Draft or DraftOwner or Saved or Avatar;

    /// <summary>The row of one service's private reading choice (<see cref="PrivateReadingRowPrefix"/>), as <c>PrivateState</c> files it.</summary>
    public static string PrivateReadingKey(string profile, string word) => KeyOf(profile, Preferences, PrivateReadingRowPrefix + word);

    /// <summary>The read-here marks' row, as <c>PrivateState</c> files it.</summary>
    public static string ReadHereKey(string profile) => KeyOf(profile, Preferences, ReadHereRow);

    /// <summary>
    /// The category <paramref name="key"/> belongs to, or null when it is not shared by a peer of <paramref name="protocol"/>. <paramref name="profile"/> is the
    /// profile the keys are filed under (<c>NotificationIdentity.ProfileOf</c> of the local key); without one no single row is shared.
    /// </summary>
    public static SharedCategory? Match(string key, string? profile, int protocol = ProtocolVersion.Current)
    {
        foreach (var category in All)
        {
            if (category.Since > protocol || !key.StartsWith(category.Category + ":", StringComparison.Ordinal)) continue;
            // The in-process schedule's whole-list row shares the prefix and is not a scheduled send's row (see Scheduled).
            if (profile is not null && (category.Category == Scheduled && string.Equals(key, KeyOf(profile, Scheduled, ScheduledLegacyRow), StringComparison.Ordinal)
                || category.Category == Outbox && string.Equals(key, KeyOf(profile, Outbox, OutboxLegacyRow), StringComparison.Ordinal))) continue;
            if (category.Row is null) return category;
            if (profile is not null && string.Equals(key, KeyOf(profile, category.Category, category.Row), StringComparison.Ordinal)) return category;
        }
        return null;
    }

    /// <summary>
    /// The version that brought the category <paramref name="key"/> would be shared under at the newest version, or null when it is not shared at all. A client
    /// holds a write of a newer category than its host speaks rather than sending it to be refused (and then dropped as a key the host does not keep).
    /// </summary>
    public static int? SinceOf(string key, string? profile) => Match(key, profile)?.Since;

    /// <summary>Whether a key's category holds any shared row at all: a cheap test that needs no profile, for the many keys that are never shared.</summary>
    public static bool MayBeShared(string key)
    {
        foreach (var category in All)
            if (key.StartsWith(category.Category + ":", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// The key <c>PrivateState</c> files a row under: the category, a colon, and the SHA-256 (upper-case hex) of the profile, a line break and the row's id.
    /// The same as <c>PrivateState.KeyOf</c>, which this library cannot reference; the harness holds the two together.
    /// </summary>
    public static string KeyOf(string profile, string category, string id) =>
        category + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile + "\n" + id)));
}
