namespace MessageLink.Server.Network;

/// <summary>
/// The pace of guesses at a secret: a pairing or setup code (<see cref="PairingCodes"/>) or the page's password (ServerPage). A secret is looked at at most once
/// per <see cref="GlobalSpacing"/> from anywhere, and each source waits <see cref="SourceSpacing"/> after a wrong try, doubling from the
/// <see cref="FreeWrong"/>th wrong one up to <see cref="LongestWait"/>. A try made too soon is not looked at, so it tells the peer nothing and changes no wait.
/// At most <see cref="MaxSources"/> sources are remembered; when that is full, forgotten ones make room, and a new source waits until there is room:
/// whoever filled it does not get to clear a waiting source's count by knocking from somewhere new. A source the table knows keeps its place.
/// </summary>
/// <remarks>The secrets differ only in what <see cref="PacingRules"/> says, so the rules sit side by side: when a source's count is forgotten, whether a try that
/// was right forgets the source, and whether it gives back the moment it claimed.</remarks>
internal sealed class TryPacing(TimeProvider time, PacingRules rules)
{
    /// <summary>The least time between two tries looked at, from any source.</summary>
    public static readonly TimeSpan GlobalSpacing = TimeSpan.FromSeconds(1);
    /// <summary>The least time a source waits after a wrong try, for its first <see cref="FreeWrong"/> wrong tries.</summary>
    public static readonly TimeSpan SourceSpacing = TimeSpan.FromSeconds(1);
    /// <summary>Wrong tries from one source before its wait starts doubling (a person mistyping gets these at the plain pace).</summary>
    public const int FreeWrong = 3;
    /// <summary>The longest a source is made to wait.</summary>
    public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(5);
    /// <summary>How many sources are remembered at once.</summary>
    public const int MaxSources = 1024;

    /// <summary>How long a source waits after its <paramref name="wrong"/>th wrong try.</summary>
    public static TimeSpan Wait(int wrong) =>
        wrong <= FreeWrong ? SourceSpacing : TimeSpan.FromTicks(Math.Min(LongestWait.Ticks, SourceSpacing.Ticks << Math.Min(wrong - FreeWrong, 20)));

    private sealed class Source
    {
        public int Wrong;
        public DateTimeOffset Next, Last;
    }

    /// <summary>A try being looked at: its source, when it was made, and the moment it claimed from everyone.</summary>
    public readonly record struct Claim(string Source, DateTimeOffset At, DateTimeOffset Until);

    private readonly object gate = new();
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);
    private DateTimeOffset nextAnywhere;

    /// <summary>How many sources are remembered now.</summary>
    public int Count
    {
        get { lock (gate) return sources.Count; }
    }

    /// <summary>Whether a try from <paramref name="source"/> may be looked at now. False: too soon, from anywhere or from it, or a new source while the table is
    /// full; nothing was looked at and nothing moved. True claims the moment, so tries sent together are one try and the rest are told to wait.</summary>
    public bool TryClaim(string source, out Claim claim)
    {
        var now = time.GetUtcNow();
        claim = default;
        lock (gate)
        {
            if (sources.TryGetValue(source, out var from))
            {
                if (Forgotten(from, now)) from.Wrong = 0;
            }
            else
            {
                if (sources.Count >= MaxSources)
                {
                    foreach (var stale in sources.Where(pair => Forgotten(pair.Value, now)).Select(pair => pair.Key).ToArray()) sources.Remove(stale);
                    if (sources.Count >= MaxSources) return false;
                }
                if (rules.RememberEveryTry) sources[source] = from = new Source { Last = now, Next = now };
            }
            if (now < nextAnywhere || from is not null && now < from.Next) return false;
            nextAnywhere = now + GlobalSpacing;
            if (from is not null) from.Last = now;
            claim = new(source, now, nextAnywhere);
            return true;
        }
    }

    /// <summary>The claimed try was wrong: its source waits longer.</summary>
    public void Wrong(Claim claim)
    {
        lock (gate)
        {
            if (!sources.TryGetValue(claim.Source, out var from)) sources[claim.Source] = from = new Source { Last = claim.At, Next = claim.At };
            else if (Forgotten(from, claim.At)) from.Wrong = 0;
            from.Wrong++;
            from.Next = claim.At + Wait(from.Wrong);
        }
    }

    /// <summary>The claimed try was right.</summary>
    public void Right(Claim claim)
    {
        lock (gate)
        {
            if (rules.ForgetOnRight) sources.Remove(claim.Source);
            // The owner's next step (a code just after signing in) does not wait on the moment this try claimed.
            if (rules.ReleaseOnRight && nextAnywhere == claim.Until) nextAnywhere = claim.At;
        }
    }

    private bool Forgotten(Source source, DateTimeOffset now) => rules.QuietFrom switch
    {
        QuietFrom.LastTry => now - source.Last > rules.Quiet && now >= source.Next,
        _ => now - source.Next >= rules.Quiet,
    };
}

/// <summary>Where a source's quiet is counted from before its wrong tries are forgotten.</summary>
internal enum QuietFrom
{
    /// <summary>Its last try looked at, once its wait is over too.</summary>
    LastTry,
    /// <summary>The end of its wait.</summary>
    WaitEnd,
}

/// <param name="Quiet">How long a source stays quiet before its wrong tries are forgotten, counted from <paramref name="QuietFrom"/>.</param>
/// <param name="RememberEveryTry">A source is remembered from its first try looked at (true), or only from its first wrong one.</param>
/// <param name="ForgetOnRight">A right try forgets its source's wrong ones.</param>
/// <param name="ReleaseOnRight">A right try gives back the moment it claimed from everyone.</param>
internal sealed record PacingRules(TimeSpan Quiet, QuietFrom QuietFrom, bool RememberEveryTry, bool ForgetOnRight, bool ReleaseOnRight)
{
    /// <summary>A pairing or setup code: a code is gone once taken, so nothing is given back; a source is forgotten when quiet for a code's lifetime.</summary>
    public static PacingRules Code { get; } = new(PairingCodes.Lifetime, QuietFrom.LastTry, RememberEveryTry: true, ForgetOnRight: false, ReleaseOnRight: false);

    /// <summary>The page's password: a source is remembered only once it is wrong, and forgotten once right or an hour past its wait (an owner who mistyped last
    /// week does not wait five minutes now).</summary>
    public static PacingRules Password { get; } = new(TimeSpan.FromHours(1), QuietFrom.WaitEnd, RememberEveryTry: false, ForgetOnRight: true, ReleaseOnRight: true);
}
