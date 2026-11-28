namespace MessageLink.Server;

/// <summary>
/// The whole history's waits, paces and limits, shared by its parts. Set through <see cref="ConversationHistory"/>'s own properties (the checks shorten them).
/// </summary>
internal sealed class HistoryTimings
{
    /// <summary>Between two reads of one service that nobody asked for.</summary>
    // A second: Messenger's reads of conversations not on the device go to Facebook, and nobody is waiting on these.
    public TimeSpan Pace { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>How long after starting the first walk of the list begins, and how often it repeats.</summary>
    public TimeSpan WalkDelay { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan WalkEvery { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>
    /// How many pages of the list a routine read takes, between each the wait below. Reading all of it each walk was 200 pages every half hour of Google Voice's call records
    /// alone, about 9,600 calls a day to a real account, unpaced, for rows that almost never change (audit, 2026-11-01). The newest pages are where a list changes, and a read that
    /// stops short is never taken for the whole list, so it drops nothing.
    /// </summary>
    public int ListPages { get; set; } = 10;
    /// <summary>How far the read goes once a day, for the rows below those: read in stretches of <see cref="ListPages"/>, so rows waiting to be written are not held behind it.</summary>
    public int DeepListPages { get; set; } = 200;
    public TimeSpan DeepListEvery { get; set; } = TimeSpan.FromHours(24);
    public TimeSpan ListPagePace { get; set; } = TimeSpan.FromMilliseconds(500);
    /// <summary>A conversation opened after this long without a read of its newest page has it read again behind the answer.</summary>
    public TimeSpan OpenRefreshAfter { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>
    /// The most stale conversations a minute that syncs have read again. A sync is how a client of 24 opens a conversation, but also how a phone catches up
    /// on every copy it keeps: after a restart (every conversation suspect) one catch-up's 1,700 syncs queued 500 reads of the services (2026-10-30).
    /// </summary>
    public int SyncRefreshesPerMinute { get; set; } = 6;
    /// <summary>When a change made through this host is read back, in case its service sends no event for it.</summary>
    public IReadOnlyList<TimeSpan> ConfirmAfter { get; set; } = [TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6)];
    /// <summary>A held message this young is not taken as gone for missing from a page: the service's page may not have it yet.</summary>
    public TimeSpan AbsenceGrace { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>How long a background read put off (its page partial, the budget spent) waits before it is tried again.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>The longest a resolve waits for an event's own read of the same message before it asks the service itself.</summary>
    public TimeSpan ResolveWait { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>The longest a page, a sync or a held read waits for the conversation's events already under way to be written.</summary>
    public TimeSpan SettleWait { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>How long after an event its conversation's list row is built again, so a burst builds it once.</summary>
    public TimeSpan RowsDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>The longest a feed that starts a client over waits for the first whole read of the list.</summary>
    public TimeSpan RowsReadyWait { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>
    /// The most recently active conversations of each service a mirror check compares with the service. Each is one read of its newest page, a second apart
    /// per service (owner, 2026-10-30: the walk's pace for Google, Messenger and Voice; Instagram only within its budget).
    /// </summary>
    public int MirrorPerService { get; set; } = 25;
    /// <summary>A mirror check still waiting on reads after this long is reported with those counted as not read.</summary>
    public TimeSpan MirrorTimeout { get; set; } = TimeSpan.FromMinutes(20);
    /// <summary>
    /// How long after a send through this host to a conversation whose service does not announce its own sends (<see cref="MessageLink.App.Services.HistoryFacts.EchoesOwnSends"/>)
    /// its newest page is read again. Instagram announces only what arrives, so a message sent from here reached no copy: a client drawing from the copy lost
    /// its own reply (2026-10-30).
    /// </summary>
    public TimeSpan SentReadBack { get; set; } = TimeSpan.FromSeconds(1.5);
}
