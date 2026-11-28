namespace MessageLink.Core.Services;

/// <summary>
/// How a helper restart call ended: <c>reconnected</c>, <c>failed</c>, <c>refused</c>, <c>cancelled</c>; <c>settled</c> (a settle call
/// found no send in flight, and nothing was stopped); <c>waiting</c> (a stop call found a send in flight, and nothing was stopped);
/// or <c>asking</c> (sends are still in flight, nothing was stopped, and <paramref name="Token"/> names them). The fixed code word for a
/// failure or a refusal; how long the call waited for sends; and whether it stopped the helper with one still in flight.
/// </summary>
/// <param name="Token">Opaque. With <c>asking</c>: which sends the question is about, to be given back with the person's yes.</param>
public sealed record HelperRestartResult(string Outcome, string? Code, long WaitedMs, bool Forced = false, string? Token = null);

/// <summary>
/// Connection health's Restart for the helper that serves a scope (<c>google-direct:</c>, <c>messenger:</c>, <c>instagram:</c>,
/// <c>gvoice:&lt;key&gt;:</c>): the stop-and-resume from the saved session a service already makes when its helper stops by itself, never
/// a pairing or a sign-in. It is not a send, so it is not journaled: a repeat only restarts again, and the host refuses one that is
/// already running or that its recovery budget does not allow (Instagram's).
/// </summary>
/// <remarks>
/// A restart is two calls, because the wait for sends can take as long as the host's patience and only the caller knows, at the moment
/// the helper is stopped, whether it is signing in and which of its own controls to hold. A settle call waits and stops nothing; the
/// stop call that follows never waits. Neither is a send, so a repeat of either is harmless.
/// </remarks>
public interface IHelperRestarter
{
    /// <summary>
    /// With <paramref name="settle"/>: waits for the contact-visible calls on that helper to be answered and ends <c>settled</c>, or, if
    /// they outlast the host's patience, <c>asking</c> with a token naming them; nothing is stopped. Without it, the stop call: the helper
    /// is stopped and resumed at once if nothing is in flight, <c>waiting</c> answers a send that is, and the person's yes to
    /// <c>asking</c> is <paramref name="force"/> with that <paramref name="token"/>, which stops the helper under exactly those sends (their
    /// outcomes are then unknown and are never sent again). A send begun since the token was read is not covered by the yes: the answer is
    /// <c>asking</c> again, with a new token. Every refusal and failure is a <see cref="HelperRestartResult"/> with a fixed code word,
    /// never an exception.
    /// </summary>
    /// <param name="signingIn">The caller is signing in to that service itself (a sign-in window is the caller's, so only the caller knows); the restart is then refused with <c>signing_in</c>. Stated afresh in every call, the stop call above all.</param>
    Task<HelperRestartResult> RestartAsync(string scope, bool signingIn, bool force, string? token = null, bool settle = false);
}

public static class HelperRestartScope
{
    /// <summary>
    /// The fixed word the restart log names a scope's helper by (google_messages, messenger, instagram or gvoice), or null for a scope that
    /// no helper serves.
    /// </summary>
    public static string? Service(string? scope) => scope switch
    {
        "google-direct:" => "google_messages",
        "messenger:" => "messenger",
        "instagram:" => "instagram",
        not null when scope.StartsWith("gvoice:", StringComparison.Ordinal) && scope.Length > "gvoice:".Length + 1 => "gvoice",
        _ => null,
    };
}
