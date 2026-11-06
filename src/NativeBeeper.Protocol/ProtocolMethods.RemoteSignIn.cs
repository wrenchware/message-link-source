namespace NativeBeeper.Protocol;

// A sign-in made in the server's own browser (SWITCHBOARD.md 5.4 and 10.6). Google binds a sign-in to the browser that made it, so a server that must renew one has to be the
// browser that signs in. The server's page shows that browser and drives it from its own socket (RemoteSignInSessions); no protocol method starts one, so this file holds the
// types of what the person does and what the browser shows, and the ports a host gives.
//
// What the person types may be a password. So: an input event is never logged, journaled, kept in the event log or written to a file; a frame (a picture of Google's page,
// which may show an address or a code) the same. The events carry no identifier a log could show, an error names no part of them, and the types that hold them print only their
// own name.

/// <summary>The services a sign-in in the server's browser is made for.</summary>
public static class RemoteSignInServices
{
    public const string Google = "google", Voice = "voice", Messenger = "messenger", Instagram = "instagram";

    public static bool Known(string? service) => service is Google or Voice or Messenger or Instagram;

    /// <summary>Whether the service's sign-in ends when the person says so (<see cref="RemoteSignIn.DoneKind"/>) and the host then reads its cookies, rather than when the host sees it signed in.</summary>
    public static bool EndsWithDone(string? service) => service is Messenger or Instagram;
}

/// <summary>
/// How a sign-in in the server's browser ended (protocol 27). <paramref name="Conversations"/>: the conversations that joined (Messenger and Instagram; 0 for Google and Voice). <paramref name="Encrypted"/>:
/// Messenger's encrypted conversations after the sign-in: <see cref="RemoteSignInEncryption.On"/>, <see cref="RemoteSignInEncryption.AlreadyOn"/>, or a fixed failure word (the registration's own code, or
/// the type of what stopped it); null for every other service.
/// </summary>
public sealed record RemoteSignInResult(int Conversations = 0, string? Encrypted = null);

/// <summary>The words of <see cref="RemoteSignInResult.Encrypted"/> that are not a failure.</summary>
public static class RemoteSignInEncryption
{
    public const string On = "on", AlreadyOn = "already_on";
}

/// <summary>What the person did, in the order they did it. The host takes at most <see cref="RemoteSignIn.MaxEvents"/> in a call.</summary>
public sealed record RemoteSignInInputParams(IReadOnlyList<RemoteSignInInput> Events)
{
    public override string ToString() => nameof(RemoteSignInInputParams);
}

/// <summary>
/// One thing the person did. <see cref="Kind"/> is <see cref="RemoteSignIn.Mouse"/>, <see cref="RemoteSignIn.Key"/>, <see cref="RemoteSignIn.Text"/> or
/// <see cref="RemoteSignIn.Resize"/>; the members each kind reads are the ones its name says, and the host ignores the rest. Pointer positions are in the page's own pixels
/// (the page's size in its own pixels, from the newest picture). <see cref="Text"/> is what was typed or pasted and is
/// private: it is never printed, logged or kept (the record prints only its name).
/// </summary>
/// <param name="Type">A mouse event: <c>mouseMoved</c>, <c>mousePressed</c>, <c>mouseReleased</c> or <c>mouseWheel</c>. A key event: <c>keyDown</c> or <c>keyUp</c>.</param>
/// <param name="Button"><c>none</c>, <c>left</c>, <c>middle</c> or <c>right</c>.</param>
/// <param name="Buttons">The buttons held while it happened, as a mask (left 1, right 2, middle 4).</param>
/// <param name="Modifiers">Alt 1, Ctrl 2, Meta 4, Shift 8.</param>
/// <param name="Key">A control key by its DOM name (<c>Enter</c>, <c>Tab</c>, <c>Backspace</c>, <c>Delete</c>, <c>Escape</c>, the arrows, <c>Home</c>, <c>End</c>, <c>PageUp</c>, <c>PageDown</c>), or <c>a</c> with Ctrl (select all). Characters are <see cref="Text"/>.</param>
public sealed record RemoteSignInInput(string Kind, string? Type = null, double X = 0, double Y = 0, string? Button = null, int Buttons = 0, int ClickCount = 0,
    double DeltaX = 0, double DeltaY = 0, int Modifiers = 0, string? Key = null, string? Text = null, int Width = 0, int Height = 0)
{
    public override string ToString() => nameof(RemoteSignInInput);
}

/// <summary>One picture from the host's browser, ready to send: base64 JPEG, and the page's size in its own pixels.</summary>
public sealed record RemoteSignInFrame(string Jpeg, int ViewWidth, int ViewHeight)
{
    public override string ToString() => nameof(RemoteSignInFrame);
}

/// <summary>Who is signing in: <paramref name="Owner"/> is the connection (the host answers input from that connection only).</summary>
public sealed record RemoteSignInRequest(object Owner, string ClientOpId, bool AsNewDevice, int Width, int Height);

public static class RemoteSignIn
{
    public const string Mouse = "mouse", Key = "key", Text = "text", Resize = "resize";
    /// <summary>The person says a Messenger or Instagram sign-in is finished (protocol 27). A host ignores it in a sign-in that ends by itself (Google's), and a host below 27 never gets one.</summary>
    public const string DoneKind = "done";
    public const string FrameKind = "frame", SignedInKind = "signedIn";
    /// <summary>
    /// What a Messenger or Instagram sign-in reports as progress (protocol 27), in place of Google's emoji: the browser is closed and the account is being connected, then (Messenger) its encrypted
    /// conversations are being turned on.
    /// </summary>
    public const string ConnectingProgress = "connecting", EncryptingProgress = "encrypting";
    /// <summary>The most input events one call carries, and the most characters one text event carries.</summary>
    public const int MaxEvents = 64, MaxTextCharacters = 4096;
    /// <summary>The longest picture the host sends, as base64 (about 512 KiB of JPEG); a bigger one is not sent.</summary>
    public const int MaxFrameCharacters = 700_000;
    /// <summary>The picture size the host starts with, and the limits it clamps a request to.</summary>
    public const int DefaultWidth = 1280, DefaultHeight = 800, MinWidth = 320, MinHeight = 240, MaxWidth = 1920, MaxHeight = 1200;
    /// <summary>What Settings' Google Messages card says while the server's Chrome is out of date (protocol 14).</summary>
    public const string ChromeOutOfDateSentence = "Message Link Server's Chrome is out of date. Reinstall the server to update it.";
}

/// <summary>
/// The host's sign-in in its own browser, as the dispatcher serves it. A host without one (every host but the Linux server run with Google Messages and a headed browser)
/// leaves it null, and both methods answer <c>not_supported</c>.
/// </summary>
public interface IRemoteSignInHost
{
    /// <summary>
    /// Runs one sign-in to its end: the browser, then the cookies, the kept profile and the phone's pairing (the emoji as <paramref name="pairingEmoji"/>). Failures are fixed code
    /// words as <see cref="InvalidOperationException"/> (<c>sign_in_busy</c>, <c>sign_in_timeout</c>, <c>sign_in_browser</c>) or the backend's own.
    /// </summary>
    Task SignInAsync(RemoteSignInRequest request, IRemoteSignInOutput output, IProgress<string> pairingEmoji, CancellationToken cancellationToken);

    /// <summary>Delivers what the person did to <paramref name="owner"/>'s sign-in. Refused (<see cref="InvalidOperationException"/>, <c>no_sign_in</c>) when that connection has none.</summary>
    Task InputAsync(object owner, IReadOnlyList<RemoteSignInInput> events, CancellationToken cancellationToken);

    /// <summary>Whether the browser this host signs in through has gone unrenewed past the age its image was meant to keep it (protocol 14, <see cref="HostLimits.ChromeOutOfDate"/>). Read whenever the state is built.</summary>
    bool BrowserOutOfDate => false;
}

/// <summary>Where a sign-in puts what it shows. Both calls return at once; the newest picture is the only one that matters, so one the connection is too slow for is replaced, never queued.</summary>
public interface IRemoteSignInOutput
{
    void Frame(RemoteSignInFrame frame);
    void SignedIn();
}

/// <summary>
/// A sign-in in the host's own browser whose result is the service's cookies (protocol 27: Messenger and Instagram), made on a throwaway profile and read when the person says they are done. A host
/// without one (every host but the Linux server with its own Chrome) leaves it null, and those services answer <c>not_supported</c> on <see cref="ProtocolMethods.ServerSignInMethod"/>.
/// <para>
/// The session is held by <see cref="RemoteSignInRequest.Owner"/>, any object: every face of the flow runs it through <see cref="RemoteSignInSessions"/>, the dispatcher with its connection and the
/// server page's own socket (10.6) with an object of its own, each with its own <see cref="IRemoteSignInOutput"/>, and the cookies go on to <see cref="RemoteSignInFinish.ConnectAsync"/> the same way.
/// </para>
/// </summary>
public interface IRemoteCookieSignInHost
{
    /// <summary>Whether this host signs <paramref name="service"/> in (<see cref="RemoteSignInServices.Messenger"/> or <see cref="RemoteSignInServices.Instagram"/>).</summary>
    bool Offers(string service);

    /// <summary>
    /// Runs the browser part to its end and returns the cookies the service's own sign-in window would have kept, the required ones among them; the caller owns and clears them. Failures are
    /// fixed words as <see cref="InvalidOperationException"/>: <c>sign_in_busy</c>, <c>sign_in_timeout</c>, <c>sign_in_browser</c>, <c>missing_cookies</c> (the person chose Done before the
    /// service had signed them in).
    /// </summary>
    Task<Dictionary<string, string>> SignInAsync(string service, RemoteSignInRequest request, IRemoteSignInOutput output, CancellationToken cancellationToken);

    /// <summary>As <see cref="IRemoteSignInHost.InputAsync"/>; a <see cref="RemoteSignIn.DoneKind"/> event says the person is finished.</summary>
    Task InputAsync(object owner, IReadOnlyList<RemoteSignInInput> events, CancellationToken cancellationToken);
}
