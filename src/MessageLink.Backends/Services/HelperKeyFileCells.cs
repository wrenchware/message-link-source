using System.Security.Cryptography;
using MessageLink.Core.Services;

namespace MessageLink.App.Services;

// The Linux server's counterpart to MessengerSealedCells: the Messenger helper's unprotectLocal (protection_linux.go) in
// reverse. Layout: "MSP" 2 | 12-byte nonce | ciphertext | 16-byte tag (Go's GCM Seal appends its tag), AES-256-GCM under
// the 32-byte key in MESSAGES_HELPER_KEY_FILE with the helper's associated data. The key is read for each cell and
// zeroed straight after, by the same rules as the helper and FileKeyStateProtector, and opening writes nothing.
public sealed class HelperKeyFileCells : IHelperCellUnprotector
{
    private static readonly byte[] Prefix = [(byte)'M', (byte)'S', (byte)'P', 2];
    private static readonly byte[] AssociatedData = "Messages server Messenger retained history v2"u8.ToArray();
    private const int NonceLength = 12, TagLength = 16;
    private readonly string keyPath;

    public HelperKeyFileCells(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath)) throw new ArgumentException("A key file path is required.", nameof(keyPath));
        this.keyPath = Path.GetFullPath(keyPath);
    }

    public byte[]? Open(byte[] cell)
    {
        if (cell.Length < Prefix.Length + NonceLength + TagLength || !cell.AsSpan().StartsWith(Prefix)) return null;
        byte[] key;
        // A key that cannot be read opens nothing, as a cell under another key does: the search then answers without it.
        try { key = FileKeyStateProtector.ReadKey(keyPath); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
        var body = cell.AsSpan(Prefix.Length);
        var plain = new byte[body.Length - NonceLength - TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(body[..NonceLength], body[NonceLength..^TagLength], body[^TagLength..], plain, AssociatedData);
            return plain;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plain);
            return null;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
