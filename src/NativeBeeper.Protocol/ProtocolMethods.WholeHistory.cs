using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Protocol;

// The server's whole copy of every conversation (SWITCHBOARD.md stage 8, protocol 24). The server keeps each conversation whole from its newest message
// down to as far back as anyone has read, numbers every change it makes to it, and hands a client only what changed after the number the client holds, so
// a client's copy is whole by construction and is drawn at once. A revision is "<epoch>.<number>": the epoch is new whenever the server's store starts
// over, and a revision of another epoch, or older than the conversation's last reset, is answered with a reset (replace your copy, don't patch it).
// Nothing here is journaled: each is a read, and asking again gives the same answer or a newer one.
public static partial class ProtocolMethods
{
    /// <summary>What changed in one conversation after a revision (<see cref="HistorySyncParams"/>), or its newest messages when the client holds nothing of it.</summary>
    public const string HistorySync = "history.sync";
    /// <summary>Messages older than one the client holds (<see cref="HistoryOlderParams"/>), read from the service and kept if the server does not hold them yet.</summary>
    public const string HistoryOlder = "history.older";
    /// <summary>Which conversations changed after a revision (<see cref="HistoryChangesParams"/>): what a phone woken by a push reads first.</summary>
    public const string HistoryChanges = "history.changes";

    /// <summary>
    /// What changed after a revision in the whole store, the list rows and the messages together in one order (<see cref="HistoryFeedParams"/>, protocol 25):
    /// how a client catches up, with no read per conversation.
    /// </summary>
    public const string HistoryFeed = "history.feed";
    /// <summary>A client's copies compared with the server's by their fingerprints (<see cref="HistoryVerifyParams"/>, protocol 25). Reads nothing from a service.</summary>
    public const string HistoryVerify = "history.verify";
    /// <summary>The server's check of its copy against the services: its latest results, and with <c>Start</c> a check started now (protocol 25).</summary>
    public const string HistoryMirror = "history.mirror";

    private static Entry[] WholeHistoryEntries() =>
    [
        new(HistorySync, "IWholeHistory.SyncAsync", typeof(HistorySyncParams), typeof(HistorySyncResult)),
        new(HistoryOlder, "IWholeHistory.OlderAsync", typeof(HistoryOlderParams), typeof(HistoryOlderResult)),
        new(HistoryChanges, "IWholeHistory.ChangesAsync", typeof(HistoryChangesParams), typeof(HistoryChangesResult)),
        new(HistoryFeed, "IWholeHistory.FeedAsync", typeof(HistoryFeedParams), typeof(HistoryFeedResult)),
        new(HistoryVerify, "IWholeHistory.VerifyAsync", typeof(HistoryVerifyParams), typeof(HistoryVerifyResult)),
        new(HistoryMirror, "IWholeHistory.MirrorAsync", typeof(HistoryMirrorParams), typeof(HistoryMirrorResult)),
    ];
}

/// <summary>
/// <paramref name="Since"/> is the revision the client's list and copies reached; none (or one of another epoch) starts it over. <paramref name="Page"/> carries
/// on a start-over that did not fit in one answer (<see cref="HistoryFeedResult.Next"/>). At most <paramref name="Limit"/> items (500 when left out).
/// </summary>
public sealed record HistoryFeedParams(string? Since = null, string? Page = null, int? Limit = null);

/// <summary>
/// <paramref name="Reset"/> (the first answer of a start-over): the client's list is replaced by the rows that follow, through every <paramref name="Next"/>, and
/// every copy it holds is read again with <see cref="ProtocolMethods.HistorySync"/> when next opened. Otherwise <paramref name="Rows"/> replace those rows,
/// <paramref name="RemovedRows"/> leave the list (and their copies go), and each of <paramref name="Changes"/> patches a copy whose revision has reached its
/// <see cref="HistoryChange.From"/> (a copy behind it, or one the change resets, is read again when opened). Commit <paramref name="Revision"/> once all of it
/// is applied; <paramref name="More"/> says to ask again from it at once.
/// </summary>
public sealed record HistoryFeedResult(string Revision, bool Reset, IReadOnlyList<ChatSummary> Rows, IReadOnlyList<string> RemovedRows, IReadOnlyList<HistoryChange> Changes,
    bool More = false, string? Next = null);

/// <summary>Up to <see cref="WholeHistoryLimits.MaxVerify"/> copies, each by <see cref="HistoryCopyDigest"/>.</summary>
public sealed record HistoryVerifyParams(IReadOnlyList<HistoryCopyDigest> Chats);

/// <summary>
/// A client's copy of one conversation: the revision it reached, its oldest message (time, sort key and id: the copy runs from there to the newest), how many
/// messages it holds, and <see cref="HistoryDigest.Of"/> of them.
/// </summary>
public sealed record HistoryCopyDigest(string ChatId, string Revision, DateTimeOffset OldestTimestamp, string OldestSortKey, string OldestId, int Count, string Digest);

public sealed record HistoryVerifyResult(IReadOnlyList<HistoryCopyCheck> Chats);

/// <summary>
/// <paramref name="Outcome"/>: <c>same</c>; <c>differ</c> (the server holds something else from the copy's oldest on: the copy is read again); <c>behind</c> (the
/// conversation changed after the copy's revision, so the two cannot be compared yet); <c>unknown</c> (the server does not hold it whole). <paramref name="Count"/>
/// is how many the server holds over the same span.
/// </summary>
public sealed record HistoryCopyCheck(string ChatId, string Outcome, int Count);

public static class HistoryCopyOutcomes
{
    public const string Same = "same", Differ = "differ", Behind = "behind", Unknown = "unknown";
}

/// <summary>A conversation's list row as the server keeps it now (event kind <see cref="EventKinds.Row"/>); no <paramref name="Row"/> for one that left the list.</summary>
public sealed record HistoryRowChange(string ChatId, ChatSummary? Row);

public sealed record HistoryMirrorParams(bool Start = false);

/// <summary><paramref name="Running"/> says a check is under way; <paramref name="Reports"/> is the newest finished check of each service.</summary>
public sealed record HistoryMirrorResult(bool Running, IReadOnlyList<HistoryMirrorReport> Reports);

/// <summary>
/// One service's check: when it finished, how many conversations it compared, how many of those differed from the service and in what (messages the copy lacked,
/// held messages the service no longer has, messages that changed), and how many it could not read (a page still loading, Instagram's budget).
/// </summary>
public sealed record HistoryMirrorReport(string Service, DateTimeOffset At, int Checked, int Differed, int Missing, int Extra, int Changed, int Skipped);

/// <summary>
/// <paramref name="Since"/> is the revision of the client's copy of this conversation; none asks for its newest <paramref name="Limit"/> messages (50 when
/// left out, at most <see cref="WholeHistoryLimits.MaxPage"/>).
/// </summary>
public sealed record HistorySyncParams(string ChatId, string? Since = null, int? Limit = null);

/// <summary>
/// <paramref name="Reset"/>: <paramref name="Messages"/> are the conversation's newest, oldest first, and replace the client's copy; otherwise they are the
/// messages changed after the revision asked for (new, edited, reacted to, receipts), oldest change first, and <paramref name="Removed"/> names those that went.
/// A changed message older than the oldest the client holds is one the client has not read yet: it leaves it. <paramref name="MoreOlder"/> says something lies
/// before the oldest message of a reset (held, or still to read from the service); <paramref name="Complete"/> that the service has nothing older at all.
/// Commit <paramref name="Revision"/> with what was applied.
/// </summary>
public sealed record HistorySyncResult(string Revision, bool Reset, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Removed, bool MoreOlder, bool Complete);

/// <summary>
/// The messages before the client's oldest, named by its time, sort key and id (the order every copy sorts by), at most <paramref name="Limit"/> (50 when left
/// out, at most <see cref="WholeHistoryLimits.MaxPage"/>). <paramref name="HeldOnly"/> (protocol 33): only what the server holds, never read further from the
/// service; <c>More</c> then says the server holds more, and <c>Complete</c> whether the service has nothing past what it holds.
/// </summary>
public sealed record HistoryOlderParams(string ChatId, DateTimeOffset BeforeTimestamp, string BeforeSortKey, string BeforeId, int? Limit = null, bool? HeldOnly = null);

/// <summary><paramref name="Messages"/> oldest first, directly before the one asked from; <paramref name="More"/> says there is still something before them.</summary>
public sealed record HistoryOlderResult(IReadOnlyList<ChatMessage> Messages, bool More, bool Complete);

public sealed record HistoryChangesParams(string? Since = null);

/// <summary>
/// The conversations whose history changed after <c>Since</c>, each with its revision now. <paramref name="Reset"/> says the server cannot tell (another epoch,
/// or too many changed): every copy the client holds is to be read again with <see cref="ProtocolMethods.HistorySync"/>, which says by itself which need a reset.
/// </summary>
public sealed record HistoryChangesResult(string Revision, bool Reset, IReadOnlyList<HistoryChatRevision> Chats);

public sealed record HistoryChatRevision(string ChatId, string Revision);

/// <summary>
/// One change to the server's copy of one conversation, in the event sequence as a <see cref="EventKinds.History"/> frame. A client whose copy is at
/// <paramref name="From"/> applies <paramref name="Messages"/> and <paramref name="Removed"/> and is then at <paramref name="To"/>, with no call; any other client
/// (or this one when <paramref name="Truncated"/> says the messages were too many to carry) asks <see cref="ProtocolMethods.HistorySync"/> from what it holds.
/// <paramref name="Reset"/> says the server started this conversation again, so a copy of it is replaced rather than patched.
/// </summary>
public sealed record HistoryChange(string ChatId, string From, string To, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Removed,
    bool Reset = false, bool Truncated = false);

public static class WholeHistoryLimits
{
    /// <summary>The most messages one sync or older read hands back.</summary>
    public const int MaxPage = 200;
    /// <summary>The newest messages a sync with no revision hands back when it does not say.</summary>
    public const int DefaultPage = 50;
    /// <summary>The most changed messages a sync carries before it answers with a reset instead.</summary>
    public const int MaxChanges = 500;
    /// <summary>The most messages a history event carries; more are announced as truncated and read with a sync.</summary>
    public const int MaxEventMessages = 100;
    /// <summary>The most conversations one <see cref="ProtocolMethods.HistoryChanges"/> names before it answers with a reset.</summary>
    public const int MaxChangedChats = 5000;
    /// <summary>The most items (rows and messages) one feed answer carries when the client does not say, and the most it may ask for.</summary>
    public const int DefaultFeed = 500, MaxFeed = 2000;
    /// <summary>About the most bytes of rows and messages one feed answer carries (a frame holds 4 MB).</summary>
    public const int MaxFeedBytes = 1_500_000;
    /// <summary>The most copies one <see cref="ProtocolMethods.HistoryVerify"/> compares.</summary>
    public const int MaxVerify = 64;
}

/// <summary>
/// Where a message sorts in every copy (its time, then its sort key, then its id), and the page position <c>messages.page</c> hands out for it when the page
/// comes from a copy of the whole history: the server's, or a client's own (<see cref="HistoryCopies"/>), which hand out the same positions so either reads on
/// from the other's.
/// </summary>
public readonly record struct HistoryPosition(long Ticks, string SortKey, string Id) : IComparable<HistoryPosition>
{
    public const string CursorPrefix = "h1|";

    public static HistoryPosition Of(ChatMessage message) => new(message.Timestamp.UtcTicks, message.SortKey ?? "", message.Id);

    public int CompareTo(HistoryPosition other) =>
        Ticks != other.Ticks ? Ticks.CompareTo(other.Ticks)
        : string.CompareOrdinal(SortKey, other.SortKey) is var sort and not 0 ? sort
        : string.CompareOrdinal(Id, other.Id);

    public static bool IsCursor(string? cursor) => cursor?.StartsWith(CursorPrefix, StringComparison.Ordinal) == true;

    public static string Cursor(ChatMessage oldest) => CursorPrefix + oldest.Timestamp.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
        + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(oldest.SortKey ?? "")) + "|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(oldest.Id));

    public static HistoryPosition? ReadCursor(string cursor)
    {
        if (!IsCursor(cursor)) return null;
        var parts = cursor[CursorPrefix.Length..].Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ticks)) return null;
        try
        {
            return new HistoryPosition(ticks, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])),
                System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])));
        }
        catch (FormatException) { return null; }
    }
}

/// <summary>The server's whole copy of every conversation, as <see cref="ProtocolMethods.HistorySync"/>, <see cref="ProtocolMethods.HistoryOlder"/> and
/// <see cref="ProtocolMethods.HistoryChanges"/> serve it.</summary>
public interface IWholeHistory
{
    Task<HistorySyncResult> SyncAsync(HistorySyncParams request, CancellationToken cancellationToken = default);
    Task<HistoryOlderResult> OlderAsync(HistoryOlderParams request, CancellationToken cancellationToken = default);
    Task<HistoryChangesResult> ChangesAsync(HistoryChangesParams request, CancellationToken cancellationToken = default);
    Task<HistoryFeedResult> FeedAsync(HistoryFeedParams request, CancellationToken cancellationToken = default);
    Task<HistoryVerifyResult> VerifyAsync(HistoryVerifyParams request, CancellationToken cancellationToken = default);
    Task<HistoryMirrorResult> MirrorAsync(HistoryMirrorParams request, CancellationToken cancellationToken = default);
}
