using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MessageLink.App.Services.GoogleMessages;

namespace MessageLink.Server.Linux;

/// <summary>
/// What a read of Chrome's cookie store found: the cookies a service needs (by the rules of its <see cref="ChromiumRenewalTarget"/>, Messages' unless the read says otherwise, each from the host it is
/// accepted from), and how long Chrome was told to keep <c>__Secure-1PSIDTS</c>. Credentials: the type prints only its name, and the values go nowhere but the pairing, the add and the in-place adoption.
/// </summary>
internal sealed record ChromeCookieRead(Dictionary<string, string> Cookies, string SidtsLifetime, ChromiumRenewalTarget? Target = null)
{
    /// <summary>The value of <c>__Secure-1PSIDTS</c>, or null. Compared with the one before, in memory, and dropped.</summary>
    public string? Sidts => Cookies.TryGetValue("__Secure-1PSIDTS", out var value) ? value : null;

    /// <summary>Whether every cookie a sign-in needs is there (by the rules of <see cref="Target"/>, <see cref="ChromiumRenewalTarget.Messages"/> when none is given).</summary>
    public bool Complete => (Target ?? ChromiumRenewalTarget.Messages).IsComplete(Cookies);

    public override string ToString() => nameof(ChromeCookieRead);
}

/// <summary>Reads the cookies Chrome holds on disk for one service's sign-in. The real one is <see cref="ChromeCookieStore"/>; the harness gives a fake.</summary>
internal interface IChromeCookieSource
{
    /// <summary>Null when the store cannot be read at all right now (not made yet, locked past the retries, unreadable); an empty read when it holds nothing of Google's.</summary>
    Task<ChromeCookieRead?> ReadAsync(string profileFolder, ChromiumRenewalTarget target, CancellationToken cancellationToken);

    /// <summary>Google Messages' cookies, as before Voice ran here.</summary>
    Task<ChromeCookieRead?> ReadAsync(string profileFolder, CancellationToken cancellationToken) => ReadAsync(profileFolder, ChromiumRenewalTarget.Messages, cancellationToken);
}

/// <summary>
/// Chrome's cookie store, read from disk and never through the browser (Chrome on the server): the file <c>Default/Network/Cookies</c> (<c>Default/Cookies</c> before Chrome 96),
/// a SQLite database Chrome keeps open and writes to in batches (every 30 seconds, or at 512 changes: <c>kCommitInterval</c> in <c>sqlite_persistent_cookie_store.cc</c>).
/// <para>
/// Read from a copy: the database and its journal are copied into the profile's private folder and opened there, so Chrome's locks never matter and nothing is ever written to Chrome's own
/// file. A copy taken mid-write may be torn, which SQLite reports as an error; that is retried a few times. The copy is deleted after every read. Only Google's cookies are selected.
/// </para>
/// <para>
/// Decryption, as Chrome does it on Linux with <c>--password-store=basic</c> (no keyring in a container): a value is <c>v10</c> followed by AES-128-CBC; the key is
/// PBKDF2-HMAC-SHA1 of the password <c>peanuts</c> with the salt <c>saltysalt</c> and ONE iteration, 16 bytes; the IV is sixteen spaces; PKCS#7 padding. (<c>v11</c> is encrypted with a key
/// from a keyring, which this Chrome does not have and this reader does not know: such a value is left out, not tried with this key.) From cookie database version 24 the plaintext starts with the 32-byte SHA-256 of the cookie's
/// <c>host_key</c>, which is checked and removed (version 24 is the current <c>kCurrentVersionNumber</c>). A value that does not decrypt is left out, never guessed.
/// </para>
/// </summary>
internal sealed class ChromeCookieStore : IChromeCookieSource
{
    /// <summary>The tries a torn copy gets.</summary>
    public int Attempts { get; init; } = 4;
    public TimeSpan RetryGap { get; init; } = TimeSpan.FromMilliseconds(400);

    private static readonly byte[] Key = Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes("peanuts"), Encoding.ASCII.GetBytes("saltysalt"), 1, HashAlgorithmName.SHA1, 16);
    private static readonly byte[] Iv = Enumerable.Repeat((byte)' ', 16).ToArray();

    /// <summary>Google Messages' cookies (<see cref="ChromiumRenewalTarget.Messages"/>).</summary>
    public Task<ChromeCookieRead?> ReadAsync(string profileFolder, CancellationToken cancellationToken) => ReadAsync(profileFolder, ChromiumRenewalTarget.Messages, cancellationToken);

    public async Task<ChromeCookieRead?> ReadAsync(string profileFolder, ChromiumRenewalTarget target, CancellationToken cancellationToken)
    {
        var source = StorePath(profileFolder);
        if (source is null) return null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return ReadCopy(source, profileFolder, target); }
            // Copied mid-write, or the file is busy: the next copy is whole.
            catch (Exception failure) when (failure is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                await Task.Delay(RetryGap, cancellationToken).ConfigureAwait(false);
            }
        }
        return null;
    }

    internal static string? StorePath(string profileFolder)
    {
        foreach (var relative in new[] { Path.Combine("Default", "Network", "Cookies"), Path.Combine("Default", "Cookies") })
        {
            var path = Path.Combine(profileFolder, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Test seam: runs after a copy is made and before it is checked against the original (the harness changes the original here, as Chrome would).</summary>
    internal Action? AfterCopy { get; init; }

    /// <summary>The folder inside a profile where copies are made; anything left in it by a read that was cut short is removed at the server's start.</summary>
    internal const string CopyFolder = ".cookie-read";

    /// <summary>Removes copies a cut-short read left behind (each is a database of the profile's encrypted cookies, in the profile's own private folder).</summary>
    internal static void Sweep(string profileFolder)
    {
        try { var folder = Path.Combine(profileFolder, CopyFolder); if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }

    // The database, its journal and its write-ahead log, as one picture of Chrome's file: stamped before and after the copy, and taken again if Chrome wrote meanwhile.
    private void CopyConsistent(string source, string copy)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = Stamp(source);
            // A journal or log an earlier attempt copied, which Chrome has since removed, must not stay beside this attempt's database: SQLite would roll the newer copy
            // back with it (code review 2026-10-11).
            foreach (var suffix in new[] { "-journal", "-wal", "-shm" }) File.Delete(copy + suffix);
            CopyShared(source, copy);
            foreach (var suffix in new[] { "-journal", "-wal" })
                if (File.Exists(source + suffix)) CopyShared(source + suffix, copy + suffix);
            AfterCopy?.Invoke();
            if (Stamp(source) == before) return;
        }
        throw new IOException("cookies_changing");
    }

    private static string Stamp(string source)
    {
        var parts = new List<string>();
        foreach (var path in new[] { source, source + "-journal", source + "-wal" })
        {
            var info = new FileInfo(path);
            parts.Add(info.Exists ? $"{info.Length}/{info.LastWriteTimeUtc.Ticks}" : "-");
        }
        return string.Join("|", parts);
    }

    // The store's servers for a service, each with and without its dot: constants of this server's own, never anything read from a page or a client.
    internal static string HostList(ChromiumRenewalTarget target) => string.Join(", ", target.StoreHosts.SelectMany(host => new[] { "'." + host + "'", "'" + host + "'" }));

    private ChromeCookieRead ReadCopy(string source, string profileFolder, ChromiumRenewalTarget target)
    {
        var folder = Path.Combine(profileFolder, CopyFolder);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var copy = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        try
        {
            CopyConsistent(source, copy);
            var builder = new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false, Mode = SqliteOpenMode.ReadWrite };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            var version = Version(connection);
            var cookies = new Dictionary<string, string>(StringComparer.Ordinal);
            var sidtsLifetime = "none";
            var now = DateTimeOffset.UtcNow;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT host_key, name, value, encrypted_value, expires_utc, has_expires FROM cookies WHERE host_key IN (" + HostList(target) + ")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var host = reader.GetString(0);
                var name = reader.GetString(1);
                // The names the service takes at all; which host each is accepted from is ReadCookie's below.
                if (target.CookieDomain(name) is null) continue;
                var expires = reader.GetInt64(4);
                var session = reader.GetInt64(5) == 0;
                var expiresSeconds = session ? -1 : expires / 1_000_000.0 - 11_644_473_600.0;
                // Expired in the store: not a cookie any more.
                if (!session && DateTimeOffset.FromUnixTimeMilliseconds((long)(expiresSeconds * 1000)) < now) continue;
                var value = reader.GetString(2);
                if (value.Length == 0 && reader[3] is byte[] encrypted && encrypted.Length > 0) value = Decrypt(encrypted, host, version) ?? "";
                if (value.Length == 0) continue;
                if (target.ReadCookie(name, host, value) is not { } accepted) continue;
                cookies[name] = accepted;
                if (name == "__Secure-1PSIDTS") sidtsLifetime = GoogleRefreshResult.Lifetime(session, expiresSeconds, now);
            }
            // Messages' read carries no target: it is what a read always was, and a fake that makes one says nothing of it either.
            return new ChromeCookieRead(cookies, sidtsLifetime, ReferenceEquals(target, ChromiumRenewalTarget.Messages) ? null : target);
        }
        finally
        {
            // The connection is closed already (it is not pooled); nothing process-wide is touched.
            foreach (var leftover in new[] { copy, copy + "-journal", copy + "-wal", copy + "-shm" }) { try { File.Delete(leftover); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { } }
        }
    }

    // Chrome has the file open; it is read with sharing allowed and written to a file of the copy's own.
    private static void CopyShared(string from, string to)
    {
        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var output = new FileStream(to, options);
        input.CopyTo(output);
    }

    private static int Version(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'version'";
        return command.ExecuteScalar() is { } value && int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var parsed) ? parsed : 0;
    }

    /// <summary>The plaintext of one encrypted value, or null when it is not one this key made (no <c>v10</c> prefix), does not decrypt, or fails its domain check.</summary>
    internal static string? Decrypt(byte[] encrypted, string hostKey, int databaseVersion)
    {
        // Only v10: a v11 value under the wrong key can still unpad by chance, and before version 24 there is no domain hash to catch the garbage it decrypts to.
        if (encrypted.Length < 3 + 16 || encrypted[0] != (byte)'v' || encrypted[1] != (byte)'1' || encrypted[2] != (byte)'0') return null;
        byte[] plain;
        try
        {
            using var aes = Aes.Create();
            aes.Key = Key;
            plain = aes.DecryptCbc(encrypted.AsSpan(3), Iv, PaddingMode.PKCS7);
        }
        catch (CryptographicException) { return null; }
        try
        {
            var start = 0;
            if (databaseVersion >= 24)
            {
                // The domain binding: the first 32 bytes are SHA-256 of the host_key, so a value cannot be moved to another cookie. Both spellings of the host are accepted.
                if (plain.Length < 32) return null;
                var hash = plain.AsSpan(0, 32);
                if (!hash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(hostKey))) && !hash.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(hostKey.TrimStart('.'))))) return null;
                start = 32;
            }
            return Encoding.UTF8.GetString(plain, start, plain.Length - start);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
