namespace MessageLink.Server.Network;

/// <summary>
/// When a signed-in browser must type the server password again before a step (a phone code, a removal, an account sign-in, the backup's password), and for how
/// long typing it counts. One answer both draws a step's form (with its field, or with none) and checks what that form sends, so the two cannot disagree.
/// </summary>
/// <para>
/// Not asked again: a session that typed the password a moment ago (<see cref="Grace"/>, or the longer window setup gives), and on the Windows server
/// (<see cref="DesktopTrust"/>) a signed-in browser on this PC, the PC's own user, whose session only the code from Message Link or the password could have made.
/// Signing in is always asked. In memory only, so a restart asks again.
/// </para>
internal sealed class PasswordAgain(TimeProvider time)
{
    /// <summary>The purpose a sign-in is checked under: never taken as given.</summary>
    public const string SignIn = "sign_in";

    /// <summary>The most sessions remembered as having typed the password a moment ago.</summary>
    public const int MaxSessions = 1024;

    /// <summary>What a step's form sent comes to.</summary>
    public enum Answer
    {
        /// <summary>Taken as given: whatever the field held (the form drew none), the step goes on.</summary>
        Given,
        /// <summary>Sent empty while it must be typed (a form drawn without the field, sent once the moment ran out): asked for, not a wrong try.</summary>
        Asked,
        /// <summary>Typed: checked as a password try.</summary>
        Check,
    }

    private readonly object gate = new();
    // Sessions that typed the password, and until when it stands for them.
    private readonly Dictionary<string, DateTimeOffset> typed = new(StringComparer.Ordinal);

    /// <summary>How long the password, once typed, stands for the steps that ask for it again. Zero: every step asks.</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>A signed-in browser on this PC needs no password again (<see cref="NetworkHostOptions.DesktopTrust"/>).</summary>
    public bool DesktopTrust { get; set; }

    /// <summary>Whether <paramref name="session"/> (null: not signed in), from this PC or not, must type the password for <paramref name="purpose"/>.</summary>
    public bool Asks(string? session, bool onThisPc, string purpose) =>
        purpose == SignIn || session is null || !(DesktopTrust && onThisPc || Recently(session));

    /// <summary>What <paramref name="presented"/>, sent on a step's form, comes to.</summary>
    public Answer Judge(string? session, bool onThisPc, string purpose, string presented) =>
        !Asks(session, onThisPc, purpose) ? Answer.Given : presented.Length == 0 ? Answer.Asked : Answer.Check;

    /// <summary>
    /// The password typed by <paramref name="session"/> now (a sign-in, or a step it was given for): the steps that ask for it again take it as given for
    /// <see cref="Grace"/> from here, or for <paramref name="longer"/> (the password just chosen at setup, so a first setup, which takes longer than the grace, never
    /// asks twice: owner, 2026-11-10). Never shortened: a step taken inside the longer window counts as typing it again.
    /// </summary>
    public void Typed(string session, TimeSpan? longer = null)
    {
        if (Grace <= TimeSpan.Zero) return;
        var now = time.GetUtcNow();
        var grace = longer is { } span && span > Grace ? span : Grace;
        lock (gate)
        {
            foreach (var ended in typed.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList()) typed.Remove(ended);
            if (typed.Count >= MaxSessions && !typed.ContainsKey(session)) typed.Remove(typed.MinBy(pair => pair.Value).Key);
            typed[session] = typed.TryGetValue(session, out var until) && until > now + grace ? until : now + grace;
        }
    }

    private bool Recently(string session)
    {
        var now = time.GetUtcNow();
        lock (gate) return typed.TryGetValue(session, out var until) && now < until;
    }
}
