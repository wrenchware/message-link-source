using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MessageLink.Server;

/// <summary>
/// Web Push message encryption (RFC 8291) in the aes128gcm content coding (RFC 8188): each notification is sealed to the phone's P-256 key and the secret it
/// made at registration, under a key made for that one message, so Firebase carries only ciphertext of one padded size. The phone opens it with its key in
/// Android's keystore (android/app/.../push/PushCrypto.kt); the harness holds the two together through a fixture (PushChecks).
/// </summary>
public static class WebPushEncryption
{
    /// <summary>The record size written in the header: more than any notification this server sends, so each is one record.</summary>
    public const int RecordSize = 4096;

    /// <summary>
    /// Seals <paramref name="plaintext"/>, padded with zeros to <paramref name="paddedTo"/> bytes so every push is the same size, for the phone whose public
    /// key is <paramref name="uaPublic"/> (65-byte uncompressed point) and whose secret is <paramref name="auth"/> (16 bytes). <paramref name="salt"/> and
    /// <paramref name="sender"/> are fresh for each message unless a check fixes them.
    /// </summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, byte[] uaPublic, byte[] auth, int paddedTo, byte[]? salt = null, ECDiffieHellman? sender = null)
    {
        if (uaPublic is not { Length: 65 } || uaPublic[0] != 4) throw new ArgumentException("The phone's key is not an uncompressed P-256 point.", nameof(uaPublic));
        if (auth is not { Length: 16 }) throw new ArgumentException("The phone's secret is not 16 bytes.", nameof(auth));
        if (plaintext.Length + 1 > paddedTo || paddedTo + 16 > RecordSize) throw new ArgumentException("The notification is too long for one push.", nameof(plaintext));
        salt ??= RandomNumberGenerator.GetBytes(16);
        using var made = sender is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var ours = sender ?? made!;
        using var phone = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var asPublic = Point(ours);
        var ecdh = ours.DeriveRawSecretAgreement(phone.PublicKey);

        var keyInfo = Concat(Encoding.ASCII.GetBytes("WebPush: info"), [0], uaPublic, asPublic);
        var ikm = HMACSHA256.HashData(HMACSHA256.HashData(auth, ecdh), Concat(keyInfo, [1]));
        var prk = HMACSHA256.HashData(salt, ikm);
        var cek = HMACSHA256.HashData(prk, Concat(Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm"), [0, 1]))[..16];
        var nonce = HMACSHA256.HashData(prk, Concat(Encoding.ASCII.GetBytes("Content-Encoding: nonce"), [0, 1]))[..12];

        // The last record's delimiter (0x02), then zeros up to the padded size (RFC 8188 2).
        var record = new byte[paddedTo];
        plaintext.CopyTo(record);
        record[plaintext.Length] = 2;
        var sealedRecord = new byte[record.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, record, sealedRecord, tag);

        var header = new byte[16 + 4 + 1 + asPublic.Length];
        salt.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), RecordSize);
        header[20] = (byte)asPublic.Length;
        asPublic.CopyTo(header, 21);
        return Concat(header, sealedRecord, tag);
    }

    /// <summary>
    /// Whether a push can be sealed to <paramref name="uaPublic"/>: an uncompressed point on P-256, which a key agreement takes. One of the right length that
    /// is not on the curve would fail every push sealed to it, so a registration is held to this.
    /// </summary>
    public static bool Accepts(byte[]? uaPublic)
    {
        if (uaPublic is not { Length: 65 } || uaPublic[0] != 4) return false;
        try
        {
            using var phone = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
            });
            using var ours = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ours.DeriveRawSecretAgreement(phone.PublicKey);
            return true;
        }
        // Windows refuses a point off the curve as parameters "not valid for this platform"; OpenSSL as a cryptographic failure.
        catch (Exception failure) when (failure is CryptographicException or PlatformNotSupportedException) { return false; }
    }

    /// <summary>A key's public point as Web Push names it: 0x04 || x || y.</summary>
    public static byte[] Point(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        return Concat([4], q.X!, q.Y!);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts) { part.CopyTo(all, at); at += part.Length; }
        return all;
    }
}
