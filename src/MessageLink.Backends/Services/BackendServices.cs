using Microsoft.Extensions.DependencyInjection;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;
using MessageLink.App.Services.Instagram;
using MessageLink.App.Services.Messenger;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services;

// The five providers behind the routers that present them as one. A server registers what it supplies itself first
// (IStateProtector, IPhotoReencoder, IGoogleBrowser, IAppPresence, IHelperCellUnprotector, the Storage stores; and, if it
// can play video, IVideoFrameExtractor, without which a video its network sent no still for keeps its play button) and
// what the routers read from Presentation (ComposerActivity, PrivateReading, SavedPhotos).
public static class BackendServices
{
    // The access-token store the backends themselves read (the Google profile). The unkeyed IAccessTokenStore is the same object, until a
    // client with its own (IdentityAccessTokens, which reads the server's pushed state) replaces it; the backends must not follow it.
    public const string ServerTokensKey = "host-tokens";

    public static IServiceCollection AddBackends(this IServiceCollection collection)
    {
        collection.AddSingleton<GoogleSessionProfile>();
        // The unkeyed first: a client that swaps it (ServiceCollection.Replace) takes the first match.
        collection.AddSingleton<IAccessTokenStore>(sp => sp.GetRequiredService<GoogleSessionProfile>());
        collection.AddKeyedSingleton<IAccessTokenStore>(ServerTokensKey, (sp, _) => sp.GetRequiredService<GoogleSessionProfile>());
        // The backend's own view of the same store, so it never waits on a client's token store, which may be waiting on the server (PrivateState holds no state).
        collection.AddKeyedSingleton<MessageLink.Presentation.Services.PrivateState>(ServerTokensKey, (sp, _) =>
            new(sp.GetRequiredService<IPrivateStateStore>(), sp.GetRequiredKeyedService<IAccessTokenStore>(ServerTokensKey)));
        collection.AddSingleton<IServerIdentity>(sp => ServerIdentityPort.Over(sp.GetRequiredKeyedService<IAccessTokenStore>(ServerTokensKey),
            sp.GetRequiredService<MessengerBackend>(), sp.GetRequiredService<InstagramBackend>(), sp.GetRequiredService<GoogleVoiceAccounts>()));
        collection.AddSingleton<GoogleMessagesBackend>();
        collection.AddSingleton<IGoogleMessagesAccount, GoogleMessagesAccountPort>();
        collection.AddSingleton<MessageLink.Presentation.Services.IRecentMessages>(sp => sp.GetRequiredService<GoogleMessagesBackend>());
        collection.AddSingleton<GoogleSignInKeeper>();
        collection.AddSingleton<GooglePendingFeatures>();
        collection.AddSingleton<GoogleMediaBackend>();
        collection.AddSingleton<ProviderAvatarReader>();
        collection.AddSingleton<GoogleArchive>();
        collection.AddSingleton<IChatBackend>(sp => sp.GetRequiredService<GoogleMessagesBackend>());
        collection.AddSingleton<ILocalChatPreferences, LocalChatPreferenceStore>();
        collection.AddSingleton<ILocalContactNames>(sp => new LocalContactNameStore(sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton<MessengerBackend>();
        collection.AddSingleton<IMessengerAccount, MessengerAccount>();
        collection.AddSingleton<InstagramBackend>();
        collection.AddSingleton<IInstagramAccount, InstagramAccountLifecycle>();
        collection.AddSingleton(sp => new MessageLink.App.Services.Hermes.HermesBackend(sp.GetRequiredService<IStateProtector>()));
        collection.AddSingleton<IHermesAccount>(sp => sp.GetRequiredService<MessageLink.App.Services.Hermes.HermesBackend>());
        collection.AddSingleton<IHermesPrompts>(sp => sp.GetRequiredService<MessageLink.App.Services.Hermes.HermesBackend>());
        collection.AddSingleton<ProviderAccounts>();
        collection.AddSingleton<ConversationOwners>();
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
        collection.AddSingleton<ServerDiagnosticsSource>();
        collection.AddSingleton<IServerDiagnostics>(sp => sp.GetRequiredService<ServerDiagnosticsSource>());
        collection.AddSingleton<IHelperRestarter>(sp => new ServerHelperRestarter(sp.GetRequiredService<GoogleMessagesBackend>(), sp.GetRequiredService<GoogleSignInKeeper>(),
            sp.GetRequiredService<MessengerBackend>(), sp.GetRequiredService<InstagramBackend>(), sp.GetRequiredService<GoogleVoiceAccounts>(), AppLog.Write));
        // What the incoming-photo and recent-photo services ask of the server: the concrete objects stay registered for a server that serves them over the
        // protocol, because the interfaces are what a client replaces with its proxies.
        collection.AddSingleton<IMediaPreparer>(sp => sp.GetRequiredService<MediaRouter>());
        collection.AddSingleton<IncomingCardRequester>(sp => new(sp.GetRequiredService<MessengerBackend>(), sp.GetRequiredService<GoogleMessagesBackend>(),
            new GoogleIncomingCards(sp.GetRequiredService<GoogleArchive>(), sp.GetRequiredKeyedService<IAccessTokenStore>(ServerTokensKey)), AppLog.Write));
        collection.AddSingleton<IIncomingCards>(sp => sp.GetRequiredService<IncomingCardRequester>());
        return collection;
    }
}
