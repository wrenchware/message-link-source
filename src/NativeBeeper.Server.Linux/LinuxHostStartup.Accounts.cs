using Microsoft.Extensions.DependencyInjection;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.Core.Services;
using NativeBeeper.Host;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server.Linux;

internal sealed partial class LinuxHostStartup
{
    private SerialQueue? accountSignInQueue;

    private void ConfigureAccounts()
    {
        var accounts = Services.GetRequiredService<ProviderAccounts>();
        accounts.StartGoogleKeeper = (account, stop) =>
        {
            accountSignInQueue ??= new SerialQueue("Account sign-in", log);
            var signIn = new LinuxSignInHost(accountSignInQueue, Services.GetRequiredService<HostClientState>(),
                async ct => { await account.Google!.DiscoverAsync(ct); }, stop, log,
                () => Host.EventLog.Append(new RealtimeEvent(RealtimeKind.ResyncRequired, Scope: ProviderAccounts.Scope(account))));
            if (google?.Chrome is { } chrome)
            {
                account.Google!.Files.BrowserProfile.BeforeChange = () => chrome.Chrome.StopAsync();
                _ = Task.Run(() => new ChromeRenewalLoop(chrome.Browser, new BackendRenewalTarget(account.Google), log).RunAsync(stop));
            }
            account.Keeper!.Start(signIn);
            signIn.EndStartupLoad();
        };
        // Only the host with its own sign-in browser can add accounts on its page.
        if (google?.Chrome is not { SignInOffered: true } browser) return;
        Host.ConfigureAccountSignIn(async (service, key, ct) =>
        {
            if (Move?.Allows(service) == false) throw new InvalidOperationException("account_held");
            var account = await accounts.SelectForSignInAsync(service, key, ct) ?? throw new ArgumentException("Choose an added account.");
            accounts.BeginAccount(account);
            return await WithAccountSignInLeaseAsync(accounts, account, service, () =>
            {
                var selected = Host.Services;
                if (account.Google is { } g)
                {
                    var port = new GoogleMessagesAccountPort(g, account.GoogleMedia!);
                    selected = selected with { GoogleAccount = port, RemoteSignIn = browser.SignIn(port, log, google.Guard, g.Files.BrowserProfile),
                        GoogleSignedIn = () => { account.Keeper!.SignedIn(); accounts.NotifyChanged(); } };
                }
                if (account.Messenger is { } m) selected = selected with { MessengerAccount = new MessengerAccount(m) };
                if (account.Instagram is { } i) selected = selected with { InstagramAccount = new InstagramAccountLifecycle(i) };
                return selected;
            }, ct);
        });
    }

    internal static async Task<ProtocolServices> WithAccountSignInLeaseAsync(ProviderAccounts accounts, ProviderAccounts.Account account,
        string service, Func<ProtocolServices> create, CancellationToken token)
    {
        IDisposable? lease = null;
        try
        {
            lease = await accounts.HoldSignInAsync(account.Registration.Key, service, token);
            return create() with { AccountSignInEnded = () =>
            {
                try { accounts.FinishSignInAsync(account.Registration.Key).GetAwaiter().GetResult(); }
                finally { lease.Dispose(); }
            } };
        }
        catch
        {
            try { await accounts.FinishSignInAsync(account.Registration.Key); }
            finally { lease?.Dispose(); }
            throw;
        }
    }
}
