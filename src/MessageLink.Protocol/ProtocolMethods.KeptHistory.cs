using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// The history a provider's helper keeps on the host's disk, searched there (SWITCHBOARD.md 3.0b). It is search.query with a source
// (SearchQueryParams.KeptSource): the kept answer comes back as a page that is whole (every match, newest first, no cursor), because the
// client's universal search merges it with the catalogue and pages through both itself. Nothing is written to the store and nothing is
// journaled; the matches are results the person asked for, so they carry message text on the wire and are written nowhere else.
public static partial class ProtocolMethods
{
    /// <summary>Starts the first read of the kept history in the background (the search dialog is opening). Best effort; not journaled.</summary>
    public const string SearchWarmKept = "search.warmKept";

    private static Entry[] KeptHistoryEntries() =>
    [
        new(SearchWarmKept, "IKeptHistorySearch.Warm", null, typeof(Done), ProtocolVersion.Original),
    ];
}
