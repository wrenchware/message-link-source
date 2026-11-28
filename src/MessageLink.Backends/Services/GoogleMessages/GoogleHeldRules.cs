namespace MessageLink.App.Services.GoogleMessages;

internal enum GoogleHeldSource { None, Memory, Archive }

// When an opening Google conversation can be drawn from what this PC holds before the phone answers, and from where.
// Pure, so the offline checks can hold it to its cases; GoogleMessagesBackend.Window and .PriorityCatchUp use it.
internal static class GoogleHeldRules
{
    // Why what is kept cannot vouch for this conversation up to now, or null when it can: its crawl finished, and its last
    // catch-up reached at least where it stood (its newest message's time) in this connection's first list. Anything
    // newer than that came live, and was kept as it came.
    public static string? Refusal(bool connected, GoogleCrawlState? state, DateTimeOffset? headAtConnect) =>
        !connected ? "not_connected"
        : state is not { Done: true, CatchUpCursor: null } ? "crawl_not_done"
        : headAtConnect is not { } head ? "not_listed"
        : state.UpdatedAt < head ? "behind"
        : null;

    // A finished crawl whose conversation has had messages since its last catch-up: the ones to catch up first at connect.
    public static bool NeedsCatchUp(GoogleCrawlState? state, DateTimeOffset chatAt) => state is { Done: true } && chatAt > state.UpdatedAt;

    // Where the messages after `after` are read. Memory, when this connection holds every message from `freshFrom` (the
    // archive's boundary that its catch-up reached, or that had nothing after it at connect) and either `after` is at or past
    // it, or there are enough of them for a first drawing on their own. Otherwise the archive, once it has loaded; it pauses
    // its loading while the app is in use, so after a launch that can be minutes away.
    public static GoogleHeldSource Source(DateTimeOffset after, DateTimeOffset? freshFrom, int heldSinceFresh, int limit, bool archiveLoaded) =>
        freshFrom is { } from && (after >= from || heldSinceFresh >= limit) ? GoogleHeldSource.Memory
        : archiveLoaded ? GoogleHeldSource.Archive
        : GoogleHeldSource.None;
}
