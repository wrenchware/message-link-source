using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// Connection health's Restart as a client asks a host for it (SWITCHBOARD.md 3.0b, <c>host.restartHelper</c>). One request, never repeated
/// here: it is not a send, so it has no operation id, and a connection that ends under it answers a failure with a fixed code word that the
/// person can read and press Restart again on, which the host paces (one at a time per helper, Instagram's budget). The wait for sends
/// happens at the host, in a call of its own that stops nothing, and the question it ends in comes back as the <c>asking</c> outcome.
/// </summary>
public sealed class HelperRestartProxy(ProtocolClient client) : IHelperRestarter
{
    public async Task<HelperRestartResult> RestartAsync(string scope, bool signingIn, bool force, string? token = null, bool settle = false)
    {
        try { return await client.CallAsync<HelperRestartParams, HelperRestartResult>(ProtocolMethods.HostRestartHelper, new(scope, signingIn, force, token, settle)).ConfigureAwait(false); }
        // The same shape every other ending has: a result with a code word, never an exception the window would have to word.
        catch (Exception ex) when (ex is not OperationCanceledException) { return new("failed", ProtocolErrors.From(ex).Code, 0); }
    }
}
