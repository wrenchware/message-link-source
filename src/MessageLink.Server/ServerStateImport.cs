using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// A server's <c>state.import</c>: a PC's choices, handed up as a file (<see cref="UploadStore"/>) and taken into the shared state
/// once. The dispatcher journals it by the move's id, so a repeat of that id answers the first answer; the store adds and never replaces
/// (<see cref="SharedStateStore.ImportBundleAsync"/>), so a second move under another id adds nothing it has not got. The upload is the server's to delete: it is claimed,
/// read whole (a bundle is at most <see cref="MoveBundle.MaxBytes"/>) and gone however the import ends, so a bundle of names and drafts never lingers in the spill folder.
/// Logs carry fixed words and counts only.
/// </summary>
internal sealed class ServerStateImport(SharedStateStore store, UploadStore uploads, Func<Task> afterNewKey, Action<string> log) : IStateImport
{
    public async Task<StateImportResult> ImportAsync(StateImportParams request, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        using (var claim = uploads.Claim([request.Bundle]))
        {
            var file = claim.FileAt(0);
            if (file.Length > MoveBundle.MaxBytes || !string.Equals(file.MimeType, MoveBundle.MimeType, StringComparison.OrdinalIgnoreCase))
            {
                log("state import outcome=invalid reason=upload");
                return new(StateImportStatuses.Invalid);
            }
            await using var stream = await file.OpenRead(cancellationToken).ConfigureAwait(false);
            bytes = new byte[file.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        var bundle = MoveBundle.Read(bytes);
        // What it says it holds is what it holds: a bundle cut short or changed on the way has rows its own counts do not match.
        if (bundle is not { Version: MoveBundle.CurrentVersion, LocalKey.Length: > 0 and <= 1024, Rows: { Count: <= 200_000 } rows, Counts: { } counts, Digest: { } digest }
            || rows.Any(row => row?.Key is null) || !MoveCounts.Same(counts, MoveCounts.Of(rows.Select(row => row.Key))) || digest != MoveCounts.DigestOf(rows.Select(row => row.Key))
            || MessageLink.Presentation.Services.NotificationIdentity.ProfileOf(bundle.LocalKey) is null)
        {
            log("state import outcome=invalid reason=bundle");
            return new(StateImportStatuses.Invalid);
        }
        var imported = await store.ImportBundleAsync(bundle, request.Resume, cancellationToken).ConfigureAwait(false);
        // A key the server now serves as its own: its clients take it from the state, which is built again.
        if (imported.Pinned) await afterNewKey().ConfigureAwait(false);
        log($"state import outcome={imported.Status} added={imported.Added} skipped={imported.Skipped} refused={imported.Refused}");
        return new(imported.Status, imported.Added, imported.Skipped, imported.Refused, imported.PerKind, imported.Digest, imported.Epoch, imported.Rev, imported.Values);
    }
}
