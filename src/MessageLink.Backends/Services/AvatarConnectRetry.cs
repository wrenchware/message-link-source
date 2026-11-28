namespace MessageLink.App.Services;

// Avatars asked for before their service had connected. That answer is no answer about the picture, but a row took it as
// one: it drew initials, asked once more six seconds later and then never again that session, and Messenger takes several
// seconds to connect at a launch, so its rows kept their initials until the next one (2026-10-02). Now such an avatar
// waits for its own service's Connected and asks once then: one request per avatar per connect, Instagram's included,
// paced by the caller (ChatAvatar).
internal static class AvatarConnectRetry
{
    internal const string Google = "google-direct:", Messenger = "messenger:", Instagram = "instagram:", Voice = "gvoice:";

    // The service an avatar waits for, from its conversation id or, for a member's face, from its source; every Google
    // Voice account is one. Null where nothing names a service.
    internal static string? Service(string? chatId, string? source)
    {
        if (chatId is not null)
        {
            foreach (var service in new[] { Google, Messenger, Instagram, Voice })
                if (chatId.StartsWith(service, StringComparison.Ordinal)) return service;
        }
        if (source is null) return null;
        // Google names its avatars apart from its media.
        if (source.StartsWith("google-avatar:", StringComparison.Ordinal)) return Google;
        return ProviderMedia.For(source)?.Diagnostic switch
        {
            "google" => Google,
            "messenger" => Messenger,
            "instagram" => Instagram,
            "voice" => Voice,
            _ => null,
        };
    }

    // The service a connection scope belongs to: a Google Voice account's scope names the account as well.
    internal static string ServiceOf(string scope) => scope.StartsWith(Voice, StringComparison.Ordinal) ? Voice : scope;

    // What a log line calls the service. Never the scope itself, which for Voice carries an account key.
    internal static string Diagnostic(string? service) => service switch
    {
        Google => "google",
        Messenger => "messenger",
        Instagram => "instagram",
        Voice => "voice",
        _ => "other",
    };
}

// Who is waiting for which service. Held weakly: a row that is gone must not be kept alive by waiting (a timer or an event
// handler holding its owner was the 0.1.312 leak of every conversation view). The interface thread's alone in the app;
// locked anyway, because the harness drives it from wherever.
internal sealed class AvatarConnectWaits<T> where T : class
{
    // The most asked of a service at one connect; any beyond wait for the next. More rows than this are never realised
    // at once, so it only bounds a list gone wrong.
    internal const int PerConnect = 200;
    private readonly object gate = new();
    private long stamp;
    private readonly Dictionary<string, long> connected = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(WeakReference<T> Item, int Token)>> waiting = new(StringComparer.Ordinal);

    // Read before an avatar asks its service and handed back with the waiting answer, so a connect landing between the
    // question and the answer is not missed.
    internal long Stamp { get { lock (gate) return stamp; } }

    // False: kept until the service next connects. True: it connected after `asked`, so the caller asks again now and
    // nothing is kept. Waiting again replaces an avatar's token rather than adding a second entry.
    internal bool Wait(T item, string service, long asked, int token)
    {
        lock (gate)
        {
            if (connected.TryGetValue(service, out var at) && at > asked) return true;
            if (!waiting.TryGetValue(service, out var list)) waiting[service] = list = [];
            list.RemoveAll(entry => !entry.Item.TryGetTarget(out var held) || ReferenceEquals(held, item));
            list.Add((new(item), token));
            return false;
        }
    }

    // The service connected: everything still alive that waited for it, each once, oldest first and at most PerConnect;
    // the rest keep waiting.
    internal IReadOnlyList<(T Item, int Token)> Connected(string scope)
    {
        var service = AvatarConnectRetry.ServiceOf(scope);
        lock (gate)
        {
            connected[service] = ++stamp;
            if (!waiting.Remove(service, out var list)) return [];
            var due = new List<(T Item, int Token)>();
            var later = new List<(WeakReference<T> Item, int Token)>();
            foreach (var entry in list)
            {
                if (!entry.Item.TryGetTarget(out var item)) continue;
                if (due.Count < PerConnect) due.Add((item, entry.Token));
                else later.Add(entry);
            }
            if (later.Count > 0) waiting[service] = later;
            return due;
        }
    }

    internal int Waiting(string service)
    {
        lock (gate) return waiting.TryGetValue(service, out var list) ? list.Count(entry => entry.Item.TryGetTarget(out _)) : 0;
    }
}
