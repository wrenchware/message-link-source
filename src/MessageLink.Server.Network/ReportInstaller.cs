using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace MessageLink.Server.Network;

/// <summary>
/// What the setup command downloads (integrations/hermes-messagelink/setup): <c>install.py</c> with the report script, its skill and the Hermes plugin built in
/// as one base64 zip, and <c>install.sh</c>, the host half for Linux and macOS, with that Python after its last line. The command names each file's SHA-256, so
/// one check covers the installer and every file it places; nothing else is downloaded. Built once from the copies in the server, with Unix line endings
/// whatever the checkout had, so every server of one build serves the same bytes.
/// </summary>
internal static class ReportInstaller
{
    internal const string ShellName = "install.sh", PythonName = "install.py";

    // The files the installer places, by the name it knows them by (its PLACES).
    internal static readonly string[] BundleFiles = ["ml-report", "mlreport_client.py", "SKILL.md", "plugin.yaml", "__init__.py", "adapter.py", "README.md", "LICENSE"];

    private const string BundleLine = "BUNDLE = None\n";
    private const string Marker = "#__ML_SETUP_PY__\n";

    private static readonly Lazy<(byte[] Shell, byte[] Python)> Built = new(Build);

    internal static byte[] Shell => Built.Value.Shell;
    internal static byte[] Python => Built.Value.Python;
    internal static string ShellSha256 => Convert.ToHexStringLower(SHA256.HashData(Shell));
    internal static string PythonSha256 => Convert.ToHexStringLower(SHA256.HashData(Python));

    /// <summary>curl's <c>--pinnedpubkey</c> value for a certificate: the base64 SHA-256 of its public key's SubjectPublicKeyInfo.</summary>
    internal static string PublicKeyPin(X509Certificate2 certificate) =>
        "sha256//" + Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    internal static byte[] Bundle()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in BundleFiles)
            {
                var bytes = Resource("hermes-messagelink/" + name) ?? throw new InvalidOperationException("The server is missing " + name + ".");
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var target = entry.Open();
                target.Write(bytes);
            }
        return output.ToArray();
    }

    private static (byte[], byte[]) Build()
    {
        var python = Text("hermes-messagelink/setup/" + PythonName);
        // Exactly one line is replaced, at the start of a line: a copy that lost it would otherwise be served with no files in it.
        var at = python.IndexOf("\n" + BundleLine, StringComparison.Ordinal);
        if (at < 0 || python.IndexOf("\n" + BundleLine, at + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException("install.py's BUNDLE line is missing.");
        python = python[..(at + 1)] + "BUNDLE = \"" + Convert.ToBase64String(Bundle()) + "\"\n" + python[(at + 1 + BundleLine.Length)..];
        var shell = Text("hermes-messagelink/setup/" + ShellName);
        if (!shell.EndsWith("\n" + Marker, StringComparison.Ordinal)) throw new InvalidOperationException("install.sh doesn't end with its marker line.");
        return (Encoding.UTF8.GetBytes(shell + python), Encoding.UTF8.GetBytes(python));
    }

    private static string Text(string name) =>
        Encoding.UTF8.GetString(Resource(name) ?? throw new InvalidOperationException("The server is missing " + name + ".")).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static byte[]? Resource(string name)
    {
        using var source = typeof(ReportInstaller).Assembly.GetManifestResourceStream(name);
        if (source is null) return null;
        using var copy = new MemoryStream();
        source.CopyTo(copy);
        return copy.ToArray();
    }
}
