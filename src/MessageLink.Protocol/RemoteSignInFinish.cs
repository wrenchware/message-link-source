namespace MessageLink.Protocol;

/// <summary>
/// What follows the browser in a Messenger or Instagram sign-in made in the server's own browser (protocol 27): the account is connected with the cookies through the host's own port, exactly as
/// <c>accounts.messenger.connect</c> and <c>accounts.instagram.connect</c> connect a sign-in handed up from a PC's window, and Messenger then turns its encrypted conversations on, once, as the PC does
/// after a new sign-in (MessengerEncryptedAuto). Public so that every face of the one flow (the dispatcher and the server page's socket) finishes it the same way.
/// <para>
/// Nothing is retried: a connect that fails ends the sign-in with its own fixed word, and the person starts again. The cookies are cleared however it ends. Log lines are the fixed
/// <c>remote sign-in service= outcome= reason=</c> with code words only, never a cookie, a name or an id; nothing the person typed or saw ever reaches this class.
/// </para>
/// </summary>
public static class RemoteSignInFinish
{
    public static async Task<RemoteSignInResult> ConnectAsync(ProtocolServices services, string service, Dictionary<string, string> cookies, IProgress<string> progress, Action<string>? log,
        CancellationToken cancellationToken)
    {
        try
        {
            progress.Report(RemoteSignIn.ConnectingProgress);
            int conversations;
            try
            {
                conversations = service switch
                {
                    RemoteSignInServices.Messenger => await (services.MessengerAccount ?? throw new NotSupportedException()).ConnectAsync(cookies, cancellationToken).ConfigureAwait(false),
                    RemoteSignInServices.Instagram => await (services.InstagramAccount ?? throw new NotSupportedException()).ConnectAsync(cookies, cancellationToken).ConfigureAwait(false),
                    _ => throw new NotSupportedException(),
                };
            }
            catch (Exception failure) when (failure is not NotSupportedException)
            {
                log?.Invoke(Line(service, "failed", "connect_" + Word(failure)));
                throw;
            }
            log?.Invoke(Line(service, "connected", "none"));
            if (service != RemoteSignInServices.Messenger) return new RemoteSignInResult(conversations);
            var messenger = services.MessengerAccount!;
            // Already on, unless Facebook removed its device or its keys are gone: then this new sign-in is what asks for a new one (MessengerEncryptedAuto).
            if (messenger.State is { EncryptedEnabled: true, Encrypted: not (MessageLink.Core.Services.MessengerEncryption.DeviceGone or MessageLink.Core.Services.MessengerEncryption.DeviceMissing) })
            {
                log?.Invoke(Line(service, "encryption", RemoteSignInEncryption.AlreadyOn));
                return new RemoteSignInResult(conversations, RemoteSignInEncryption.AlreadyOn);
            }
            progress.Report(RemoteSignIn.EncryptingProgress);
            // Once, whatever it answers: the sign-in itself connected, so a registration that does not answer is said in the result and never made the sign-in's failure.
            string encrypted;
            try { encrypted = await messenger.EnableEncryptedAfterSignInAsync().ConfigureAwait(false) is { } code ? CodeOrOther(code) : RemoteSignInEncryption.On; }
            catch (Exception failure) { encrypted = Word(failure); }
            log?.Invoke(Line(service, "encryption", encrypted));
            return new RemoteSignInResult(conversations, encrypted);
        }
        finally { cookies.Clear(); }
    }

    /// <summary>The one line shape every face of the sign-in logs: fixed words only.</summary>
    public static string Line(string service, string outcome, string reason) => $"remote sign-in service={service} outcome={outcome} reason={reason}";

    // A failure as a word a log or a result may carry: a fixed code word of the app's own, or only the exception's type.
    internal static string Word(Exception failure) => failure is InvalidOperationException && ProtocolDispatcher.IsCodeWord(failure.Message) ? failure.Message : failure.GetType().Name;

    private static string CodeOrOther(string code) => ProtocolDispatcher.IsCodeWord(code) ? code : "other";
}
