using Microsoft.Extensions.DependencyInjection;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services;

// The four providers behind the routers that present them as one. A host registers what it supplies itself first
// (IStateProtector, IPhotoReencoder, IGoogleBrowser, IAppPresence, IHelperCellUnprotector, the Storage stores; and, if it
// can play video, IVideoFrameExtractor, without which a video its network sent no still for keeps its play button) and
// what the routers read from Presentation (ComposerActivity, PrivateReading, SavedPhotos).
public static class BackendServices
{
    // The access-token store the backends themselves read (the Google profile). The unkeyed IAccessTokenStore is the same object, until a
    // client with its own (IdentityAccessTokens, which reads the host's pushed state) replaces it; the backends must not follow it.
    public const string HostTokensKey = "host-tokens";

    public static IServiceCollection AddBackends(this IServiceCollection collection)
    {
        collection.AddSingleton<GoogleSessionProfile>();
        // The unkeyed first: a client that swaps it (ServiceCollection.Replace) takes the first match.
        collection.AddSingleton<IAccessTokenStore>(sp => sp.GetRequiredService<GoogleSessionProfile>());
        collection.AddKeyedSingleton<IAccessTokenStore>(HostTokensKey, (sp, _) => sp.GetRequiredService<GoogleSessionProfile>());
        // The backend's own view of the same store, so it never waits on a client's token store, which may be waiting on the host (PrivateState holds no state).
        collection.AddKeyedSingleton<NativeBeeper.Presentation.Services.PrivateState>(HostTokensKey, (sp, _) =>
            new(sp.GetRequiredService<IPrivateStateStore>(), sp.GetRequiredKeyedService<IAccessTokenStore>(HostTokensKey)));
        collection.AddSingleton<IHostIdentity>(sp => HostIdentityPort.Over(sp.GetRequiredKeyedService<IAccessTokenStore>(HostTokensKey),
            sp.GetRequiredService<MessengerBackend>(), sp.GetRequiredService<InstagramBackend>(), sp.GetRequiredService<GoogleVoiceAccounts>()));
        collection.AddSingleton<GoogleMessagesBackend>();
        collection.AddSingleton<IGoogleMessagesAccount, GoogleMessagesAccountPort>();
        collection.AddSingleton<NativeBeeper.Presentation.Services.IRecentMessages>(sp => sp.GetRequiredService<GoogleMessagesBackend>());
        collection.AddSingleton<GoogleSignInKeeper>();
        collection.AddSingleton<GooglePendingFeatures>();
        collection.AddSingleton<GoogleMediaBackend>();
        collection.AddSingleton<BeeperAvatarReader>();
        collection.AddSingleton<GoogleArchive>();
        collection.AddSingleton<IBeeperBackend>(sp => sp.GetRequiredService<GoogleMessagesBackend>());
        collection.AddSingleton<ILocalChatPreferences, LocalChatPreferenceStore>();
        collection.AddSingleton<ILocalContactNames>(sp => new LocalContactNameStore(sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton<MessengerBackend>();
        collection.AddSingleton<IMessengerAccount, MessengerAccount>();
        collection.AddSingleton<InstagramBackend>();
        collection.AddSingleton<IInstagramAccount, InstagramAccountLifecycle>();
        collection.AddSingleton<ProviderAccounts>();
        collection.AddSingleton<MessagingRouter>();
        collection.AddSingleton<DirectoryRouter>();
        collection.AddSingleton<TypingRouter>();
        collection.AddSingleton<MediaRouter>();
        collection.AddSingleton<GoogleVoiceAccounts>();
        collection.AddSingleton<IGoogleVoiceAccounts, GoogleVoiceAccountsPort>();
        collection.AddSingleton<GoogleVoiceBackend>();
        collection.AddSingleton<IMessageSender>(sp => sp.GetRequiredService<MessagingRouter>());
        collection.AddSingleton<IContactBackend>(sp => sp.GetRequiredService<DirectoryRouter>());
        collection.AddSingleton<IGroupBackend>(sp => sp.GetRequiredService<DirectoryRouter>());
        collection.AddSingleton<IGroupManagement>(sp => sp.GetRequiredService<DirectoryRouter>());
        collection.AddSingleton<IChatSettingsBackend>(sp => sp.GetRequiredService<MessagingRouter>());
        collection.AddSingleton<IChatDeletion>(sp => sp.GetRequiredService<MessagingRouter>());
        collection.AddSingleton<IMessageActions>(sp => sp.GetRequiredService<MessagingRouter>());
        collection.AddSingleton<IMessageMutations>(sp => sp.GetRequiredService<MessagingRouter>());
        collection.AddSingleton<IMediaReader>(sp => sp.GetRequiredService<MediaRouter>());
        collection.AddSingleton<IAttachmentBackend>(sp => sp.GetRequiredService<MediaRouter>());
        collection.AddSingleton<HostDiagnosticsSource>();
        collection.AddSingleton<IHostDiagnostics>(sp => sp.GetRequiredService<HostDiagnosticsSource>());
        collection.AddSingleton<IHelperRestarter>(sp => new HostHelperRestarter(sp.GetRequiredService<GoogleMessagesBackend>(), sp.GetRequiredService<GoogleSignInKeeper>(),
            sp.GetRequiredService<MessengerBackend>(), sp.GetRequiredService<InstagramBackend>(), sp.GetRequiredService<GoogleVoiceAccounts>(), AppLog.Write));
        // What the incoming-photo and recent-photo services ask of the host (3.0b): the concrete objects stay registered for a host that serves them over the
        // protocol, because the interfaces are what a client replaces with its proxies.
        collection.AddSingleton<IMediaPreparer>(sp => sp.GetRequiredService<MediaRouter>());
        collection.AddSingleton<IncomingCardRequester>(sp => new(sp.GetRequiredService<MessengerBackend>(), sp.GetRequiredService<GoogleMessagesBackend>(),
            new GoogleIncomingCards(sp.GetRequiredService<GoogleArchive>(), sp.GetRequiredKeyedService<IAccessTokenStore>(HostTokensKey)), AppLog.Write));
        collection.AddSingleton<IIncomingCards>(sp => sp.GetRequiredService<IncomingCardRequester>());
        collection.AddSingleton<ICaughtUpFeed>(sp => new BackendCaughtUp(sp.GetRequiredService<GoogleMessagesBackend>(), sp.GetRequiredService<MessengerBackend>(), sp.GetRequiredService<InstagramBackend>()));
        return collection;
    }
}
