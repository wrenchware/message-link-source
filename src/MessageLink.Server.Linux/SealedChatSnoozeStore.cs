using System.Security.Cryptography;
using System.Text.Json;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.Server.Linux;

/// <summary>
/// <c>snoozes.*</c> on Linux: the Windows store's layout (one file per conversation under a hashed name, in <c>ChatSnoozes-v1</c>) sealed by the
/// server's own protector instead of DPAPI. A file that does not open is skipped, not deleted: on this server that most likely means the key file
/// was swapped, and the next start with the right key should still find it.
/// </summary>
internal sealed class SealedChatSnoozeStore(string directory, IStateProtector protector) : IChatSnoozes, IChatSnoozeFiles
{
    public const string FolderName = "ChatSnoozes-v1";
    private const int MaxFile = 4096;
    private sealed record Entry(int Version, string Profile, string ChatId, DateTimeOffset Until);

    private string PathFor(string profile, string chatId) => Path.Combine(directory, NotificationIdentity.Hash(profile + "\n" + chatId) + ".protected");

    public async Task<DateTimeOffset?> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default) =>
        (await ReadAsync(PathFor(profile, chatId), cancellationToken).ConfigureAwait(false))?.Until;

    public async Task SetAsync(string profile, string chatId, DateTimeOffset? until, CancellationToken cancellationToken = default)
    {
        var path = PathFor(profile, chatId);
        if (until is null)
        {
            try { File.Delete(path); } catch (DirectoryNotFoundException) { }
            return;
        }
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Entry(1, profile, chatId, until.Value));
        byte[] sealedBytes;
        try { sealedBytes = await protector.ProtectAsync(plain).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await file.WriteAsync(sealedBytes, cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<IReadOnlyList<ChatSnooze>> ListAsync(CancellationToken cancellationToken = default) => (await ReadAllAsync(cancellationToken).ConfigureAwait(false)).Snoozes;

    // ListAsync's reading, saying how many files it passed over: the server's one-time import (SharedChatSnoozes) is not finished while any could not be read.
    public async Task<ChatSnoozeFiles> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return new([], 0);
        var snoozes = new List<ChatSnooze>();
        var unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.protected"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await ReadAsync(path, cancellationToken).ConfigureAwait(false) is { } entry) snoozes.Add(new(entry.Profile, entry.ChatId, entry.Until));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { unreadable++; }
        }
        return new(snoozes, unreadable);
    }

    private async Task<Entry?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > MaxFile) throw new InvalidDataException("Invalid chat snooze.");
        var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        Entry? entry;
        try { entry = JsonSerializer.Deserialize<Entry>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (entry is null || entry.Version != 1 || entry.ChatId.Length == 0) throw new InvalidDataException("Unsupported chat snooze.");
        return entry;
    }
}
