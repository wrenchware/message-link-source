using System.Text.RegularExpressions;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// Where the server's image came from, as the image says (<c>MESSAGES_CHANNEL</c>, <c>MESSAGES_REVISION</c> and the build time, <c>MESSAGES_IMAGE_BUILT</c> or <c>MESSAGES_CHROME_BUILT</c>): build arguments the GitHub workflow and install-unraid.sh set. A value that is not the expected shape is not shown (null), never passed on as it is.
/// </summary>
internal sealed partial record ServerImage(string? Channel, string? Revision, DateTimeOffset? Built)
{
    public static ServerImage FromEnvironment(Func<string, string?> variable) =>
        new(variable("MESSAGES_CHANNEL") is { } channel && ChannelWord().IsMatch(channel) ? channel : null,
            variable("MESSAGES_REVISION") is { } revision && RevisionWord().IsMatch(revision) ? revision[..12] : null,
            ChromeBuild.ParseBuilt(variable("MESSAGES_IMAGE_BUILT")) ?? ChromeBuild.ParseBuilt(variable("MESSAGES_CHROME_BUILT")));

    // \z, not $: $ also matches before a final newline, and these values reach the owner's dialog.
    [GeneratedRegex(@"^[a-z][a-z0-9:._-]{0,63}\z")]
    private static partial Regex ChannelWord();

    [GeneratedRegex(@"^[0-9a-f]{12,40}\z")]
    private static partial Regex RevisionWord();
}

/// <summary>
/// The Linux server's administration (SWITCHBOARD.md 4.6, protocol 20), for its page and its devices' reads: what the server is, the
/// accounts it holds, and removing one of them.
/// <para>
/// Removing an account removes the server's copy and nothing else. It is the same as signing that account out on the server (its sign-in and the conversations it kept go; nothing is
/// sent to the service, so the account itself, the phone and every other device that signed in to it are untouched), with two differences. A Messenger account's device keys and
/// retained history (the helper's <c>accounts/&lt;key&gt;</c> folder on the keys volume, kept in place by a sign-out so a new sign-in reuses the device) are set aside under
/// <c>set-aside/</c> rather than deleted: they hold encrypted history that is never destroyed here, and moved out of the live root no later sign-in on the server can run the same device as a
/// PC that may hold it. And the originals of saved messages' photos stay: saved messages are every device's, and a removal must not take their pictures from the others.
/// </para>
/// <para>
/// Refused, with a fixed word, are an account that is part of an unfinished move (staged here, or held while a PC takes its files home) and Google Messages,
/// which has no sign-out: it is removed by unpairing the server on the phone. Removal, the account ports, restores and move file operations share the provider's lifecycle lock,
/// so a hold cannot claim files being removed and a sign-in cannot replace the identity being quarantined. Logged as fixed words only.
/// </para>
/// </summary>
internal sealed class LinuxServerAdmin(LinuxServerAdmin.Parts parts) : IServerAdmin
{
    /// <param name="App">The application version the server was built from.</param>
    /// <param name="ChromeSandbox">The latest reading of the server's Chrome (<see cref="ChromeSandboxWords"/>), or null when it runs none.</param>
    /// <param name="MessengerKey">The helper's key for the Messenger account saved here (its <c>accounts/</c> folder name), from its saved session, or null when none is known. Read before a
    /// sign-out deletes what it is read from.</param>
    /// <param name="GooglePaired">Whether Google Messages has a pairing saved here.</param>
    /// <param name="GoogleConnected">Whether Google Messages is connected now.</param>
    /// <param name="Move">The accounts that move with a PC, and <paramref name="Gate"/> whether each may run now; both null on a host without them.</param>
    /// <param name="LoginGate">The provider's lifecycle lock, shared with the account ports, restores and move file operations.</param>
    /// <param name="MessengerKeysRoot">The helper's private root on the keys volume (<c>&lt;keys&gt;/Messenger</c>).</param>
    /// <param name="EncryptedFlag">Messenger's <c>encrypted.on</c> beside its session, which goes with a device that is set aside.</param>
    internal sealed record Parts(string App, DateTimeOffset Started, ServerImage Image, Func<string?> ChromeSandbox,
        IMessengerAccount Messenger, Func<Task<string?>> MessengerKey, IInstagramAccount Instagram, IGoogleVoiceAccounts Voice, bool VoiceRuns, bool GoogleRuns,
        Func<bool> GooglePaired, Func<bool> GoogleConnected, IAccountMove? Move, IAccountGate? Gate, Func<string, SemaphoreSlim> LoginGate, string MessengerKeysRoot, TimeProvider Time,
        Action<string> Log, string? EncryptedFlag = null);

    public Task<HostStatus> StatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new HostStatus(parts.App, ProtocolVersion.Current, ProtocolVersion.Supported, parts.Started, parts.ChromeSandbox(), parts.Image.Channel, parts.Image.Revision, parts.Image.Built));

    public async Task<IReadOnlyList<ServerAccount>> AccountsAsync(CancellationToken cancellationToken = default)
    {
        var moving = await MovingAsync(cancellationToken).ConfigureAwait(false);
        var accounts = new List<ServerAccount>();
        if (parts.GoogleRuns && parts.GooglePaired())
            accounts.Add(new(ServerAccountProviders.GoogleMessages, parts.GoogleConnected() ? ServerAccountStates.Connected : ServerAccountStates.Paired, Removable: false));
        var messenger = parts.Messenger.State;
        if (moving.TryGetValue(MoveProviders.Messenger, out var messengerMove)) accounts.Add(new(ServerAccountProviders.Messenger, messengerMove, Removable: CanRemoveHeld(MoveProviders.Messenger)));
        else if (messenger.Saved || messenger.Connected) accounts.Add(new(ServerAccountProviders.Messenger, messenger.Connected ? ServerAccountStates.Connected : ServerAccountStates.Saved, Removable: true));
        var instagram = parts.Instagram.State;
        if (moving.TryGetValue(MoveProviders.Instagram, out var instagramMove)) accounts.Add(new(ServerAccountProviders.Instagram, instagramMove, Removable: CanRemoveHeld(MoveProviders.Instagram)));
        else if (instagram.Saved || instagram.Connected)
            accounts.Add(new(ServerAccountProviders.Instagram, instagram.Connected ? ServerAccountStates.Connected : ServerAccountStates.Saved, Removable: true, Name: instagram.AccountName));
        if (parts.VoiceRuns)
            foreach (var voice in parts.Voice.Accounts)
                accounts.Add(new(ServerAccountProviders.GoogleVoice, voice.Connected ? ServerAccountStates.Connected : ServerAccountStates.Saved, Removable: true, voice.Key, voice.Number));
        return accounts;
    }

    /// <summary>
    /// Told of an account removed here, as a client's sign-out tells the host (<c>ProtocolServices.AccountEnded</c>: <c>messenger</c>, <c>instagram</c>, or
    /// <c>voice</c> with its key), so what the host keeps of it goes too: the whole history's copies of its conversations (stage 8).
    /// </summary>
    public Action<string, string?>? Ended { get; set; }

    public async Task<AccountRemoved> RemoveAccountAsync(string provider, string? key, CancellationToken cancellationToken = default)
    {
        switch (provider)
        {
            case ServerAccountProviders.GoogleMessages:
                parts.Log("server account remove refused provider=google-messages reason=not_offered");
                throw new InvalidOperationException(AccountRemoveWords.NotOffered);
            case ServerAccountProviders.GoogleVoice:
                return Told(await RemoveVoiceAsync(key).ConfigureAwait(false), "voice", key);
            case ServerAccountProviders.Messenger:
            case ServerAccountProviders.Instagram:
                return Told(await RemoveMovableAsync(provider, cancellationToken).ConfigureAwait(false), provider == ServerAccountProviders.Messenger ? "messenger" : "instagram", null);
            default:
                parts.Log("server account remove refused reason=unknown");
                throw new InvalidOperationException(AccountRemoveWords.Unknown);
        }
    }

    private AccountRemoved Told(AccountRemoved removed, string account, string? key)
    {
        if (removed.Removed)
        {
            try { Ended?.Invoke(account, key); }
            catch (Exception) { /* a hook that fails must not undo the removal */ }
        }
        return removed;
    }

    /// <summary>How long the list waits for the move's own status, behind whatever a move is doing under its lock (staging a large device database, a verify), before it goes by the gate alone.</summary>
    public TimeSpan MoveStatusWait { get; init; } = TimeSpan.FromSeconds(3);

    // The move's status says staged (moving) or held for a PC (moved away); only AccountMoveHost can retire a released hold safely. A record that cannot be read is neither, but allows nothing either,
    // and is shown as moving: what is there may be a move's. A status that does not come in time, or cannot be read (it may write the record while it reads), leaves the gate's
    // answer: anything it does not allow is moving. The list is never refused for it.
    private async Task<Dictionary<string, string>> MovingAsync(CancellationToken cancellationToken)
    {
        var moving = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parts.Move is { } move)
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(MoveStatusWait);
            try
            {
                foreach (var entry in (await move.StatusAsync(new AccountMoveStatusParams(), bound.Token).ConfigureAwait(false)).Providers)
                {
                    if (entry.State == MoveStates.Held) moving[entry.Provider] = ServerAccountStates.MovedAway;
                    else if (entry.State is MoveStates.Installing or MoveStates.Installed or MoveStates.Activating or MoveStates.Unreadable) moving[entry.Provider] = ServerAccountStates.Moving;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { parts.Log("server accounts move_status=late"); }
            catch (Exception failure) when (failure is not OperationCanceledException) { parts.Log($"server accounts move_status=failed type={failure.GetType().Name}"); }
        }
        foreach (var provider in MoveProviders.All)
            if (parts.Gate?.Allows(provider) == false) moving.TryAdd(provider, ServerAccountStates.Moving);
        return moving;
    }

    private async Task<AccountRemoved> RemoveVoiceAsync(string? key)
    {
        if (!parts.VoiceRuns || string.IsNullOrEmpty(key))
        {
            parts.Log("server account remove refused provider=google-voice reason=unknown");
            throw new InvalidOperationException(AccountRemoveWords.Unknown);
        }
        // Only a listed account can be signed out (a saved one that never came back has nothing running to retire; GoogleVoiceAccountsPort).
        if (!parts.Voice.Accounts.Any(account => account.Key == key))
        {
            parts.Log("server account remove provider=google-voice outcome=none");
            return new(false);
        }
        await parts.Voice.SignOutAsync(key).ConfigureAwait(false);
        parts.Log("server account remove provider=google-voice outcome=removed");
        return new(true);
    }

    private bool CanRemoveHeld(string provider) => parts.Move is AccountMoveHost host && ReferenceEquals(parts.Gate, host) && host.CanRemoveHeld(provider);

    private async Task<AccountRemoved> RemoveMovableAsync(string provider, CancellationToken cancellationToken)
    {
        var gate = parts.LoginGate(provider);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A hold, connect, restore or activation takes this same provider lock. The key read, sign-out and quarantine therefore concern one identity and cannot delete a held session.
            if (parts.Gate?.Allows(provider) == false)
            {
                if (parts.Move is AccountMoveHost host && ReferenceEquals(parts.Gate, host))
                {
                    try
                    {
                        var removed = await host.RemoveReleasedHeldLockedAsync(provider,
                            accountKey => provider == MoveProviders.Messenger ? parts.Messenger.SignOutAsync()
                                : parts.Instagram is InstagramAccountLifecycle instagram ? instagram.SignOutMovedAwayAsync(accountKey) : parts.Instagram.SignOutAsync(), cancellationToken).ConfigureAwait(false);
                        if (removed is not null)
                        {
                            parts.Log($"server account remove provider={provider} outcome=removed");
                            return removed;
                        }
                    }
                    catch (InvalidOperationException refused) when (refused.Message == AccountRemoveWords.Moving) { throw Moving(provider); }
                }
                throw Moving(provider);
            }
            try
            {
                if (provider == MoveProviders.Instagram)
                {
                    if (parts.Instagram.State is { Saved: false, Connected: false }) { parts.Log("server account remove provider=instagram outcome=none"); return new(false); }
                    await parts.Instagram.SignOutAsync().ConfigureAwait(false);
                    parts.Log("server account remove provider=instagram outcome=removed");
                    return new(true);
                }
                var key = await parts.MessengerKey().ConfigureAwait(false);
                var device = key is null ? null : Path.Combine(parts.MessengerKeysRoot, "accounts", key);
                if (parts.Messenger.State is { Saved: false, Connected: false } && (device is null || !Directory.Exists(device)))
                {
                    parts.Log("server account remove provider=messenger outcome=none");
                    return new(false);
                }
                await parts.Messenger.SignOutAsync().ConfigureAwait(false);
                // Keep the device if a gate implementation without a lifecycle lock revoked access during this call.
                var kept = parts.Gate?.Allows(provider) == false ? "kept_moving" : SetAside(device, key);
                parts.Log($"server account remove provider=messenger outcome=removed device={kept}");
                return new(true);
            }
            catch (InvalidOperationException refused) when (refused.Message == AccountFailureWords.AccountMovedAway) { throw Moving(provider); }
        }
        finally { gate.Release(); }

        InvalidOperationException Moving(string which)
        {
            parts.Log($"server account remove refused provider={which} reason=moving");
            return new InvalidOperationException(AccountRemoveWords.Moving);
        }
    }

    // The device's folder out of the live root, kept: set_aside, none (nothing there, or no key known) or failed (the sign-out stands; a later sign-in here would reuse the device).
    // Its encryption flag goes into the folder first, as a move's set-aside takes it (AccountMoveHost.SetAside): left behind, it said encryption was on for a server signed out of
    // Messenger, and a later sign-in there began with a reconnect to a device that is not there.
    private string SetAside(string? device, string? key)
    {
        if (device is null || key is null || !Directory.Exists(device)) return "none";
        try
        {
            if (parts.EncryptedFlag is { } flag && File.Exists(flag)) File.Move(flag, Path.Combine(device, Path.GetFileName(flag)), overwrite: true);
            var aside = Path.Combine(parts.MessengerKeysRoot, "set-aside");
            Directory.CreateDirectory(aside);
            Directory.Move(device, Path.Combine(aside, $"{key}-removed-{parts.Time.GetUtcNow().ToUnixTimeMilliseconds()}"));
            return "set_aside";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return "failed"; }
    }
}
