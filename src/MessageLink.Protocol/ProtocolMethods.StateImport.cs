namespace MessageLink.Protocol;

// Moving a PC's choices onto a Messages Server (protocol version 11). The PC reads its own stores (a MoveBundle), hands the bundle
// up like a file (media.upload*, so it is encrypted at rest on the host and never a frame of its own), and asks for it to be taken in once:
//
//   state.import   Journaled by its operation id (the move's id), so a repeat of that id answers the first answer. Takes the bundle's rows into the shared
//                  state, adding where the space holds nothing for a key and never replacing, so a second move, with another id, adds nothing it has not already
//                  got. A space with no key yet is filed under the bundle's key (the PC's, adopted verbatim, so no row is renamed); a space already filed under
//                  another key answers `space_mismatch` and keeps nothing. The answer counts what happened and says what the server now holds of the bundle's
//                  keys (per kind, and a digest), for the PC to compare with what it sent.
//
// The answer carries counts and a digest of keys only: the journal keeps it, and nothing a person wrote may be in it.
public static partial class ProtocolMethods
{
    public const string StateImport = "state.import";

    private static Entry[] StateImportEntries() =>
    [
        new(StateImport, "IStateImport.ImportAsync", typeof(StateImportParams), typeof(StateImportResult), ProtocolVersion.StateImport, true),
    ];
}

/// <summary>Takes in the bundle <paramref name="Bundle"/> names, an upload that was begun with <see cref="MoveBundle.MimeType"/> and ended.</summary>
/// <param name="Resume">
/// This is a retry of a move the caller began and did not see finish (its marker says <c>moving</c> to this server). A space already filed under the bundle's key that holds
/// rows is refused (<see cref="StateImportStatuses.SpaceInUse"/>) unless it is set: such a space has had a life of its own, and a first move does not go into it.
/// </param>
public sealed record StateImportParams(string ClientOpId, UploadRef Bundle, bool Resume = false) : MutationParams(ClientOpId);

/// <summary>The words <see cref="StateImportResult.Status"/> carries; plain strings so a word a newer server adds is simply not one this build knows.</summary>
public static class StateImportStatuses
{
    /// <summary>The bundle was taken in (every row added or already there); the counts say how it went.</summary>
    public const string Done = "done";
    /// <summary>The server's shared state is filed under another key than the bundle's. Nothing was kept, and the server is unchanged.</summary>
    public const string SpaceMismatch = "space_mismatch";
    /// <summary>The bundle is not one this server reads (another format, damaged, or its counts do not match its rows). Nothing was kept.</summary>
    public const string Invalid = "invalid";
    /// <summary>The server's shared state is already filed under the bundle's key and holds rows, and this is not a retry of an unfinished move. Nothing was kept.</summary>
    public const string SpaceInUse = "space_in_use";
}

/// <summary>
/// What the import did. <paramref name="Added"/>: rows that were new to the space. <paramref name="Skipped"/>: rows it already held (live or ended), left as they
/// were. <paramref name="Refused"/>: rows the server does not keep (not a shared kind, too large, another server's). <paramref name="PerKind"/> and
/// <paramref name="Digest"/> are <see cref="MoveCounts"/> of the bundle's keys the space holds live now, and <paramref name="Values"/> is
/// <c>MoveCounts.ValuesDigestOf</c> of those keys with the values it stores, so a whole move answers the very counts and
/// digests the bundle carried; a key ended since, or held with another value, does not. <paramref name="Epoch"/> and <paramref name="Rev"/> are where the space stands after it.
/// </summary>
public sealed record StateImportResult(string Status, int Added = 0, int Skipped = 0, int Refused = 0, IReadOnlyDictionary<string, int>? PerKind = null,
    string? Digest = null, string? Epoch = null, long Rev = 0, string? Values = null);

/// <summary>The server's import as the dispatcher serves it: the server's own, or a client's proxy. A server without a shared state leaves it null and the method answers <c>not_supported</c>.</summary>
public interface IStateImport
{
    Task<StateImportResult> ImportAsync(StateImportParams request, CancellationToken cancellationToken = default);
}
