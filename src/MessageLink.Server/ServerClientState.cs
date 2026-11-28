using System.Text.Json;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Server;

/// <summary>
/// What the server knows of its clients (<c>client.state</c>): whether anybody is looking, whether a dialog is open, which
/// services are read privately and whether link previews may be fetched. The host's own code reads these here instead of from the window's
/// objects: <see cref="IAppPresence"/> for Instagram's poll, <see cref="IClientBusy"/> for the Google sign-in keeper, <see cref="Reading"/> for
/// the routers' private-reading safety net and <see cref="LinkPreviews"/> for the cards the host fetches.
/// </summary>
/// <remarks>
/// <para><b>Combined, fail closed.</b> Over the connections that have stated something: present and busy if any is; a service is read privately if
/// any connection says so; link previews are allowed only if every connection allows them. Each rule takes the more restrictive answer, and a
/// connection that has said nothing yet adds nothing (it cannot be asked), so a client that never states anything never loosens what another said.</para>
/// <para><b>With no connection.</b> Presence and busy are false: they describe a person at a window, and one who is not connected is not looking, so a
/// headless server must not poll Instagram as though somebody were (that is more traffic on a real account, never less). The two preferences keep the
/// last combined answer, and that answer is saved (protected, small, results only) so a headless server that restarts begins from it. Never known:
/// every service private and link previews off, the most restrictive answers.</para>
/// <para>Where the client and the server share a process the client states its truth a moment after the server starts. Until then the server is
/// at the defaults, which only affect a call that skips the client's gate (refused as unadmitted) and a card fetched ahead (not fetched); the gate
/// and the settings stay the authority on the client.</para>
/// </remarks>
public sealed class ServerClientState : IClientStateSink, IAppPresence, IClientBusy
{
    private const long MaxBytes = 4096;

    private sealed record Stated(bool Presence, bool Busy, HashSet<ReadService> Private, bool LinkPreviews);

    /// <summary>What is saved: the two preferences. Words only; no conversation, person or number.</summary>
    private sealed record Remembered(IReadOnlyList<string> PrivateReading, bool LinkPreviews);

    private readonly object gate = new();
    private readonly Dictionary<object, Stated> clients = new(ReferenceEqualityComparer.Instance);
    private readonly string? path;
    private readonly IStateProtector? protector;
    private readonly Action<string>? log;
    private readonly PrivateReading reading;
    private bool present, busy, linkPreviews;
    private string? logged, saved, pendingText;
    private Remembered? pending;
    private Task writing = Task.CompletedTask;
    private bool writeActive;

    /// <param name="memoryPath">Where the last known preferences are kept, protected with <paramref name="protector"/>; null keeps nothing.</param>
    public ServerClientState(string? memoryPath = null, IStateProtector? protector = null, Action<string>? log = null)
    {
        path = memoryPath; this.protector = protector; this.log = log;
        var remembered = memoryPath is null || protector is null ? null : Load();
        linkPreviews = remembered?.LinkPreviews ?? false;
        reading = new PrivateReading(remembered is null ? PrivateReading.All : remembered.PrivateReading.Select(PrivateReading.Parse).OfType<ReadService>(),
            log is null ? null : line => log("host " + line));
        if (remembered is not null) saved = Describe(Words(reading), linkPreviews);
    }

    /// <summary>The server's private-reading choice, which the routers' safety net reads. Set only from what clients state.</summary>
    public PrivateReading Reading => reading;

    public bool Present { get { lock (gate) return present; } }
    public bool Busy { get { lock (gate) return busy; } }
    public bool LinkPreviews { get { lock (gate) return linkPreviews; } }

    /// <summary>How many connections have stated something.</summary>
    public int Clients { get { lock (gate) return clients.Count; } }

    /// <summary>Raised with the new answer, only when it changes. Serialized with every other change, so the order heard is the order made.</summary>
    public event Action<bool>? Changed;

    public void Apply(object connection, ClientStateParams state)
    {
        var stated = new Stated(state.Presence, state.Busy, [.. state.PrivateReading.Select(PrivateReading.Parse).OfType<ReadService>()], state.LinkPreviews);
        lock (gate)
        {
            clients[connection] = stated;
            Recompute();
        }
    }

    public void Disconnected(object connection)
    {
        lock (gate)
        {
            if (clients.Remove(connection)) Recompute();
        }
    }

    /// <summary>The combination of what the connections stated, or none when none has: the rule, apart from the server that holds it.</summary>
    internal static (bool Present, bool Busy, IReadOnlySet<ReadService> Private, bool LinkPreviews)? Combine(IEnumerable<(bool Presence, bool Busy, IReadOnlyCollection<ReadService> Private, bool LinkPreviews)> stated)
    {
        var any = false;
        bool present = false, busy = false, links = true;
        var services = new HashSet<ReadService>();
        foreach (var one in stated)
        {
            any = true;
            present |= one.Presence; busy |= one.Busy; links &= one.LinkPreviews;
            services.UnionWith(one.Private);
        }
        return any ? (present, busy, services, links) : null;
    }

    // Called with the gate held. Delivering Changed under it keeps two changes in the order they were made.
    private void Recompute()
    {
        var combined = Combine(clients.Values.Select(one => (one.Presence, one.Busy, (IReadOnlyCollection<ReadService>)one.Private, one.LinkPreviews)));
        var wasPresent = present;
        present = combined?.Present ?? false;
        busy = combined?.Busy ?? false;
        if (combined is { } known)
        {
            foreach (var service in PrivateReading.All) reading.Set(service, known.Private.Contains(service));
            linkPreviews = known.LinkPreviews;
        }
        var line = $"client state clients={clients.Count} present={(present ? "true" : "false")} busy={(busy ? "true" : "false")} " +
            $"private_reading={Describe(Words(reading))} link_previews={(linkPreviews ? "true" : "false")}";
        if (!string.Equals(line, logged, StringComparison.Ordinal))
        {
            logged = line;
            try { log?.Invoke(line); } catch (Exception) { /* a logger that fails must not fail the connection */ }
        }
        if (combined is not null) Remember();
        if (present != wasPresent) Changed?.Invoke(present);
    }

    // ---- The last known preferences ----

    private static string[] Words(PrivateReading reading) => [.. PrivateReading.All.Where(reading.IsOn).Select(PrivateReading.Word).Order(StringComparer.Ordinal)];

    private static string Describe(string[] words, bool links) => Describe(words) + "|" + (links ? "1" : "0");

    private static string Describe(string[] words) => words.Length == 0 ? "none" : string.Join("|", words);

    // Called with the gate held.
    private void Remember()
    {
        if (path is null || protector is null) return;
        var words = Words(reading);
        var text = Describe(words, linkPreviews);
        if (string.Equals(text, saved, StringComparison.Ordinal) && !writeActive) return;
        if (string.Equals(text, pendingText, StringComparison.Ordinal) && writeActive) return;
        pending = new Remembered(words, linkPreviews);
        pendingText = text;
        if (writeActive) return;
        writeActive = true;
        writing = Task.Run(SaveAsync);
    }

    /// <summary>Completes when what is to be saved has been written (or failed to be). For the harness and a clean stop.</summary>
    public Task SavedAsync()
    {
        lock (gate) return writing;
    }

    private async Task SaveAsync()
    {
        while (true)
        {
            Remembered next;
            string text;
            lock (gate)
            {
                if (pending is not { } taken) { writeActive = false; return; }
                next = taken; text = pendingText!;
                pending = null; pendingText = null;
            }
            if (string.Equals(text, Volatile.Read(ref saved), StringComparison.Ordinal)) continue;
            try
            {
                await WriteAsync(next).ConfigureAwait(false);
                Volatile.Write(ref saved, text);
            }
            catch (Exception)
            {
                try { log?.Invoke("client state memory save failed"); } catch (Exception) { /* a logger that fails must not fail the connection */ }
            }
        }
    }

    private Remembered? Load()
    {
        try
        {
            // The bound is only for a disk that never answers; a server that cannot read its memory starts at the most restrictive answers.
            var loading = Task.Run(async () =>
            {
                if (!File.Exists(path!) || new FileInfo(path!).Length > MaxBytes) return null;
                var plain = await protector!.UnprotectAsync(await File.ReadAllBytesAsync(path!).ConfigureAwait(false)).ConfigureAwait(false);
                try { return JsonSerializer.Deserialize<Remembered>(plain, ProtocolJson.Options); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
            });
            return loading.Wait(TimeSpan.FromSeconds(2)) && loading.Result is { PrivateReading: not null } remembered ? remembered : null;
        }
        catch (Exception) { return null; }
    }

    // Written beside the file and moved over it, so a crash leaves the old one whole.
    private async Task WriteAsync(Remembered value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, ProtocolJson.Options);
        try
        {
            var sealedBytes = await protector!.ProtectAsync(plain).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path!)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
                {
                    await file.WriteAsync(sealedBytes).ConfigureAwait(false);
                    file.Flush(true);
                }
                File.Move(temporary, path!, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }
}
