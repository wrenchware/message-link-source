using System.Runtime.InteropServices;
using System.Text;
using MessageLink.Core.Services;

namespace MessageLink.WindowsPlatform;

// The helper's protectLocal in reverse (protection_windows.go): "MLP" and a version byte, then CryptProtectData output
// sealed to this Windows user with the helper's own entropy. Opening needs nothing else and writes nothing. A cell with
// any other prefix, the retired "NBP" one included, does not open: it is counted unreadable, never read as plaintext.
public sealed class MessengerSealedCells : IHelperCellUnprotector
{
    // Both match the helper's protectedPrefix and protectedEntropy byte for byte; the contract harness compares them.
    public static readonly byte[] Prefix = [(byte)'M', (byte)'L', (byte)'P', 1];
    public static readonly byte[] Entropy = Encoding.ASCII.GetBytes("Message Link Messenger retained history v1");
    private const int UiForbidden = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public nint Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, ref DataBlob entropy, nint reserved,
        nint prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    // The value the helper sealed, or null.
    public byte[]? Open(byte[] cell)
    {
        if (cell.Length <= Prefix.Length || !cell.AsSpan(0, Prefix.Length).SequenceEqual(Prefix)) return null;
        var sealedBytes = cell[Prefix.Length..];
        var input = GCHandle.Alloc(sealedBytes, GCHandleType.Pinned);
        var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inputBlob = new DataBlob { Size = sealedBytes.Length, Data = input.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = entropy.AddrOfPinnedObject() };
            if (!CryptUnprotectData(ref inputBlob, 0, ref entropyBlob, 0, 0, UiForbidden, out var output)) return null;
            try
            {
                // The helper's format byte (1) comes first, inside the seal; any other is a format this reader does not know.
                if (output.Size == 0 || Marshal.ReadByte(output.Data) != 1) return null;
                var plain = new byte[output.Size - 1];
                Marshal.Copy(output.Data + 1, plain, 0, plain.Length);
                return plain;
            }
            finally
            {
                if (output.Size > 0) Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                LocalFree(output.Data);
            }
        }
        finally { input.Free(); entropy.Free(); }
    }
}
