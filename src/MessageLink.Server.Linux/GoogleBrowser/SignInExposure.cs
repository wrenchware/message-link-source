using System.Security.Cryptography;

namespace MessageLink.Server.Linux;

/// <summary>
/// The sign-in's display key. The display the person's browser draws on is closed to every client that has no key: Xvfb is started with <c>-auth</c> on a file in the
/// browser's own 0700 profile folder (<see cref="ChromiumLauncher.SignInScript"/>), holding one MIT-MAGIC-COOKIE-1 of 16 random bytes made for the run, and the browser is told the file by
/// XAUTHORITY. The entry is for any address and any display number (family 0xFFFF, both empty), because the display number is only known once Xvfb has picked it. The cookie is never
/// logged, and the file is deleted when the run ends. Residual: other processes of the server's own account (the helpers, a compromised renderer under --no-sandbox) can read this
/// folder and so the key, and the X server also admits its own account's local clients; the key keeps out every other account and every client that is not local.
/// </summary>
internal static class XAuthority
{
    private const string Method = "MIT-MAGIC-COOKIE-1";

    /// <summary>The bytes of an Xauthority file holding <paramref name="cookie"/> for any display.</summary>
    public static byte[] Entry(ReadOnlySpan<byte> cookie)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(Method);
        var entry = new List<byte>(14 + name.Length + cookie.Length);
        void Short(int value) { entry.Add((byte)(value >> 8)); entry.Add((byte)value); }
        Short(0xFFFF);          // family: wild
        Short(0);               // address: any
        Short(0);               // display number: any
        Short(name.Length);
        entry.AddRange(name);
        Short(cookie.Length);
        entry.AddRange(cookie.ToArray());
        return [.. entry];
    }

    /// <summary>Writes a new random key to <paramref name="path"/>, readable and writable by this account alone from the first byte.</summary>
    public static void Write(string path)
    {
        var cookie = RandomNumberGenerator.GetBytes(16);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var file = new FileStream(path, options);
            file.Write(Entry(cookie));
        }
        finally { CryptographicOperations.ZeroMemory(cookie); }
    }

    /// <summary>What a key file holds that a client needs to authenticate: the method's name and the secret. Never printed.</summary>
    public readonly record struct Credential(byte[] Method, byte[] Cookie)
    {
        public override string ToString() => nameof(Credential);
    }

    /// <summary>The first entry of a key file written by <see cref="Write"/> (or by <see cref="Entry"/>), or null when the file is missing or is not one.</summary>
    public static Credential? ReadEntry(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return null; }
        try
        {
            var at = 0;
            bool Short(out int value)
            {
                value = 0;
                if (at + 2 > bytes.Length) return false;
                value = bytes[at] << 8 | bytes[at + 1];
                at += 2;
                return true;
            }
            bool Chunk(out byte[] chunk)
            {
                chunk = [];
                if (!Short(out var length) || at + length > bytes.Length) return false;
                chunk = bytes.AsSpan(at, length).ToArray();
                at += length;
                return true;
            }
            // family, address, display number, method, secret.
            if (!Short(out _) || !Chunk(out _) || !Chunk(out _) || !Chunk(out var method) || !Chunk(out var cookie)) return null;
            return method.Length == 0 || cookie.Length == 0 ? null : new Credential(method, cookie);
        }
        finally { Array.Clear(bytes); }
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The one guard every Google sign-in on the server runs under (remote, or the legacy cookie hand-over), from its first step to the kept profile and the pairing it commits: no two
/// interleave, so the profile the renewals use is always the one whose session was committed last.
/// </summary>
internal sealed class SignInGuard
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The lease, or null when another sign-in holds the guard.</summary>
    public IDisposable? TryEnter() => gate.Wait(0) ? new Lease(gate) : null;

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }
}

/// <summary>
/// What a browser keeps of a page it has shown that a kept profile does not need. A real Chrome, measured by the smoke run (<c>--chromium-signin-smoke</c>), keeps
/// the text typed into a form field in its saved session (<c>Default/Sessions</c>, for restoring the tab) even with autofill and password saving off in <c>Preferences</c>; its history,
/// autofill, saved-login, top-sites and address-prediction stores could hold an address or a typed value too. After the browser has closed, before the profile is kept, these are removed.
/// The cookie store, <c>Local State</c> and <c>Preferences</c> (all a renewal loads) stay. Deleting is best effort: a file that cannot be deleted is left, and the profile is still private to
/// the server's account on the keys volume.
/// </summary>
internal static class ProfileScrub
{
    private static readonly string[] FilePrefixes =
        ["History", "Web Data", "Login Data", "Top Sites", "Visited Links", "Shortcuts", "Network Action Predictor", "Favicons", "Last Session", "Last Tabs", "Current Session", "Current Tabs", "Affiliation Database"];

    private static bool Stored(string name) => FilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>
    /// Removes every store on its own (one that cannot be deleted does not stop the others), then looks again and says whether none is left. False means the profile may still hold typed form
    /// state, or that it could not be looked at to tell: the caller does not keep it.
    /// </summary>
    public static bool Run(string profileFolder)
    {
        var folder = Path.Combine(profileFolder, "Default");
        var sessions = Path.Combine(folder, "Sessions");
        Attempt(() => { if (Directory.Exists(sessions)) Directory.Delete(sessions, true); });
        List<string> files;
        try { files = Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder).Where(file => Stored(Path.GetFileName(file)))] : []; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
        foreach (var file in files) Attempt(() => File.Delete(file));
        try { return !Directory.Exists(sessions) && !(Directory.Exists(folder) && Directory.EnumerateFiles(folder).Any(file => Stored(Path.GetFileName(file)))); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Attempt(Action delete)
    {
        try { delete(); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Whether the server shares its network namespace with the machine it runs on (<c>docker run --network host</c>). The sign-in's browser listens for DevTools on 127.0.0.1, which in a
/// container of its own is reachable only from that container; on the host's it is reachable from every process on the machine, and a process that reads <c>/json/version</c> can drive the
/// page the person is typing a password into. So the remote sign-in is refused there (<c>server google remote_signin refused reason=host_network</c>) unless
/// <c>MESSAGES_REMOTE_SIGNIN_HOST_NETWORK=1</c> says it is wanted anyway. It is also offered only on positive proof of a namespace of its own (<c>MESSAGES_NETWORK_ISOLATED=1</c> from the install
/// script, and interfaces that could be read and show no host bridge), else <c>network_unproven</c>. The interfaces in view tell the host's namespace: a container on a bridge sees <c>lo</c> and <c>eth0</c>; the host's namespace has
/// the bridges and virtual pairs the container runtime makes (<c>docker0</c>, <c>br-…</c>, <c>veth…</c>) and the machine's own bridge (<c>br0</c>, <c>virbr0</c>).
/// </summary>
internal static class HostNetwork
{
    private static readonly string[] HostOnly = ["docker", "br", "veth", "virbr", "cni", "flannel", "cali", "podman"];

    public static bool Looks(IEnumerable<string> interfaces) =>
        interfaces.Any(name => HostOnly.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));

    /// <summary>The interface names in view, or empty when they cannot be listed (then nothing is claimed).</summary>
    public static IReadOnlyList<string> Interfaces()
    {
        try { return Directory.Exists("/sys/class/net") ? [.. Directory.EnumerateDirectories("/sys/class/net").Select(Path.GetFileName).OfType<string>()] : []; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return []; }
    }
}
