using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>Where an <see cref="OperationJournal"/> keeps itself between runs: one blob, replaced whole. The blob is plain here; a store protects it.</summary>
public interface IOperationJournalStore
{
    /// <summary>What was last written, or null when nothing was. Throws when something is there that cannot be read.</summary>
    Task<byte[]?> ReadAsync();

    /// <summary>Replaces what is stored, all or nothing: a crash leaves the old blob or the new, never a mix.</summary>
    Task WriteAsync(byte[] value);
}

/// <summary>
/// The journal's file, protected as a whole through <see cref="IStateProtector"/> (DPAPI on Windows, a key file on the server). A write goes to
/// a temporary file beside it and is moved over the old one, as the app's other protected stores do, so a power cut leaves one or the other.
/// </summary>
public sealed class ProtectedFileJournalStore(string path, IStateProtector protector) : IOperationJournalStore
{
    public async Task<byte[]?> ReadAsync()
    {
        if (!File.Exists(path)) return null;
        return await protector.UnprotectAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false);
    }

    public async Task WriteAsync(byte[] value)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, await protector.ProtectAsync(value).ConfigureAwait(false)).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }
}
