using NativeBeeper.App;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// Google Messages' account on a host whose sign-ins are made in a client's window (SWITCHBOARD.md 2.6c, 4.3). The window keeps the browser profile it
/// signed in with on the client's own PC (<c>GoogleMessagesBrowserProfile.KeepAsync</c>), where this host cannot load it; so once the host has accepted
/// the sign-in, it seeds a profile of its own from the same cookies and loads it once (<see cref="ChromiumGoogleBrowser.CreateProfileWithReasonAsync"/>).
/// That profile is what <see cref="GoogleSignInKeeper"/> renews from here, ten minutes before Google's two hours.
/// <para>
/// The sign-in stands whatever the seeding does: a profile that could not be made is logged as a fixed word, and the keeper then finds no profile
/// when the two hours come and asks the user to renew, as it does today on a PC whose profile was lost. Nothing here retries against Google.
/// Everything else is the backend's own port, unchanged.
/// </para>
/// </summary>
internal sealed class ProfileKeepingGoogleAccount(IGoogleMessagesAccount inner, ChromiumGoogleBrowser browser, SignInGuard? guard = null, bool refuseHandOver = false) : IGoogleMessagesAccount
{
    private readonly SignInGuard? signInGuard = guard;
    private readonly bool refusesHandOver = refuseHandOver;

    /// <summary>A seeding that has not finished by now gives up; the sign-in has already succeeded.</summary>
    public TimeSpan SeedBudget { get; init; } = TimeSpan.FromSeconds(45);

    public GoogleMessagesAccountState State => inner.State;
    public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
    public event EventHandler? SimsChanged { add => inner.SimsChanged += value; remove => inner.SimsChanged -= value; }
    public event EventHandler? DefaultSmsAppChanged { add => inner.DefaultSmsAppChanged += value; remove => inner.DefaultSmsAppChanged -= value; }
    public event EventHandler<bool>? RenumberedChanged { add => inner.RenumberedChanged += value; remove => inner.RenumberedChanged -= value; }

    public Task SignInAsync(IReadOnlyDictionary<string, string> cookies, IProgress<string> pairingEmoji, CancellationToken cancellationToken, bool asNewDevice = false) =>
        SignInAsync(cookies, pairingEmoji, cancellationToken, asNewDevice, null);

    /// <summary>
    /// With <paramref name="profileCookies"/> (everything the client's sign-in browser held, <see cref="GoogleProfileCookie"/>) the new profile is seeded with all of
    /// them, as that browser had them; without, with the helper's cookies alone, as before. The backend is given the helper's cookies either way and never sees these.
    /// </summary>
    public async Task SignInAsync(IReadOnlyDictionary<string, string> cookies, IProgress<string> pairingEmoji, CancellationToken cancellationToken, bool asNewDevice,
        IReadOnlyList<GoogleProfileCookie>? profileCookies)
    {
        // A server that signs in in its own browser has proven this hand-over unrenewable (SWITCHBOARD.md 5.4: Google binds a sign-in to the browser that made it), so it is refused before
        // anything is done with the cookies; the client uses the remote sign-in there.
        if (refusesHandOver) throw new InvalidOperationException("sign_in_use_server");
        // And no two Google sign-ins interleave: this one holds the guard through the backend's sign-in and the profile it keeps, as a remote one does through its own.
        using var lease = signInGuard?.TryEnter();
        if (signInGuard is not null && lease is null) throw new InvalidOperationException("sign_in_busy");
        // Copied before the backend sees them: the caller's dictionary is the request's, and is cleared with it.
        var copy = new Dictionary<string, string>(cookies, StringComparer.Ordinal);
        try
        {
            await inner.SignInAsync(cookies, pairingEmoji, cancellationToken, asNewDevice).ConfigureAwait(false);
            await KeepProfileAsync(copy, profileCookies, cancellationToken).ConfigureAwait(false);
        }
        finally { copy.Clear(); }
    }

    private async Task KeepProfileAsync(Dictionary<string, string> cookies, IReadOnlyList<GoogleProfileCookie>? profileCookies, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(SeedBudget);
        try
        {
            var (made, reason) = await browser.CreateProfileWithReasonAsync(GoogleSignInProfile.MessagesService, cookies, bounded.Token, profileCookies).ConfigureAwait(false);
            if (made is null)
            {
                GoogleMessagesLog.Write($"browser profile=not_seeded reason={reason}");
                return;
            }
            await using (made.ConfigureAwait(false)) await GoogleMessagesBrowserProfile.KeepAsync(made).ConfigureAwait(false);
            GoogleMessagesLog.Write("browser profile=seeded");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { GoogleMessagesLog.Write("browser profile=not_seeded reason=timeout"); }
        // The sign-in is done; a browser that failed takes only the silent renewal with it. The type only.
        catch (Exception failure) when (failure is not OperationCanceledException) { GoogleMessagesLog.Write($"browser profile=not_seeded reason=error type={failure.GetType().Name}"); }
    }

    public GoogleSimChip? SimFor(string? chatId) => inner.SimFor(chatId);
    public Task SetHistoryIndexingAsync(bool enabled, CancellationToken cancellationToken = default) => inner.SetHistoryIndexingAsync(enabled, cancellationToken);
    public Task LoadRenumberedAsync() => inner.LoadRenumberedAsync();
    public void WarmArchive() => inner.WarmArchive();
    public IReadOnlyList<ChatSummary> KnownConversations => inner.KnownConversations;
    public Task<IReadOnlyList<GoogleKnownChat>> KnownChatsForResetAsync(CancellationToken cancellationToken = default) => inner.KnownChatsForResetAsync(cancellationToken);
    public Task<IReadOnlyList<string>> AvatarCacheKeysAsync(IReadOnlyList<string> nativeIds, CancellationToken cancellationToken = default) => inner.AvatarCacheKeysAsync(nativeIds, cancellationToken);
    public Task ForgetAvatarsAsync(CancellationToken cancellationToken = default) => inner.ForgetAvatarsAsync(cancellationToken);
    public Task ResetLocalAsync(string profile) => inner.ResetLocalAsync(profile);
}
