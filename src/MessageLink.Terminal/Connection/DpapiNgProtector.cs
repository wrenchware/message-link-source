using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using MessageLink.Core.Services;

namespace MessageLink.Terminal.Connection;

/// <summary>
/// DPAPI-NG for this Windows user (the descriptor <c>LOCAL=user</c>), called through ncrypt.dll. The app seals its files with WinRT's
/// <c>DataProtectionProvider("LOCAL=user")</c>, which is this same protection, so a blob either one makes the other opens: mlink reads the token the PC's own
/// server keeps for its clients (<c>&lt;data root&gt;\Server\client.token</c>) without being a WinRT program.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiNgProtector : IStateProtector
{
    public const string Descriptor = "LOCAL=user";

    // NCRYPT_SILENT_FLAG: never a prompt, which a terminal has nowhere to show.
    private const uint Silent = 0x40;

    public Task<byte[]> ProtectAsync(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Check(NCryptCreateProtectionDescriptor(Descriptor, 0, out var descriptor));
        try
        {
            Check(NCryptProtectSecret(descriptor, Silent, value, (uint)value.Length, IntPtr.Zero, IntPtr.Zero, out var blob, out var length));
            return Task.FromResult(Take(blob, length, secret: false));
        }
        finally { _ = NCryptCloseProtectionDescriptor(descriptor); }
    }

    public Task<byte[]> UnprotectAsync(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Check(NCryptUnprotectSecret(IntPtr.Zero, Silent, value, (uint)value.Length, IntPtr.Zero, IntPtr.Zero, out var plain, out var length));
        return Task.FromResult(Take(plain, length, secret: true));
    }

    private static void Check(int status)
    {
        // The status alone: whether it was the key or the data is not for a log to tell.
        if (status != 0) throw new CryptographicException(status);
    }

    // The output into managed memory, and the native copy freed (zeroed first when it held the plain value).
    private static byte[] Take(IntPtr native, uint length, bool secret)
    {
        try
        {
            var bytes = new byte[length];
            if (length > 0) Marshal.Copy(native, bytes, 0, (int)length);
            if (secret && length > 0) Marshal.Copy(new byte[length], 0, native, (int)length);
            return bytes;
        }
        finally { if (native != IntPtr.Zero) LocalFree(native); }
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptCreateProtectionDescriptor(string descriptorString, uint flags, out IntPtr descriptor);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptCloseProtectionDescriptor(IntPtr descriptor);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptProtectSecret(IntPtr descriptor, uint flags, byte[] data, uint dataLength, IntPtr memPara, IntPtr window,
        out IntPtr protectedBlob, out uint protectedBlobLength);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptUnprotectSecret(IntPtr descriptor, uint flags, byte[] protectedBlob, uint protectedBlobLength, IntPtr memPara, IntPtr window,
        out IntPtr data, out uint dataLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
