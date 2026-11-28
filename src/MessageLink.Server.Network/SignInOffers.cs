using MessageLink.Core.Messaging;
using MessageLink.Protocol;

namespace MessageLink.Server.Network;

/// <summary>A service the page signs in to: its provider (<see cref="ServerAccountProviders"/>), its key for the page's own browser (<see cref="RemoteSignInServices"/>), its key for the Windows server's desktop, and its name.</summary>
internal sealed record SignInService(string Provider, string Service, string? Desktop, string Name)
{
    /// <summary>The four, in the order the page lists them (owner, 2026-11-11). A new service is one line here, and a card in Settings.</summary>
    public static IReadOnlyList<SignInService> All { get; } =
    [
        new(ServerAccountProviders.GoogleMessages, RemoteSignInServices.Google, "google-messages", "Google Messages"),
        new(ServerAccountProviders.Messenger, RemoteSignInServices.Messenger, "messenger", "Messenger"),
        new(ServerAccountProviders.GoogleVoice, RemoteSignInServices.Voice, "google-voice", "Google Voice"),
        new(ServerAccountProviders.Instagram, RemoteSignInServices.Instagram, "instagram", "Instagram"),
    ];

    /// <summary>The service an account's provider names; one a newer server lists is another service, which nothing here signs in to.</summary>
    public static SignInService Of(string provider) => All.FirstOrDefault(service => service.Provider == provider) ?? new(provider, "", null, "Another service");

    /// <summary>Its place in the page's order, after the four for any other.</summary>
    public static int Order(string provider) => All.Select(service => service.Provider).ToList().IndexOf(provider) is var at and >= 0 ? at : All.Count;

    /// <summary>Google Voice adds numbers, one process each, so adding one is always open to it.</summary>
    public bool Numbers => Service == RemoteSignInServices.Voice;
}

/// <summary>How the server signs in to its services, and so what its page can offer.</summary>
internal enum SignInHost
{
    /// <summary>It can't sign in at all from its page.</summary>
    None,
    /// <summary>In its own browser, streamed to the page (Linux).</summary>
    Browser,
    /// <summary>In Message Link's native windows on its own desktop (the Windows server, 10.7), from a browser on that PC.</summary>
    Desktop,
}

/// <summary>What the offers are worked out from, read once per page (<see cref="ServerPage"/>), so <see cref="SignInOffers"/> can be checked over made-up ones.</summary>
internal sealed record SignInFacts
{
    /// <summary>The accounts the server holds; null when they couldn't be read.</summary>
    public IReadOnlyList<ServerAccount>? Accounts { get; init; }

    public SignInHost Host { get; init; }

    /// <summary>Which services the server's browser signs in to (<c>RemoteSignInSessions.Offers</c>), on <see cref="SignInHost.Browser"/>.</summary>
    public Func<string, bool> BrowserOffers { get; init; } = _ => false;

    /// <summary>A sign-in can say which account it is for, so an account can be added beside another (protocol's account sign-in, or the desktop's DesktopAccountSignIn).</summary>
    public bool ChoosesAccount { get; init; }

    /// <summary>The browser asking is on the PC that runs the server, the only one a desktop sign-in opens for.</summary>
    public bool OnThisPc { get; init; }
}

internal enum SignInOfferKind { Connect, SignInAgain, AddAccount, AddNumber }

/// <summary>
/// One sign-in the page offers, and the slot it posts: null for none (the service's one sign-in), <c>legacy</c> (the service's first account, by the
/// desktop's chooser), <c>new</c>, or the account's key.
/// </summary>
internal sealed record SignInOffer(SignInOfferKind Kind, string? Slot)
{
    /// <summary>Its words in a line that names the service already: a button, or an item in an account's menu.</summary>
    public string Words => Kind switch
    {
        SignInOfferKind.Connect => "Connect",
        SignInOfferKind.SignInAgain => "Sign in again…",
        SignInOfferKind.AddAccount => "Add account…",
        _ => "Add a number…",
    };

    /// <summary>What it's called for a screen reader, naming what it acts on: "Add account: Messenger", "Sign in again: Messenger (Sam)".</summary>
    public string Label(string target) => Words.TrimEnd('…') + ": " + target;

    /// <summary>Its words where nothing else names the service (the Windows server's desktop row, and every offer's label): "Add account: Messenger" (owner, 2026-11-26).</summary>
    public string Named(string service, string? account = null) => Kind switch
    {
        SignInOfferKind.Connect => "Connect " + service,
        SignInOfferKind.SignInAgain => "Sign in again to " + service + (account is { Length: > 0 } ? ": " + account : ""),
        SignInOfferKind.AddAccount => "Add account: " + service,
        _ => "Add a number: " + service,
    };
}

/// <summary>An account's line: signing it in again (never for a Voice number, which adds another instead), and whether it can be removed here or Google's unpair note stands in.</summary>
internal sealed record AccountOffers(ServerAccount Account, SignInOffer? SignInAgain, bool Removable, bool UnpairNote);

/// <summary>A service's lines: whether it can be signed in to here at all, Connect when it has no account, Add when it has, and each account's line.</summary>
internal sealed record ServiceOffers(SignInService Service, bool Offered, SignInOffer? Connect, SignInOffer? Add, IReadOnlyList<AccountOffers> Accounts);

/// <summary>
/// What the page offers for each service and its accounts, and which slot each sign-in posts, for every place that offers them: the Accounts card, Needs
/// attention, the walkthrough and the Windows server's desktop row. One rule adds an account: the sign-in can choose which one it is for, or the service is
/// Google Voice, which adds numbers. No page works any of this out itself.
/// </summary>
internal sealed class SignInOffers(SignInFacts facts)
{
    /// <summary>The four services in order, then any other a newer server lists, each with its accounts.</summary>
    public IReadOnlyList<ServiceOffers> Services
    {
        get
        {
            var accounts = facts.Accounts ?? [];
            return [.. SignInService.All.Concat(accounts.Select(account => account.Provider).Distinct().Where(provider => SignInService.Order(provider) == SignInService.All.Count).Select(SignInService.Of))
                .Select(service => For(service, [.. accounts.Where(account => account.Provider == service.Provider)]))];
        }
    }

    /// <summary>The one service's lines.</summary>
    public ServiceOffers Of(string provider) => Services.First(service => service.Service.Provider == provider);

    /// <summary>The line an account has, by the service it belongs to.</summary>
    public AccountOffers Line(ServerAccount account) => Of(account.Provider).Accounts.First(line => line.Account == account);

    /// <summary>Needs attention's Sign in for an account that isn't connected: a link, so only where the server's browser signs in; a desktop sign-in is a form in the account's line.</summary>
    public SignInOffer? AttentionSignIn(ServerAccount account) => facts.Host == SignInHost.Browser ? Line(account).SignInAgain : null;

    private ServiceOffers For(SignInService service, ServerAccount[] mine)
    {
        var offered = service.Service.Length > 0 && facts.Host switch
        {
            SignInHost.Browser => facts.BrowserOffers(service.Service),
            SignInHost.Desktop => facts.OnThisPc && service.Desktop is not null,
            _ => false,
        };
        var desktop = facts.Host == SignInHost.Desktop;
        // The desktop's chooser takes "legacy" for a service's first account, and "new" for any other, Google Voice's first number included.
        var chooser = desktop && facts.ChoosesAccount;
        SignInOffer? connect = null, add = null;
        if (offered && mine.Length == 0) connect = new(SignInOfferKind.Connect, !chooser ? null : service.Numbers ? "new" : "legacy");
        if (offered && mine.Length > 0 && (service.Numbers || facts.ChoosesAccount))
            add = service.Numbers ? new(SignInOfferKind.AddNumber, chooser ? "new" : null) : new(SignInOfferKind.AddAccount, "new");
        var lines = mine.Select(account => new AccountOffers(account,
            offered && !service.Numbers ? new SignInOffer(SignInOfferKind.SignInAgain, chooser ? Key(account) ?? "legacy" : desktop ? null : Key(account)) : null,
            account.Removable, !account.Removable && account.Provider == ServerAccountProviders.GoogleMessages)).ToArray();
        return new(service, offered, connect, add, lines);
    }

    // An added account's own key (AccountNamespace), which its sign-in names; Voice's numbers are added, never signed in again by key.
    private static string? Key(ServerAccount account) => AccountNamespace.ValidKey(account.Key) ? account.Key : null;

    // ---- Removing an account ----

    /// <summary>Where a removal asked of an account that can't be removed here goes: Google's unpair note, or that it's gone.</summary>
    public static string NotRemovable(ServerAccount account) => account.Provider == ServerAccountProviders.GoogleMessages ? "account_unpair" : "account_gone";

    /// <summary>What the front page says after a removal: whether there was one, and Google's own words.</summary>
    public static string Removed(ServerAccount account, bool removed) =>
        !removed ? "account_gone" : account.Provider == ServerAccountProviders.GoogleMessages ? "google_removed" : "account_removed";

    /// <summary>What removing it does, asked before it is done. <paramref name="desktop"/>: the Windows server, which keeps Messenger's device where it is (WindowsServerAdmin).</summary>
    public static string RemoveWarning(ServerAccount account, bool desktop)
    {
        var service = SignInService.Of(account.Provider).Name;
        // Removed here since 2026-11-22: the phone is asked to drop the server when it can be, and the pairing is forgotten either way (GoogleMessagesBackend.RemoveAsync).
        if (account.Provider == ServerAccountProviders.GoogleMessages)
            return "The server asks your phone to remove it from Device pairing and forgets the pairing, so every device that uses the server stops showing Google Messages until you pair it here again. "
                + "Your conversations stay on your phone, and your saved messages stay.";
        return $"The server signs out of {service} and forgets what it kept of it, so every device that uses the server stops showing it until you connect it here again. "
            + $"Nothing changes at {service} itself or in its own app, and your saved messages stay."
            + (account.Provider == ServerAccountProviders.Messenger && !desktop ? " Encrypted chats would start over here: the server would join as a new device." : "");
    }
}
