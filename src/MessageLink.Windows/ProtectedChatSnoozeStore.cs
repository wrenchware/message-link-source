using System.Text.Json;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace MessageLink.WindowsPlatform;

// One file per snoozed conversation, named by the same opaque hash the chat preferences use. Unlike a pin or a
// mute, a snooze has to name its conversation: the reminder fires from a timer with nothing else to go on. So the
// conversation ID is written the way notification records write theirs, protected to this Windows user.
public sealed class ProtectedChatSnoozeStore : IChatSnoozes, IChatSnoozeFiles
{
    // The folder's name under the data root; the server composes the path (AppPaths is the backends' own).
    public const string FolderName = "ChatSnoozes-v1";
    private readonly string directory;
    public ProtectedChatSnoozeStore(string directory) => this.directory = directory;
    private sealed record Entry(int Version, string Profile, string ChatId, DateTimeOffset Until);
    private string PathFor(string profile, string chatId) => Path.Combine(directory, NotificationIdentity.Hash(profile + "\n" + chatId) + ".protected");

    public async Task<DateTimeOffset?> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default)
        => (await ReadAsync(PathFor(profile, chatId), cancellationToken))?.Until;

    public async Task SetAsync(string profile, string chatId, DateTimeOffset? until, CancellationToken cancellationToken = default)
    {
        var path = PathFor(profile, chatId);
        if (until is null)
        {
            try { File.Delete(path); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            return;
        }
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Entry(1, profile, chatId, until.Value));
        byte[] encrypted;
        try
        {
            var buffer = await new DataProtectionProvider("LOCAL=user").ProtectAsync(CryptographicBuffer.CreateFromByteArray(plain));
            CryptographicBuffer.CopyToByteArray(buffer, out encrypted);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
            { await file.WriteAsync(encrypted, cancellationToken); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<IReadOnlyList<ChatSnooze>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return [];
        var snoozes = new List<ChatSnooze>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.protected"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // One unreadable file must not hide every other reminder. A snooze that cannot be read is dropped:
            // the conversation simply notifies normally again.
            try
            {
                if (await ReadAsync(path, cancellationToken) is { } entry) snoozes.Add(new(entry.Profile, entry.ChatId, entry.Until));
                else TryDelete(path);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { TryDelete(path); }
        }
        return snoozes;
    }

    // For the server's one-time import (SharedChatSnoozes): what ListAsync reads, but deleting nothing (the files may be the in-process app's own, on a root
    // a local server shares) and saying how many could not be read, so an import that missed one is tried again rather than recorded done.
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
                if (await ReadAsync(path, cancellationToken) is { } entry) snoozes.Add(new(entry.Profile, entry.ChatId, entry.Until));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { unreadable++; }
        }
        return new(snoozes, unreadable);
    }

    private static async Task<Entry?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("Invalid chat snooze.");
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var decrypted = await new DataProtectionProvider().UnprotectAsync(CryptographicBuffer.CreateFromByteArray(bytes));
        CryptographicBuffer.CopyToByteArray(decrypted, out var plain);
        Entry? entry;
        try { entry = JsonSerializer.Deserialize<Entry>(plain); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
        if (entry is null || entry.Version != 1 || entry.ChatId.Length == 0) throw new InvalidDataException("Unsupported chat snooze.");
        return entry;
    }

    private static void TryDelete(string path)
    { try { File.Delete(path); } catch (Exception) { /* A stale file is re-examined next time. */ } }
}
