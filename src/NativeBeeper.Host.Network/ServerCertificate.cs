using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>
/// The host's TLS certificate (SWITCHBOARD.md stage 4): self-signed, ECDSA P-256, made on the first start and kept with its private key in
/// <c>&lt;data&gt;/tls/</c> (<c>server.crt</c> and <c>server.key</c>, PEM; the folder 0700 and the files 0600 off Windows). Every later start reuses it, so
/// the fingerprint a client pinned stays good; it changes only when the owner deletes the files. Each start logs
/// <c>server tls fingerprint sha256=&lt;hex&gt;</c> (not a secret: it is what the owner pins). The key is never logged.
/// </summary>
internal static class ServerCertificate
{
    public const string FolderName = "tls";
    public const string CertificateFile = "server.crt";
    public const string KeyFile = "server.key";
    public const string Subject = "CN=Message Link Server";

    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// The certificate with its key, made first if there is none. <paramref name="hostNames"/> (names and addresses) and the machine's name go in a new
    /// certificate's subject alternative names; an existing one is never remade to add them, because that would change its fingerprint. A certificate
    /// with no key beside it is refused rather than replaced: the owner deletes the folder to rotate, and every client pins the new fingerprint.
    /// With <paramref name="seal"/> (the Windows server, SWITCHBOARD.md 10.7) the key file holds the PEM sealed with it (DPAPI, to the server's user), and a plain
    /// one found there is sealed in place; without it (Linux) the key is plain PEM in the keys volume, as before.
    /// </summary>
    public static X509Certificate2 LoadOrCreate(string dataFolder, IEnumerable<string> hostNames, Action<string> log, NativeBeeper.Core.Services.IStateProtector? seal = null)
    {
        var folder = Path.Combine(dataFolder, FolderName);
        var certificatePath = Path.Combine(folder, CertificateFile);
        var keyPath = Path.Combine(folder, KeyFile);
        if (!File.Exists(certificatePath))
        {
            // A key with no certificate is what a start that died between the two writes leaves (the key is written first). Nobody can have pinned a
            // certificate that was never written, so both are made again.
            Create(folder, certificatePath, keyPath, hostNames, seal);
            log("server tls created");
        }
        else if (!File.Exists(keyPath))
        {
            log("server tls refused reason=no_key");
            throw new InvalidDataException("The server's certificate has no key beside it. Delete the tls folder to make a new one, and pin the new fingerprint on every device.");
        }
        var certificate = Load(certificatePath, keyPath, seal, log);
        log($"server tls fingerprint sha256={NetworkEndpoint.Fingerprint(certificate)}");
        return certificate;
    }

    private static X509Certificate2 Load(string certificatePath, string keyPath, NativeBeeper.Core.Services.IStateProtector? seal, Action<string> log)
    {
        var stored = File.ReadAllBytes(keyPath);
        string keyPem;
        if (seal is null || stored.AsSpan().StartsWith("-----BEGIN"u8))
        {
            keyPem = System.Text.Encoding.UTF8.GetString(stored);
            // A plain key where the host seals them (written before it did, or put there by hand): sealed in place now.
            if (seal is not null)
            {
                WritePrivate(keyPath, seal.ProtectAsync(stored).GetAwaiter().GetResult());
                log("server tls key sealed");
            }
        }
        else
        {
            var opened = seal.UnprotectAsync(stored).GetAwaiter().GetResult();
            try { keyPem = System.Text.Encoding.UTF8.GetString(opened); }
            finally { CryptographicOperations.ZeroMemory(opened); }
        }
        CryptographicOperations.ZeroMemory(stored);
        using var pem = X509Certificate2.CreateFromPem(File.ReadAllText(certificatePath), keyPem);
        // SChannel (Windows) cannot serve TLS from a key that lives only in memory, which is what a PEM gives. Through PKCS#12 the key gets a container of its
        // own, which goes when the certificate is disposed (no PersistKeySet); off Windows the same round trip is merely harmless.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }

    private static void Create(string folder, string certificatePath, string keyPath, IEnumerable<string> hostNames, NativeBeeper.Core.Services.IStateProtector? seal)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, PrivateFolder);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(AlternativeNames(hostNames));
        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem());
        WritePrivate(keyPath, seal is null ? keyBytes : seal.ProtectAsync(keyBytes).GetAwaiter().GetResult());
        CryptographicOperations.ZeroMemory(keyBytes);
        WritePrivate(certificatePath, System.Text.Encoding.UTF8.GetBytes(certificate.ExportCertificatePem()));
    }

    /// <summary>The configured names and addresses and the machine's name, each once; what is neither a host name nor an address is left out.</summary>
    internal static X509Extension AlternativeNames(IEnumerable<string> hostNames)
    {
        var builder = new SubjectAlternativeNameBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in hostNames.Append(Environment.MachineName))
        {
            var name = raw?.Trim() ?? "";
            if (name.Length == 0 || !seen.Add(name)) continue;
            if (IPAddress.TryParse(name, out var address)) builder.AddIpAddress(address);
            else if (Uri.CheckHostName(name) == UriHostNameType.Dns) builder.AddDnsName(name);
        }
        return builder.Build();
    }

    // Written whole to a temporary file that only the owner can read, then moved into place.
    private static void WritePrivate(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
        using (var stream = new FileStream(temporary, options))
            stream.Write(bytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, PrivateFile);
        File.Move(temporary, path, true);
    }
}
