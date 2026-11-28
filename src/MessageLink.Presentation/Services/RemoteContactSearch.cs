using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// The rules for asking a provider to search its own directory, as opposed to matching what is already on this PC.
/// </summary>
/// <remarks>
/// Messenger and Instagram answer a search over the network, so every keystroke that reaches them is a real API call
/// against the user's own account. Searching all services at once multiplies that by however many are connected, and
/// Instagram's limits are not documented anywhere this project can check — being throttled there risks a checkpoint
/// on a real account, which is a far worse outcome than a picker that waits a moment. So remote searches are rare by
/// construction: only for a query long enough to mean something, only once typing has actually stopped, never twice
/// for the same words, and never more than one at a time per provider.
/// </remarks>
public static class RemoteSearchRules
{
    /// <summary>Below this, a query matches too much to be worth a round trip.</summary>
    public const int MinimumLength = 3;

    /// <summary>How long typing must stop before a remote provider is asked. Local matching does not wait.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(700);

    /// <summary>The least time between two searches of the same provider, whatever the user is doing.</summary>
    public static readonly TimeSpan PerProvider = TimeSpan.FromMilliseconds(900);

    public static bool Allows(string query) => query.Trim().Length >= MinimumLength;
}

/// <summary>
/// Remembers what each provider answered, so returning to a query already asked costs nothing, and holds each
/// provider to a minimum interval between calls.
/// </summary>
/// <remarks>
/// Keyed by account and by the exact trimmed, case-folded query. Nothing is inferred across queries: "dav" does not
/// answer for "dave", because a provider's own matching is not this code's to imitate.
/// </remarks>
public sealed class RemoteContactCache(int capacity = 32)
{
    private readonly record struct Entry(IReadOnlyList<ContactPerson> People, DateTimeOffset At);
    private readonly Dictionary<(string Account, string Query), Entry> entries = [];
    private readonly List<(string Account, string Query)> order = [];
    private readonly Dictionary<string, DateTimeOffset> lastCall = [];
    private readonly TimeSpan life = TimeSpan.FromMinutes(5);

    private static (string, string) KeyOf(string account, string query) => (account, query.Trim().ToLowerInvariant());

    /// <summary>What this provider last said about this query, while it is still worth believing.</summary>
    public IReadOnlyList<ContactPerson>? Lookup(string account, string query, DateTimeOffset now)
    {
        var key = KeyOf(account, query);
        if (!entries.TryGetValue(key, out var entry)) return null;
        if (now - entry.At > life) { entries.Remove(key); order.Remove(key); return null; }
        return entry.People;
    }

    public void Remember(string account, string query, IReadOnlyList<ContactPerson> people, DateTimeOffset now)
    {
        var key = KeyOf(account, query);
        if (entries.ContainsKey(key)) order.Remove(key);
        entries[key] = new(people, now);
        order.Add(key);
        // Oldest out first. A picker's history is short and the cost of a miss is one round trip.
        while (order.Count > capacity)
        {
            entries.Remove(order[0]);
            order.RemoveAt(0);
        }
    }

    /// <summary>Whether this provider may be asked again yet.</summary>
    public bool MayCall(string account, DateTimeOffset now) =>
        !lastCall.TryGetValue(account, out var at) || now - at >= RemoteSearchRules.PerProvider;

    public void Called(string account, DateTimeOffset now) => lastCall[account] = now;

    /// <summary>Forgets what was answered but not when the provider was last asked: a sign-out makes old answers somebody else's, and gives no new right to ask sooner.</summary>
    public void ForgetAnswers() { entries.Clear(); order.Clear(); }

    /// <summary>Forgets everything, for a sign-out or a connection change that makes old answers somebody else's.</summary>
    public void Clear() { entries.Clear(); order.Clear(); lastCall.Clear(); }
}
