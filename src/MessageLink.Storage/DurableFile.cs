namespace MessageLink.Storage;

// Writes the temporary file a saved session is then renamed from, and does not return until its bytes are on the disk.
// NTFS journals the rename but not the data, so a rename after a plain write can survive a power loss that the data does
// not: the session file is then the right length and full of zeros, Windows can no longer unprotect it, and the account
// has to sign in again, which on Instagram risks a checkpoint. The notification and snooze stores already wrote this way.
// The caller still does its own rename, so each keeps its own temporary name, its checks before the rename and its cleanup.
public static class DurableFile
{
    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.WriteThrough | FileOptions.Asynchronous);
        await file.WriteAsync(bytes, cancellationToken);
        // A flush to the device waits on the disk. Off the calling thread, which for a session save can be the interface's.
        await Task.Run(() => file.Flush(flushToDisk: true), CancellationToken.None);
    }
}
