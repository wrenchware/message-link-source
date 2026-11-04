using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// The Google Messages account behind IGoogleMessagesAccount, so a window (or a remote client, later) manages it without
// holding GoogleMessagesBackend (SWITCHBOARD.md 2.6a). Every call is the backend's own; nothing here decides anything.
// Renewal, pairing and reconnect behaviour stay in the backend and GoogleSignInKeeper.
internal sealed class GoogleMessagesAccountPort(GoogleMessagesBackend google, GoogleMediaBackend media) : IGoogleMessagesAccount
{
    public GoogleMessagesAccountState State => new(google.PairingLost, google.SignInExpired, google.SignInHeld, google.SignInSettling,
        google.LastSignInFailure, google.DefaultSmsLine, google.HistoryIndexingEnabled);

    public event EventHandler? Changed
    {
        add => google.SignInStateChanged += value;
        remove => google.SignInStateChanged -= value;
    }

    public event EventHandler? SimsChanged
    {
        add => google.SimsChanged += value;
        remove => google.SimsChanged -= value;
    }

    public event EventHandler? DefaultSmsAppChanged
    {
        add => google.DefaultSmsAppChanged += value;
        remove => google.DefaultSmsAppChanged -= value;
    }

    public event EventHandler<bool>? RenumberedChanged
    {
        add => google.RenumberedChanged += value;
        remove => google.RenumberedChanged -= value;
    }

    public Task SignInAsync(IReadOnlyDictionary<string, string> cookies, IProgress<string> pairingEmoji, CancellationToken cancellationToken, bool asNewDevice = false) =>
        google.SignInAsync(cookies, pairingEmoji, cancellationToken, asNewDevice);

    public GoogleSimChip? SimFor(string? chatId) => google.SimFor(chatId) is { } sim ? new(sim.Label, sim.Color) : null;
    public Task SetHistoryIndexingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        google.HistoryIndexingEnabled = enabled;
        // Saved here, where the backend runs, because that is where its next start reads it. Saved by the window it went to the PC's folder, and a server came back with the download on (audit, 2026-11-01).
        NativeBeeper.App.Services.SearchIndexPreference.Save(enabled);
        return Task.CompletedTask;
    }

    public Task LoadRenumberedAsync() => google.LoadRenumberedAsync();
    public void WarmArchive() => google.WarmArchive();
    public IReadOnlyList<ChatSummary> KnownConversations => google.KnownConversationSummaries;

    public Task<IReadOnlyList<GoogleKnownChat>> KnownChatsForResetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<GoogleKnownChat>>([.. google.KnownChatsSnapshot().Select(chat => new GoogleKnownChat(chat.Id, chat.Group,
            [.. chat.Participants.Select(person => new GoogleKnownPerson(person.Id, person.NativeId, person.Self))]))]);

    // The keys Google's avatar cache files a picture, its "no picture" marker and its checked/failed bookkeeping under.
    public Task<IReadOnlyList<string>> AvatarCacheKeysAsync(IReadOnlyList<string> nativeIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. nativeIds.SelectMany(native => new[] { "avatar:" + native, "avatar-photo:" + native, GoogleMediaBackend.PhotoKey + native, "avatar-none:" + native, GoogleMediaBackend.NoPhotoKey + native, "avatar-checked:" + native, "avatar-failed:" + native })]);

    public Task ForgetAvatarsAsync(CancellationToken cancellationToken = default) => media.ForgetAvatarsAsync();
    public Task ResetLocalAsync(string profile) => google.ResetLocalAsync(profile);
}
