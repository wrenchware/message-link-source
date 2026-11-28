using MessageLink.Core.Services;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
namespace MessageLink.WindowsPlatform;
public sealed class WindowsStateProtector : IStateProtector
{
    public async Task<byte[]> ProtectAsync(byte[] value)
    {
        var buffer = await new DataProtectionProvider("LOCAL=user").ProtectAsync(CryptographicBuffer.CreateFromByteArray(value));
        CryptographicBuffer.CopyToByteArray(buffer, out var bytes); return bytes;
    }
    public async Task<byte[]> UnprotectAsync(byte[] value)
    {
        Windows.Storage.Streams.IBuffer buffer;
        try { buffer = await new DataProtectionProvider().UnprotectAsync(CryptographicBuffer.CreateFromByteArray(value)); }
        // Bytes that aren't a protected value at all fail to parse (CRYPT_E_ASN1_*, 0x800931xx; damaged bytes measured as
        // CRYPT_E_ASN1_BADTAG). Said as the other protectors say damaged data, and marked as its framing, so a caller can set
        // the value aside; every other failure (decryption, the key or the service unavailable) stays as it was and says
        // nothing about the bytes.
        catch (System.Runtime.InteropServices.COMException failure) when ((failure.HResult & unchecked((int)0xFFFFFF00)) == unchecked((int)0x80093100))
        {
            throw SealedFraming.Damaged(new System.Security.Cryptography.CryptographicException("The protected value is damaged."));
        }
        CryptographicBuffer.CopyToByteArray(buffer, out var bytes); return bytes;
    }
}
