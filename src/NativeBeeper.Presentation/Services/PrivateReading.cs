namespace NativeBeeper.Presentation.Services;

/// <summary>The four services private reading is chosen for, one switch each.</summary>
public enum ReadService { GoogleMessages, Messenger, Instagram, GoogleVoice }

/// <summary>
/// Private reading (Settings → Private reading): for each service, whether reading a conversation here tells that service.
/// Off by default, which is exactly how reading worked before. With a service on, opening and reading its conversations
/// sends no read marker and the composer sends no typing; the conversation is read on this PC only
/// (<see cref="ReadHereMarks"/>), so the phone and the other person still see it unread. What the user asks for by name —
/// a notification's Mark read, the conversation menu's Send read receipt, the console's /read — still tells the service.
/// The rule itself is enforced in one place, <see cref="PrivateReadingGate"/>; this holds the choice and the counts.
/// </summary>
/// <remarks>Thread-safe: reads happen on helper and pool threads as well as the interface thread.</remarks>
public sealed class PrivateReading
{
    private readonly object gate = new();
    private readonly HashSet<ReadService> on;
    private readonly Action<string>? log;
    private volatile Action<IReadOnlyCollection<ReadService>>? save;
    private volatile bool followed;
    private long withheldReads, withheldTyping, explicitReads, refused;

    /// <param name="saved">The services chosen on, as saved; null or empty is every service off.</param>
    /// <param name="log">Where the choice and a refused call are written (app.log in the app); ids never reach it.</param>
    /// <param name="save">Told the services now on after each change, to keep the choice.</param>
    public PrivateReading(IEnumerable<ReadService>? saved = null, Action<string>? log = null, Action<IReadOnlyCollection<ReadService>>? save = null)
    {
        on = [.. saved ?? []];
        this.log = log;
        this.save = save;
    }

    /// <summary>Raised after a service is turned on or off.</summary>
    public event EventHandler? Changed;

    public static IReadOnlyList<ReadService> All { get; } = [ReadService.GoogleMessages, ReadService.Messenger, ReadService.Instagram, ReadService.GoogleVoice];

    /// <summary>
    /// The service a conversation id belongs to, by the prefixes every backend qualifies its ids with. Anything else is
    /// held to Google Messages' choice: the server refuses such an id (ConversationOwners), so a read of it reaches no
    /// service either way, and the gate still decides before the refusal, as it did.
    /// </summary>
    public static ReadService Of(string chatId) =>
        chatId.StartsWith("gvoice:", StringComparison.Ordinal) ? ReadService.GoogleVoice
        : chatId.StartsWith("instagram:", StringComparison.Ordinal) ? ReadService.Instagram
        : chatId.StartsWith("messenger:", StringComparison.Ordinal) ? ReadService.Messenger
        : ReadService.GoogleMessages;

    /// <summary>The word the log and the saved preference use for a service.</summary>
    public static string Word(ReadService service) => service switch
    {
        ReadService.Messenger => "messenger",
        ReadService.Instagram => "instagram",
        ReadService.GoogleVoice => "google-voice",
        _ => "google-messages",
    };

    public static ReadService? Parse(string? word) => All.Cast<ReadService?>().FirstOrDefault(service => Word(service!.Value) == word);

    /// <summary>The service's name as Settings shows it.</summary>
    public static string Name(ReadService service) => service switch
    {
        ReadService.Messenger => "Messenger",
        ReadService.Instagram => "Instagram",
        ReadService.GoogleVoice => "Google Voice",
        _ => "Google Messages",
    };

    public bool IsOn(ReadService service) { lock (gate) return on.Contains(service); }
    // Hermes keeps its read state on its host and tells no service, so there is nothing to hold back; Of files an id it does not know
    // under Google Messages, and Google's switch must not gate a Hermes thread.
    public bool IsOn(string chatId) => !NativeBeeper.Core.Services.HermesIds.Owns(chatId) && IsOn(Of(chatId));
    public bool AnyOn { get { lock (gate) return on.Count > 0; } }

    public void Set(ReadService service, bool value)
    {
        ReadService[] now;
        lock (gate)
        {
            if (!(value ? on.Add(service) : on.Remove(service))) return;
            now = [.. on];
        }
        log?.Invoke($"private-reading service={Word(service)} {(value ? "on" : "off")}");
        save?.Invoke(now);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Whether something else holds the choice (<see cref="Follow"/>): a client of a Messages Server whose choice is shared with its other devices. The saved
    /// choice of this app (app-preferences.json) then neither receives a change made here nor sets what runs.
    /// </summary>
    public bool Followed => followed;

    /// <summary>
    /// Hands the choice to something that decides it from elsewhere (SharedPrivateReading): a change made with <see cref="Set"/> goes to
    /// <paramref name="saveInstead"/> and not to the saver this was made with, and what is on is set by <see cref="Apply"/> alone.
    /// </summary>
    public void Follow(Action<IReadOnlyCollection<ReadService>> saveInstead)
    {
        save = saveInstead;
        followed = true;
    }

    /// <summary>
    /// Makes exactly these services the ones that are on, for whatever holds the choice (<see cref="Follow"/>): nothing is saved, and a change is logged with
    /// where it came from (<paramref name="source"/>, a fixed word) and raised once. Unlike <see cref="Set"/> this is not a person's change.
    /// </summary>
    public void Apply(IEnumerable<ReadService> now, string source)
    {
        var wanted = new HashSet<ReadService>(now);
        List<(ReadService Service, bool Value)> changes = [];
        lock (gate)
        {
            foreach (var service in All)
            {
                var value = wanted.Contains(service);
                if (value ? on.Add(service) : on.Remove(service)) changes.Add((service, value));
            }
        }
        if (changes.Count == 0) return;
        foreach (var (service, value) in changes) log?.Invoke($"private-reading service={Word(service)} {(value ? "on" : "off")} source={source}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One line per service that is on, for the start of a run's log; nothing when every service is off.</summary>
    public void LogState(string when)
    {
        foreach (var service in All.Where(IsOn)) log?.Invoke($"private-reading service={Word(service)} on at={when}");
    }

    internal void WithheldRead() => Interlocked.Increment(ref withheldReads);
    internal void WithheldTyping() => Interlocked.Increment(ref withheldTyping);
    internal void SentExplicitly() => Interlocked.Increment(ref explicitReads);
    internal void Refused(string kind, ReadService service)
    {
        // A call that reached a service without passing the gate is a path this build does not know about: loud, and once
        // per call so the log shows how often.
        Interlocked.Increment(ref refused);
        log?.Invoke($"private-reading refused kind={kind} service={Word(service)} path=ungated");
    }

    public long WithheldReads => Interlocked.Read(ref withheldReads);
    public long WithheldTypingCount => Interlocked.Read(ref withheldTyping);
    public long ExplicitReads => Interlocked.Read(ref explicitReads);
    public long RefusedCalls => Interlocked.Read(ref refused);

    /// <summary>
    /// This run's counts, for the end of the activity summary line: which services are on, how many read markers and
    /// typing starts were kept from a service, how many reads were sent because the user asked, and how many ungated calls
    /// were refused. Empty while nothing is on and nothing was ever counted, so the line reads as it always did.
    /// </summary>
    public string Summary()
    {
        string services;
        lock (gate) services = string.Join("|", All.Where(on.Contains).Select(Word));
        if (services.Length == 0 && WithheldReads == 0 && WithheldTypingCount == 0 && ExplicitReads == 0 && RefusedCalls == 0) return "";
        return $" private_reading={(services.Length == 0 ? "none" : services)} reads_withheld={WithheldReads} typing_withheld={WithheldTypingCount} " +
            $"reads_explicit={ExplicitReads} refused={RefusedCalls}";
    }
}
