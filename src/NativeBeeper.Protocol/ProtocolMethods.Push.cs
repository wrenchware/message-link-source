namespace NativeBeeper.Protocol;

// Notifications to a phone (SWITCHBOARD.md 7.5, Decisions: encrypted pushes). A server set up with the owner's own Firebase project offers its app settings
// (`push.offer`); the phone makes a P-256 key and a secret, asks Firebase for a token, and gives the three to the server (`push.register`), which keeps them
// beside the paired device and forgets them when the device is removed. Each new message then goes to a phone that is not connected as a push sealed to
// its key (Web Push encryption, RFC 8291), so Firebase carries only ciphertext of one padded size. A server without the project offers nothing, and a
// phone then asks for nothing. Neither call sends anything to a service; `push.register` is per device, so it is answered only on a network connection.
// A phone whose package the project lacks reports it with `push.register` and an empty token (stage 10, 10.9): kept beside the device for the server's page,
// never a push target. Protocol 27 adds `push.testShown` (the phone showed the server's test push) and `push.status` (how the server's pushes stand, for
// Manage server), and the `push` account signal (pushes turned on, or the files replaced: an open phone asks for the offer again).
public static partial class ProtocolMethods
{
    public const string PushOffer = "push.offer";
    public const string PushRegister = "push.register";
    public const string PushTestShown = "push.testShown";
    public const string PushStatusRead = "push.status";

    private static Entry[] PushEntries() =>
    [
        new(PushOffer, "IPushService.OfferAsync", null, typeof(PushOffer)),
        new(PushRegister, "IPushService.RegisterAsync", typeof(PushRegisterParams), typeof(Done)),
        new(PushTestShown, "IPushService.TestShownAsync", typeof(PushTestShownParams), typeof(Done)),
        new(PushStatusRead, "IPushService.StatusAsync", null, typeof(PushStatus)),
    ];
}

/// <summary>
/// The owner's Firebase project as a phone needs it to ask Firebase for a token: none of it is a secret (they are the values of the app's own
/// google-services.json). <paramref name="Apps"/> names the Firebase app of each Android package the project knows; a phone whose package is not among them
/// asks for nothing. <paramref name="Available"/> false: this server sends no pushes (no project set up), and the rest is empty.
/// </summary>
public sealed record PushOffer(bool Available, string? ProjectId = null, string? SenderId = null, string? ApiKey = null, IReadOnlyList<PushApp>? Apps = null);

public sealed record PushApp(string Package, string AppId);

/// <summary>
/// The calling device's push address: Firebase's <paramref name="Token"/> for its <paramref name="Package"/>, and the P-256 public key (65-byte uncompressed
/// point, base64) and 16-byte secret (base64) its pushes are sealed to. Registering again replaces it; a device registers whenever any of them changes.
/// An empty <paramref name="Token"/> with a package the offer lacks is the phone saying the project has no app for it: kept as such, never sent to.
/// </summary>
// Opt-in: only a phone that understands a notification-free "sync" push may receive one. Older clients and saved registrations default to false.
public sealed record PushRegisterParams(string Token, string Package, string PublicKey, string Auth, bool BackgroundSync = false);

/// <summary>The phone showed the server's test push whose <c>MessageId</c> was <paramref name="Id"/> (protocol 27). At most <see cref="PushLimits.TestId"/> characters.</summary>
public sealed record PushTestShownParams(string Id);

public static class PushLimits
{
    public const int TestId = 64;
}

/// <summary>
/// How the server's pushes stand (protocol 27), for Manage server: whether they are on, when one last went, and each phone that matters to them. Names are the
/// devices' own; no token, key or project is in it.
/// </summary>
public sealed record PushStatus(bool Available, DateTimeOffset? LastSent = null, IReadOnlyList<PushPhone>? Phones = null)
{
    /// <summary>
    /// The one line the server's page and Manage server both show: off, or on with when one last went, how many phones get them and what is wrong with any
    /// (failures with their reason, a missing app, a phone that asked and never signed up).
    /// </summary>
    public string Summary(DateTimeOffset now)
    {
        if (!Available) return "Notifications to phones are off. Set them up on the server's page.";
        var phones = Phones ?? [];
        var parts = new List<string> { LastSent is { } sent ? "On, last sent " + Ago(now - sent) : "On, nothing sent yet" };
        var on = phones.Count(phone => phone.State is PushPhoneStates.On or PushPhoneStates.Failing);
        parts.Add(on == 0 ? "no phone signed up yet" : on == 1 ? "1 phone signed up" : $"{on} phones signed up");
        foreach (var phone in phones)
            switch (phone.State)
            {
                case PushPhoneStates.Failing:
                    parts.Add($"{phone.Name}: {(phone.Failures == 1 ? "1 failed" : $"{phone.Failures} failed")} ({Reason(phone)})");
                    break;
                case PushPhoneStates.MissingApp:
                    parts.Add($"{phone.Name}: Firebase has no app for {phone.Package}");
                    break;
                case PushPhoneStates.NotSignedUp:
                    parts.Add($"{phone.Name}: not signed up");
                    break;
            }
        return string.Join(" · ", parts) + ".";
    }

    /// <summary>A failure's reason in plain words.</summary>
    public static string Reason(PushPhone phone) => phone.Reason switch
    {
        PushFailureReasons.Refused => phone.Status > 0 ? $"Google refused it, status {phone.Status}" : "Google refused it",
        PushFailureReasons.Gone => "Google says its address is gone; it signs up again when Messages opens",
        _ => "Google couldn't be reached",
    };

    private static string Ago(TimeSpan span) =>
        span < TimeSpan.FromMinutes(1) ? "just now" : span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes} min ago"
        : span < TimeSpan.FromDays(1) ? $"{(int)span.TotalHours} h ago" : span.TotalDays < 2 ? "yesterday" : $"{(int)span.TotalDays} days ago";
}

/// <summary>
/// One phone as the server's pushes see it. <paramref name="State"/> is one of <see cref="PushPhoneStates"/>; on <see cref="PushPhoneStates.Failing"/>,
/// <paramref name="Failures"/> pushes failed since the last that went, the newest for <paramref name="Reason"/> (one of <see cref="PushFailureReasons"/>) with
/// Google's HTTP <paramref name="Status"/> (0 when none came back); on <see cref="PushPhoneStates.MissingApp"/>, <paramref name="Package"/> is the package the
/// project lacks.
/// </summary>
public sealed record PushPhone(string Name, string State, int Failures = 0, string? Reason = null, int Status = 0, string? Package = null);

public static class PushPhoneStates
{
    /// <summary>Signed up, and nothing has failed since the last push went.</summary>
    public const string On = "on";
    /// <summary>Signed up, and pushes to it have failed since the last that went.</summary>
    public const string Failing = "failing";
    /// <summary>The phone said the project has no app for its package.</summary>
    public const string MissingApp = "missingApp";
    /// <summary>The phone asked what the server offers but has not signed up.</summary>
    public const string NotSignedUp = "notSignedUp";
    public static IReadOnlyList<string> All { get; } = [On, Failing, MissingApp, NotSignedUp];
}

public static class PushFailureReasons
{
    /// <summary>Google answered with a refusal (<see cref="PushPhone.Status"/>).</summary>
    public const string Refused = "refused";
    /// <summary>Google could not be reached, or gave no access token.</summary>
    public const string Unreachable = "unreachable";
    /// <summary>Google says the phone's address is gone for good; it was forgotten, and the phone signs up again when it connects.</summary>
    public const string Gone = "gone";
    public static IReadOnlyList<string> All { get; } = [Refused, Unreachable, Gone];
}

/// <summary>The server's pushes, for the connection that asks: a network connection's carries its device, so a registration is that device's.</summary>
public interface IPushService
{
    Task<PushOffer> OfferAsync(CancellationToken cancellationToken = default);
    Task RegisterAsync(PushRegisterParams registration, CancellationToken cancellationToken = default);
    /// <summary>The calling phone showed a test push (protocol 27). A test that is not this device's, or no longer waited on, is let go without an error.</summary>
    Task TestShownAsync(PushTestShownParams shown, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    /// <summary>How the server's pushes stand (protocol 27).</summary>
    Task<PushStatus> StatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>A server's pushes as Manage server asks after them (protocol 27). Nothing is kept.</summary>
public sealed class PushStatusProxy(ProtocolClient client)
{
    public Task<PushStatus> StatusAsync(CancellationToken cancellationToken = default) => client.CallAsync<PushStatus>(ProtocolMethods.PushStatusRead, cancellationToken);
}
