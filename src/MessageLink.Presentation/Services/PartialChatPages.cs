using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public static class PartialChatPages
{
    public static IEnumerable<ChatSummary> Preserve(IEnumerable<ChatSummary> previous, Page<ChatSummary> page) =>
        previous.Where(chat => page.UnavailableScopes?.Any(scope => chat.Id.StartsWith(scope, StringComparison.Ordinal)) == true);

    public static async Task<Page<ChatSummary>> ReadAsync(Func<Task<Page<ChatSummary>>> read, string scope,
        string label, string? retryCursor, CancellationToken token)
    {
        try { return await read(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        // A service that needs signing in or pairing again is said once, by the connection notice. Saying here too that
        // it could not refresh made two banners for one cause, and told the user to reconnect when that cannot help.
        catch (MessageLink.Core.Backend.BackendException ex) when (ex.Failure == MessageLink.Core.Backend.BackendFailure.AuthenticationRequired)
        {
            return new([], retryCursor is not null, retryCursor, null, UnavailableScopes: [scope]);
        }
        catch (Exception)
        {
            return new([], retryCursor is not null, retryCursor, null,
                Warning: $"{label} could not refresh. Its saved conversations are still shown. Reconnect or refresh to try again.",
                UnavailableScopes: [scope]);
        }
    }
}
