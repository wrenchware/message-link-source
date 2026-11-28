using System.Security.Cryptography;
using MessageLink.Core.Services;

namespace MessageLink.App.Services;

// The server's counterpart to WindowsStateProtector: AES-256-GCM under a 32-byte key kept in a file the server names,
// private to its owner. Layout: "MSK" 1 | 12-byte nonce | 16-byte tag | ciphertext. The prefix and associated data
// differ from the Messenger helper's sealed values ("MSP" 2, protection_linux.go), so neither format opens as the other.
// The key is read for each operation and zeroed straight after, so a loosened or replaced file takes effect at once.
// Generating a key is the server's job, and only where nothing sealed exists yet: a replacement key silently orphans
// every sealed value (the server foundation's PrivateStore refuses to, and so should the server).
public sealed class FileKeyStateProtector : IStateProtector
{
    private static readonly byte[] Prefix = [(byte)'M', (byte)'S', (byte)'K', 1];
    private static readonly byte[] AssociatedData = "Messages server private state v1"u8.ToArray();
    private const int KeyLength = 32, NonceLength = 12, TagLength = 16;
    private readonly string keyPath;

    // Reads the key once, so a missing, shared or wrong-sized key file fails when the server starts, not on first use.
    public FileKeyStateProtector(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath)) throw new ArgumentException("A key file path is required.", nameof(keyPath));
        this.keyPath = Path.GetFullPath(keyPath);
        CryptographicOperations.ZeroMemory(ReadKey());
    }

    public Task<byte[]> ProtectAsync(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new byte[Prefix.Length + NonceLength + TagLength + value.Length];
        Prefix.CopyTo(result, 0);
        var nonce = result.AsSpan(Prefix.Length, NonceLength);
        RandomNumberGenerator.Fill(nonce);
        var key = ReadKey();
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, value, result.AsSpan(Prefix.Length + NonceLength + TagLength), result.AsSpan(Prefix.Length + NonceLength, TagLength), AssociatedData);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return Task.FromResult(result);
    }

    public Task<byte[]> UnprotectAsync(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.AsSpan().StartsWith(Prefix)) throw SealedFraming.Damaged(new InvalidDataException("The value is not protected by a key file."));
        if (value.Length < Prefix.Length + NonceLength + TagLength) throw SealedFraming.Damaged(new InvalidDataException("The protected value is incomplete."));
        var plain = new byte[value.Length - Prefix.Length - NonceLength - TagLength];
        var key = ReadKey();
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(value.AsSpan(Prefix.Length, NonceLength), value.AsSpan(Prefix.Length + NonceLength + TagLength), value.AsSpan(Prefix.Length + NonceLength, TagLength), plain, AssociatedData);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plain);
            // No inner exception and no detail: whether the key or the data was wrong is not for a log to tell.
            throw new CryptographicException("The protected value failed authentication.");
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return Task.FromResult(plain);
    }

    private byte[] ReadKey() => ReadKey(keyPath);

    // The mode is read from the open handle, not the path, so it cannot change between the check and the read. Windows
    // has no mode bits; the server's folder permissions are what protect the file there. The Messenger helper's key is read
    // by the same rules (HelperKeyFileCells; protectionKey in protection_linux.go).
    internal static byte[] ReadKey(string keyPath)
    {
        using var file = new FileStream(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(file.SafeFileHandle) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new InvalidDataException("The key file must be private to its owner (mode 0600).");
        // One byte past the key length, so a longer file is refused without reading all of it.
        var key = new byte[KeyLength + 1];
        var read = file.ReadAtLeast(key, key.Length, throwOnEndOfStream: false);
        if (read != KeyLength)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("The key file must hold exactly 32 bytes.");
        }
        var exact = key.AsSpan(0, KeyLength).ToArray();
        CryptographicOperations.ZeroMemory(key);
        return exact;
    }
}
