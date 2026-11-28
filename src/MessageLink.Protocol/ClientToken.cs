using System.Security.Cryptography;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// The secret a client presents to the local server (<c>Authorization: Bearer</c>, authentication). The server makes
/// 32 random bytes the first time it needs them and keeps them at <c>&lt;folder&gt;\client.token</c>, protected through
/// <see cref="IStateProtector"/> (DPAPI on Windows), so only the same user on the same machine can read the file; the client reads it back
/// each time it connects, so a token the server replaced is picked up. The pipe is already limited to this user; the token is for the
/// admission the server believes it has given (<c>admitted</c>), and it is what a remote server's per-device tokens grow from.
/// A client of a remote server keeps the device token it was given at pairing the same way, under its own <paramref name="fileName"/>
/// (<see cref="StoreAsync"/>); that one is never made here.
/// </summary>
public sealed class ClientToken(string folder, IStateProtector protector, string fileName = ClientToken.FileName)
{
    public const string FileName = "client.token";
    public const int Length = 32;

    private readonly SemaphoreSlim gate = new(1, 1);

    public string Path { get; } = System.IO.Path.Combine(folder, fileName);

    /// <summary>The server's side: the token that is kept, made and kept first if there is none (or none that can be read back).</summary>
    public async Task<byte[]> GetOrCreateAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (await ReadAsync().ConfigureAwait(false) is { } existing) return existing;
            Directory.CreateDirectory(folder);
            var token = RandomNumberGenerator.GetBytes(Length);
            var temporary = Path + ".tmp";
            await File.WriteAllBytesAsync(temporary, await protector.ProtectAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            File.Move(temporary, Path, true);
            return token;
        }
        finally { gate.Release(); }
    }

    /// <summary>Keeps <paramref name="token"/>, protected, in place of whatever was kept: the device token a remote server gave at pairing.</summary>
    public async Task StoreAsync(byte[] token)
    {
        if (token.Length != Length) throw new ArgumentException("A token is 32 bytes.", nameof(token));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(folder);
            var temporary = Path + ".tmp";
            await File.WriteAllBytesAsync(temporary, await protector.ProtectAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            File.Move(temporary, Path, true);
        }
        finally { gate.Release(); }
    }

    /// <summary>The client's side: the token that is kept, or null when there is none or it cannot be read back (a file from another user or machine).</summary>
    public async Task<byte[]?> ReadAsync()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var token = await protector.UnprotectAsync(await File.ReadAllBytesAsync(Path).ConfigureAwait(false)).ConfigureAwait(false);
            return token.Length == Length ? token : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }

    /// <summary>The value of an <c>Authorization</c> header carrying <paramref name="token"/>.</summary>
    public static string HeaderValue(byte[] token) => "Bearer " + Encode(token);

    /// <summary>A token as text: base64url with no padding, as it goes in a header and in a pairing answer.</summary>
    public static string Encode(byte[] token) => Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The bytes of a token in its text form (<see cref="Encode"/>), or null when the text is not one.</summary>
    public static byte[]? Decode(string? text)
    {
        if (text is null) return null;
        text = text.Trim().Replace('-', '+').Replace('_', '/');
        if (text.Length is 0 or > 64) return null;
        text = text.PadRight((text.Length + 3) / 4 * 4, '=');
        try { return Convert.FromBase64String(text); }
        catch (FormatException) { return null; }
    }

    /// <summary>The token an <c>Authorization</c> header presents, or null when it presents none in the form this app writes. Never logged.</summary>
    public static byte[]? Presented(string? authorization)
    {
        const string Scheme = "Bearer ";
        if (authorization is null || !authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return null;
        return Decode(authorization[Scheme.Length..]);
    }

    /// <summary>Whether an <c>Authorization</c> header carries <paramref name="expected"/>, compared in fixed time. Nothing about either is ever logged.</summary>
    public static bool Matches(byte[] expected, string? authorization) =>
        Presented(authorization) is { } presented && CryptographicOperations.FixedTimeEquals(presented, expected);
}
