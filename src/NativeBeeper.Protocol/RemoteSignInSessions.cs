namespace NativeBeeper.Protocol;

/// <summary>Where the person signing in is (SWITCHBOARD.md 10.6): the server's own page, through its socket.</summary>
public static class RemoteSignInFaces
{
    public const string Page = "page";
}

/// <summary>
/// One face of a sign-in in the server's own browser: <see cref="Owner"/> holds it (the host takes input from that object alone), <see cref="Face"/> says where the person is, <see cref="View"/> is where
/// the pictures go, <see cref="Progress"/> where Google's emoji and the connect's stages go, and <see cref="Log"/> takes the connect's fixed lines. Nothing it carries is printed.
/// </summary>
public sealed class RemoteSignInDriver(object owner, string face, IRemoteSignInOutput view, IProgress<string> progress, Action<string>? log)
{
    public object Owner { get; } = owner;
    public string Face { get; } = face;
    public IRemoteSignInOutput View { get; } = view;
    public IProgress<string> Progress { get; } = progress;
    public Action<string>? Log { get; } = log;

    public override string ToString() => nameof(RemoteSignInDriver);
}

/// <summary>A sign-in refused because another is open; <see cref="Face"/> says where (<see cref="RemoteSignInFaces"/>). Its message is the fixed word every face already knows.</summary>
public sealed class RemoteSignInBusyException(string face) : InvalidOperationException("sign_in_busy")
{
    public string Face { get; } = face;
}

/// <summary>
/// The host's sign-ins in its own browser, whichever face drives them (SWITCHBOARD.md 10.5 and 10.6): the app's protocol connection and the server page's socket both run a sign-in through
/// <see cref="RunAsync"/> and send what the person does through <see cref="InputAsync"/>, so the two are one flow and never two copies of it. One at a time across every face: a second start is
/// refused with <see cref="RemoteSignInBusyException"/>, naming where the open one is, after a short wait for one that is just ending. The host's own browser keeps its own one-at-a-time as well
/// (it is also held by renewals). Input is taken only from the owner of the open sign-in and goes to the port that sign-in runs on. Nothing a person typed or saw passes through here but on its way.
/// </summary>
public sealed class RemoteSignInSessions
{
    /// <summary>How long a start waits for an open sign-in that is ending (cancelled, its browser closing) before it is refused.</summary>
    public TimeSpan EndingGrace { get; set; } = TimeSpan.FromMilliseconds(500);

    private readonly object gate = new();
    private Session? current;

    private sealed class Session(RemoteSignInDriver driver, Func<object, IReadOnlyList<RemoteSignInInput>, CancellationToken, Task> input)
    {
        public RemoteSignInDriver Driver { get; } = driver;
        public Func<object, IReadOnlyList<RemoteSignInInput>, CancellationToken, Task> Input { get; } = input;
        public TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Where a sign-in is open now (<see cref="RemoteSignInFaces"/>), or null when none is.</summary>
    public string? OpenOn
    {
        get { lock (gate) return current?.Driver.Face; }
    }

    /// <summary>Whether <paramref name="services"/> sign <paramref name="service"/> in in the host's own browser (and, for Messenger and Instagram, can connect what it signs in).</summary>
    public static bool Offers(ProtocolServices services, string? service) => service switch
    {
        RemoteSignInServices.Google => services.RemoteSignIn is not null,
        RemoteSignInServices.Voice => services.VoiceRemoteSignIn is not null,
        RemoteSignInServices.Messenger => services.MetaRemoteSignIn?.Offers(service) == true && services.MessengerAccount is not null,
        RemoteSignInServices.Instagram => services.MetaRemoteSignIn?.Offers(service) == true && services.InstagramAccount is not null,
        _ => false,
    };

    /// <summary>
    /// Runs one sign-in of <paramref name="service"/> to its end for <paramref name="driver"/>: Google's and Voice's on their ports (Google's keeper told afterwards), Messenger's and Instagram's on the
    /// cookie port and then the connect (<see cref="RemoteSignInFinish"/>). Failures are the ports' and the connect's fixed words, <see cref="RemoteSignInBusyException"/> while another is open, and
    /// <see cref="NotSupportedException"/> for a service the host does not sign in.
    /// </summary>
    public async Task<RemoteSignInResult> RunAsync(ProtocolServices services, string service, RemoteSignInDriver driver, string clientOpId, bool asNewDevice, int width, int height,
        CancellationToken cancellationToken, string? accountKey = null)
    {
        if (!Offers(services, service)) throw new NotSupportedException();
        ProtocolServices selected = services;
        Task Input(object owner, IReadOnlyList<RemoteSignInInput> events, CancellationToken token) => (service switch
        {
            RemoteSignInServices.Google => (Func<object, IReadOnlyList<RemoteSignInInput>, CancellationToken, Task>)selected.RemoteSignIn!.InputAsync,
            RemoteSignInServices.Voice => selected.VoiceRemoteSignIn!.InputAsync,
            _ => selected.MetaRemoteSignIn!.InputAsync,
        })(owner, events, token);
        var session = await EnterAsync(driver, Input, cancellationToken).ConfigureAwait(false);
        try
        {
            if (accountKey is not null)
            {
                if (driver.Face != RemoteSignInFaces.Page || services.AccountSignIn is null) throw new NotSupportedException();
                selected = services = await services.AccountSignIn(service, accountKey, cancellationToken).ConfigureAwait(false);
            }
            var request = new RemoteSignInRequest(driver.Owner, clientOpId, service == RemoteSignInServices.Google && asNewDevice, width, height);
            switch (service)
            {
                case RemoteSignInServices.Google:
                    await services.RemoteSignIn!.SignInAsync(request, driver.View, driver.Progress, cancellationToken).ConfigureAwait(false);
                    // As accounts.google.signIn does once a sign-in has been accepted: the host's keeper is told, and a failure to tell is never the sign-in's.
                    try { services.GoogleSignedIn?.Invoke(); }
                    catch (Exception) { }
                    return new RemoteSignInResult();
                case RemoteSignInServices.Voice:
                    // Nothing of Messages' keeper is told: it renews Messages, and the host adds the Voice account itself.
                    await services.VoiceRemoteSignIn!.SignInAsync(request, driver.View, driver.Progress, cancellationToken).ConfigureAwait(false);
                    return new RemoteSignInResult();
                default:
                    var cookies = await services.MetaRemoteSignIn!.SignInAsync(service, request, driver.View, cancellationToken).ConfigureAwait(false);
                    return await RemoteSignInFinish.ConnectAsync(services, service, cookies, driver.Progress, driver.Log, cancellationToken).ConfigureAwait(false);
            }
        }
        finally { try { selected.AccountSignInEnded?.Invoke(); } finally { Leave(session); } }
    }

    /// <summary>Delivers what the person did to the sign-in <paramref name="owner"/> holds. Refused (<see cref="InvalidOperationException"/>, <c>no_sign_in</c>) when it holds none; nothing in the events is looked at then.</summary>
    public Task InputAsync(object owner, IReadOnlyList<RemoteSignInInput> events, CancellationToken cancellationToken)
    {
        Session? session;
        lock (gate) session = current;
        if (session is null || !ReferenceEquals(session.Driver.Owner, owner)) throw new InvalidOperationException("no_sign_in");
        return session.Input(owner, events, cancellationToken);
    }

    private async Task<Session> EnterAsync(RemoteSignInDriver driver, Func<object, IReadOnlyList<RemoteSignInInput>, CancellationToken, Task> input, CancellationToken cancellationToken)
    {
        var session = new Session(driver, input);
        Session? open;
        lock (gate)
        {
            if (current is null) return current = session;
            open = current;
        }
        // One that is ending (its caller cancelled, its browser closing) is given a moment, so starting again right after a cancel is not refused.
        try { await open.Ended.Task.WaitAsync(EndingGrace, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { }
        lock (gate)
        {
            if (current is null) return current = session;
            throw new RemoteSignInBusyException(current.Driver.Face);
        }
    }

    private void Leave(Session session)
    {
        lock (gate)
        {
            if (ReferenceEquals(current, session)) current = null;
        }
        session.Ended.TrySetResult();
    }
}
