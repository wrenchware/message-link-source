using Microsoft.Extensions.DependencyInjection;
using NativeBeeper.App;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;
using NativeBeeper.Host;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server;

internal sealed partial class HostStartup
{
    private void ConfigureAccounts()
    {
        var accounts = Services.GetRequiredService<ProviderAccounts>();
        accounts.StartGoogleKeeper = (account, stop) =>
        {
            var signIn = new ServerSignInHost(sta, Services.GetRequiredService<HostClientState>(),
                async ct => { await account.Google!.DiscoverAsync(ct); }, stop, log,
                () => Host.EventLog.Append(new RealtimeEvent(RealtimeKind.ResyncRequired, Scope: ProviderAccounts.Scope(account))));
            account.Keeper!.Start(signIn);
            signIn.EndStartupLoad();
        };
        Host.NativeAuthentication = async (service, key, ct) =>
        {
            var provider = service switch { "google-messages" => "google", "messenger" => "messenger", "instagram" => "instagram", "google-voice" => "voice", _ => throw new NotSupportedException() };
            // A missing selection is never permission to replace the original account.
            if (key is null || provider == "voice" && key != "new") throw new InvalidOperationException("account_selection_required");
            ProviderAccounts.Account? account = null;
            if (provider != "voice") account = await accounts.SelectForSignInAsync(provider, key, ct);
            if (account is not null) accounts.BeginAccount(account);
            var slot = account?.Registration.Key ?? key;
            bool Valid() => account is null || accounts.Available.Any(current => current.Registration.Key == slot && current.Registration.Service == provider);
            IDisposable? lease = null;
            GoogleSignInProfile? attempt;
            try
            {
                lease = account is not null ? await accounts.HoldSignInAsync(account.Registration.Key, provider, ct) : null;
                attempt = provider is "google" or "voice" ? GoogleSignInProfile.CreateAttempt(provider == "google" ? GoogleSignInProfile.MessagesService : GoogleSignInProfile.VoiceService) : null;
            }
            catch { try { if (account is not null) await accounts.FinishSignInAsync(slot); } finally { lease?.Dispose(); } throw; }
            var keptProfile = false;
            return new NativeAuthenticationTarget(service, slot, Valid, async (packet, progress, token) =>
            {
                if (!Valid() || packet.Service != service || packet.Slot != slot) throw new InvalidOperationException("account_removed");
                var cookies = packet.Cookies ?? throw new InvalidOperationException("cookies_missing");
                if (provider is "google" or "voice")
                {
                    var profileService = provider == "google" ? GoogleSignInProfile.MessagesService : GoogleSignInProfile.VoiceService;
                    if (packet.Profile is null || !GoogleSignInProfile.TryOpen(packet.Profile, out var profile, profileService) || profile is null)
                        throw new InvalidOperationException("profile_missing");
                    await using var result = new GoogleSignInResult(cookies, profile);
                    if (provider == "google")
                    {
                        var backend = account?.Google ?? Services.GetRequiredService<GoogleMessagesBackend>();
                        await backend.SignInAsync(cookies, progress, token);
                        if (!Valid()) throw new InvalidOperationException("account_removed");
                        await backend.Files.BrowserProfile.KeepAsync(result);
                        keptProfile = backend.Files.BrowserProfile.Id == result.ProfileId;
                        (account?.Keeper ?? Services.GetRequiredService<GoogleSignInKeeper>()).SignedIn();
                    }
                    else
                    {
                        await Services.GetRequiredService<IGoogleVoiceAccounts>().AddAsync(cookies, result.ProfileId, token);
                        result.TransferProfileOwnership();
                        keptProfile = true;
                    }
                }
                else
                {
                    var selected = Host.Services with
                    {
                        MessengerAccount = new MessengerAccount(account?.Messenger ?? Services.GetRequiredService<MessengerBackend>()),
                        InstagramAccount = new InstagramAccountLifecycle(account?.Instagram ?? Services.GetRequiredService<InstagramBackend>())
                    };
                    await RemoteSignInFinish.ConnectAsync(selected, provider, cookies, progress, log, token);
                }
                if (account is not null) await accounts.FinishSignInAsync(slot);
                accounts.NotifyChanged();
            }, attempt?.Id, async () => { try { if (!keptProfile && attempt is not null) await attempt.DeleteAsync(); } finally { try { if (account is not null) await accounts.FinishSignInAsync(slot); } finally { lease?.Dispose(); } } });
        };
    }
}
