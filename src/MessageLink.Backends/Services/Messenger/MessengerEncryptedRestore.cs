namespace MessageLink.App.Services.Messenger;

/// <summary>
/// The reconnect of the encrypted connection when it was turned on before (the E2EE guard). It asks the helper for <c>restore</c> only, which is the helper's
/// promise never to register a device: a missing device database must not become a new device on the account, whether a copy of the data came without it or the file was lost. Only the
/// Settings button and a brand-new sign-in register a device (the helper call without <c>restore</c>). The helper answers <c>device_missing</c> then, and nothing else is tried.
/// </summary>
internal static class MessengerEncryptedRestore
{
    public enum Outcome { Restored, DeviceMissing, Failed }

    public const string Refused = "encrypted restore outcome=refused reason=device_missing";
    public const string DidNotReconnect = "encrypted conversations did not reconnect";

    /// <summary>The one call: <paramref name="ui"/> sends <c>ui.encrypted</c> with the params it is given. Never called a second time, whatever it answers.</summary>
    public static async Task<Outcome> RunAsync(Func<object?, Task> ui, Action<string> log)
    {
        try
        {
            await ui(new { restore = true }).ConfigureAwait(false);
            return Outcome.Restored;
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException failure) when (failure.Message == "device_missing")
        {
            log(Refused);
            return Outcome.DeviceMissing;
        }
        catch (Exception)
        {
            log(DidNotReconnect);
            return Outcome.Failed;
        }
    }
}
