using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

// Client-side stand-ins for the four account ports (SWITCHBOARD.md 2.6c). They hold no object of the host's: everything goes over
// the wire or is read from the host's pushed state.
//
//   what is read       the snapshot and Changed come from the pushed state (HostStateCache), the events no state shows (SimsChanged,
//                      DefaultSmsAppChanged, RenumberedChanged, ReconnectAbandoned) from the account signals the host appends to its
//                      log, and each question per call is a request.
//   what is done       every action that changes an account or a session is a journaled mutation (accounts.<service>.<verb>): one
//                      operation id per call, never retried, an interrupted one an unknown outcome. The host appends the state the action
//                      caused before it answers and the client applies a state the moment it reads it, so State read right after an
//                      action is already the new one: nothing here settles anything.
//   what cannot wait   SimFor, AnswersFromMemory and KnownConversations are synchronous members read on a window's thread, so a
//                      round trip is not an option. Each answers from a copy of the host's answer (Remembered) with the conservative
//                      value until the host has been asked: no SIM chip, "not in memory" (so Up does not offer an Instagram edit that
//                      would read the thread), nothing extra in the merge picker. A copy that changes tells its listeners.
//   credentials        cookies and the pairing result are the parameters of the sign-in mutation and nothing else. The saved session is
//                      never asked for: connecting with it is "connect with no cookies" and the host reads its own file.

internal static class AccountWire
{
    /// <summary>
    /// A mutation on an account. A cancel that ends it is the caller's own cancel, which a direct call reports as
    /// <see cref="OperationCanceledException"/>: the unknown outcome the host records for a cancel after the start is not the caller's news.
    /// </summary>
    public static async Task<TResult> MutateAsync<TParams, TResult>(ProtocolClient client, string method, TParams parameters, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null) where TParams : MutationParams
    {
        try { return await client.MutateAsync<TParams, TResult>(method, parameters, cancellationToken, progress).ConfigureAwait(false); }
        catch (SendException ex) when (ex.OutcomeUnknown && cancellationToken.IsCancellationRequested) { throw new OperationCanceledException(cancellationToken); }
    }

    public static Task MutateAsync<TParams>(ProtocolClient client, string method, TParams parameters, CancellationToken cancellationToken = default)
        where TParams : MutationParams => MutateAsync<TParams, Done>(client, method, parameters, cancellationToken);

    public static string OperationId() => ProtocolClient.NewOperationId();

    /// <summary>
    /// A restore that stops when the caller's flag does. The flag is a function, which cannot cross; the call is cancelled instead, and
    /// the host's own flag is that cancel. Returns false when it was stopped, whatever became of the restore: nobody is waiting for it.
    /// </summary>
    public static async Task<(bool Stopped, TResult? Result)> RestoreAsync<TResult>(Func<bool> stopped, Func<CancellationToken, Task<TResult>> call)
    {
        using var stop = new CancellationTokenSource();
        using var watch = new Timer(_ =>
        {
            try { if (stopped()) stop.Cancel(); }
            catch (Exception) { /* a flag that throws is not a stop */ }
        }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        try { return (false, await call(stop.Token).ConfigureAwait(false)); }
        catch (Exception) when (stop.IsCancellationRequested) { return (true, default); }
    }
}

/// <summary>Messenger's account over the wire: its snapshot and <c>Changed</c> from the host's state, every action as a mutation.</summary>
public sealed class MessengerAccountProxy(ProtocolClient client, HostStateCache cache) : IMessengerAccount
{
    public MessengerAccountState State => cache.Messenger;

    public event EventHandler? Changed
    {
        add => cache.MessengerChanged += value;
        remove => cache.MessengerChanged -= value;
    }

    public Task<bool> HasUsableSavedSessionAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<bool>(ProtocolMethods.AccountsMessengerHasSavedSession, cancellationToken);

    // Cookies, when there are any, are this call's parameters and nothing else; none means the saved session, which the host reads itself.
    public Task<int> ConnectAsync(Dictionary<string, string>? cookies, CancellationToken cancellationToken = default) =>
        AccountWire.MutateAsync<MessengerConnectParams, int>(client, ProtocolMethods.AccountsMessengerConnect, new(AccountWire.OperationId(), cookies), cancellationToken);

    public async Task<AccountRestore> RestoreAsync(Func<bool> stopped)
    {
        var (wasStopped, result) = await AccountWire.RestoreAsync(stopped, token =>
            AccountWire.MutateAsync<AccountOpParams, AccountRestore>(client, ProtocolMethods.AccountsMessengerRestore, new(AccountWire.OperationId()), token)).ConfigureAwait(false);
        return wasStopped || result is null ? new AccountRestore(null, Skipped: true) : result;
    }

    public Task<string> DescribeAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<string>(ProtocolMethods.AccountsMessengerDescribe, cancellationToken);

    public Task<bool> RefreshEncryptedStateAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<bool>(ProtocolMethods.AccountsMessengerRefreshEncrypted, cancellationToken);

    public async Task<string?> EnableEncryptedAsync(CancellationToken cancellationToken = default) =>
        (await AccountWire.MutateAsync<AccountOpParams, FailureCode>(client, ProtocolMethods.AccountsMessengerEnableEncrypted, new(AccountWire.OperationId()), cancellationToken).ConfigureAwait(false)).Code;

    public async Task<string?> EnableEncryptedAfterSignInAsync() =>
        (await AccountWire.MutateAsync<AccountOpParams, FailureCode>(client, ProtocolMethods.AccountsMessengerEnableEncryptedAfterSignIn, new(AccountWire.OperationId())).ConfigureAwait(false)).Code;

    public Task SignOutAsync() => AccountWire.MutateAsync(client, ProtocolMethods.AccountsMessengerSignOut, new AccountOpParams(AccountWire.OperationId()));
}

/// <summary>Instagram's account over the wire.</summary>
public sealed class InstagramAccountProxy : IInstagramAccount
{
    private readonly ProtocolClient client;
    private readonly HostStateCache cache;
    private readonly Remembered<(string Chat, string Message), bool> held;

    /// <param name="memoryLifetime">How long an answer to <see cref="AnswersFromMemory"/> stands before it is asked again.</param>
    public InstagramAccountProxy(ProtocolClient client, HostStateCache cache, TimeSpan? memoryLifetime = null)
    {
        this.client = client;
        this.cache = cache;
        // Not in memory until the host says so: Up then does nothing rather than read the thread on a key press (EditTarget).
        held = new(key => client.CallAsync<MessageIdParams, bool>(ProtocolMethods.AccountsInstagramAnswersFromMemory, new(key.Chat, key.Message)),
            false, memoryLifetime ?? TimeSpan.FromSeconds(5));
    }

    public InstagramAccountState State => cache.Instagram;

    public event EventHandler? Changed
    {
        add => cache.InstagramChanged += value;
        remove => cache.InstagramChanged -= value;
    }

    // The host stops retrying, which is no change of any flag; it announces it as an account signal.
    public event EventHandler? ReconnectAbandoned
    {
        add => cache.ReconnectAbandoned += value;
        remove => cache.ReconnectAbandoned -= value;
    }

    public Task<int> ResumeAsync(CancellationToken cancellationToken = default) =>
        AccountWire.MutateAsync<AccountOpParams, int>(client, ProtocolMethods.AccountsInstagramResume, new(AccountWire.OperationId()), cancellationToken);

    public Task<int> ConnectAsync(Dictionary<string, string> cookies, CancellationToken cancellationToken = default) =>
        AccountWire.MutateAsync<InstagramConnectParams, int>(client, ProtocolMethods.AccountsInstagramConnect, new(AccountWire.OperationId(), cookies), cancellationToken);

    public async Task<AccountRestore> RestoreAsync(Func<bool> stopped)
    {
        var (wasStopped, result) = await AccountWire.RestoreAsync(stopped, token =>
            AccountWire.MutateAsync<AccountOpParams, AccountRestore>(client, ProtocolMethods.AccountsInstagramRestore, new(AccountWire.OperationId()), token)).ConfigureAwait(false);
        // A restore that stopped because the host is closing says nothing, and the window uses its generic line.
        return wasStopped || result is null ? new AccountRestore(null) : result;
    }

    public Task SignOutAsync() => AccountWire.MutateAsync(client, ProtocolMethods.AccountsInstagramSignOut, new AccountOpParams(AccountWire.OperationId()));

    // Per message, and about what the host holds in memory: read from a copy of the host's answer (see the header).
    public bool AnswersFromMemory(string chatId, string messageId) => held.Get((chatId, messageId));

}

/// <summary>Google Messages' account over the wire.</summary>
public sealed class GoogleMessagesAccountProxy : IGoogleMessagesAccount
{
    private readonly ProtocolClient client;
    private readonly HostStateCache cache;
    private readonly Remembered<string, GoogleSimChip?> sims;
    private readonly Remembered<int, IReadOnlyList<ChatSummary>> known;

    /// <param name="lifetime">How long a copy of a per-conversation answer (a SIM, the known conversations) stands before it is asked again.</param>
    public GoogleMessagesAccountProxy(ProtocolClient client, HostStateCache cache, TimeSpan? lifetime = null)
    {
        this.client = client;
        this.cache = cache;
        var age = lifetime ?? TimeSpan.FromSeconds(3);
        // A conversation moved to the other SIM on the phone arrives as a changed summary, which asks again; an answer that differs
        // is a change of SIMs to everyone listening, as the host's own announcement is.
        sims = new(async chat => await client.CallNullableAsync<SimForParams, GoogleSimChip>(ProtocolMethods.AccountsGoogleSimFor, new(chat.Length == 0 ? null : chat)).ConfigureAwait(false),
            null, age, (_, _, _) => SimsChanged?.Invoke(this, EventArgs.Empty));
        known = new(_ => client.CallAsync<IReadOnlyList<ChatSummary>>(ProtocolMethods.AccountsGoogleKnownConversations), [], age * 2);
        cache.SimsChanged += (_, args) =>
        {
            // What was said about each conversation is old now, and still shown until it is asked again.
            sims.MarkStale();
            SimsChanged?.Invoke(this, args);
        };
    }

    public GoogleMessagesAccountState State => cache.Google;

    public event EventHandler? Changed
    {
        add => cache.GoogleChanged += value;
        remove => cache.GoogleChanged -= value;
    }

    public event EventHandler? SimsChanged;

    public event EventHandler? DefaultSmsAppChanged
    {
        add => cache.DefaultSmsAppChanged += value;
        remove => cache.DefaultSmsAppChanged -= value;
    }

    public event EventHandler<bool>? RenumberedChanged
    {
        add => cache.RenumberedChanged += value;
        remove => cache.RenumberedChanged -= value;
    }

    // The cookies and the pairing result are this mutation's parameters. The emoji the phone asks to be matched come back as progress
    // frames for this call alone, in order, and are handed to the caller's sink.
    public Task SignInAsync(IReadOnlyDictionary<string, string> cookies, IProgress<string> pairingEmoji, CancellationToken cancellationToken, bool asNewDevice = false) =>
        SignInAsync(cookies, pairingEmoji, cancellationToken, asNewDevice, null);

    // The browser's other Google cookies, when the caller has them, are parameters of the same mutation and nothing else (they are credentials too). With none, the
    // request is the one an older client wrote: a null member is not written.
    public Task SignInAsync(IReadOnlyDictionary<string, string> cookies, IProgress<string> pairingEmoji, CancellationToken cancellationToken, bool asNewDevice,
        IReadOnlyList<GoogleProfileCookie>? profileCookies) =>
        AccountWire.MutateAsync<GoogleSignInParams, Done>(client, ProtocolMethods.AccountsGoogleSignIn,
            new(AccountWire.OperationId(), cookies as Dictionary<string, string> ?? new Dictionary<string, string>(cookies), asNewDevice, profileCookies is { Count: > 0 } ? profileCookies : null), cancellationToken, pairingEmoji);

    /// <summary>The one sentence Settings shows when the server's Chrome is out of date (protocol 14), or null. A host that does not say so, or speaks less than 14, gets none.</summary>
    public string? ChromeOutOfDateSentence => cache.Limits.ChromeOutOfDate && client.Version is >= ProtocolVersion.ChromeAge ? RemoteSignIn.ChromeOutOfDateSentence : null;

    // Per conversation: read from a copy of the host's answer (see the header).
    public GoogleSimChip? SimFor(string? chatId) => sims.Get(chatId ?? "");

    public Task SetHistoryIndexingAsync(bool enabled, CancellationToken cancellationToken = default) =>
        AccountWire.MutateAsync(client, ProtocolMethods.AccountsGoogleSetHistoryIndexing, new GoogleIndexingParams(AccountWire.OperationId(), enabled), cancellationToken);

    public Task LoadRenumberedAsync() => client.CallAsync<Done>(ProtocolMethods.AccountsGoogleLoadRenumbered);

    // Best effort and nothing to wait for: the archive stops pacing its warm-up whenever this reaches it.
    public void WarmArchive() => _ = Task.Run(async () =>
    {
        try { await client.CallAsync<Done>(ProtocolMethods.AccountsGoogleWarmArchive).ConfigureAwait(false); }
        catch (Exception) { /* a warm-up that did not start is only slower */ }
    });

    // Conversations, not flags: a copy of the host's list, refreshed when it is read after its lifetime.
    public IReadOnlyList<ChatSummary> KnownConversations => known.Get(0);

    public Task<IReadOnlyList<GoogleKnownChat>> KnownChatsForResetAsync(CancellationToken cancellationToken = default) =>
        client.CallAsync<IReadOnlyList<GoogleKnownChat>>(ProtocolMethods.AccountsGoogleKnownChats, cancellationToken);

    public Task<IReadOnlyList<string>> AvatarCacheKeysAsync(IReadOnlyList<string> nativeIds, CancellationToken cancellationToken = default) =>
        client.CallAsync<AvatarKeysParams, IReadOnlyList<string>>(ProtocolMethods.AccountsGoogleAvatarKeys, new(nativeIds), cancellationToken);

    public Task ForgetAvatarsAsync(CancellationToken cancellationToken = default) =>
        AccountWire.MutateAsync(client, ProtocolMethods.AccountsGoogleForgetAvatars, new AccountOpParams(AccountWire.OperationId()), cancellationToken);

    public Task ResetLocalAsync(string profile) =>
        AccountWire.MutateAsync(client, ProtocolMethods.AccountsGoogleReset, new GoogleResetParams(AccountWire.OperationId(), profile));
}

/// <summary>The Google Voice accounts over the wire: the list and <c>Changed</c> from the host's state, restore, add and sign out as mutations.</summary>
public sealed class GoogleVoiceAccountsProxy(ProtocolClient client, HostStateCache cache) : IGoogleVoiceAccounts
{
    public IReadOnlyList<GoogleVoiceAccountState> Accounts => cache.VoiceAccounts;
    public IReadOnlyList<string> KnownAccountKeys => cache.VoiceKnownKeys;

    public event EventHandler? Changed
    {
        add => cache.VoiceChanged += value;
        remove => cache.VoiceChanged -= value;
    }

    public Task<IReadOnlyList<string>> RestoreAsync(CancellationToken cancellationToken = default) =>
        AccountWire.MutateAsync<AccountOpParams, IReadOnlyList<string>>(client, ProtocolMethods.AccountsVoiceRestore, new(AccountWire.OperationId()), cancellationToken);

    public Task<GoogleVoiceAccountState> AddAsync(Dictionary<string, string> cookies, string profileId, CancellationToken cancellationToken = default) =>
        AccountWire.MutateAsync<VoiceAddParams, GoogleVoiceAccountState>(client, ProtocolMethods.AccountsVoiceAdd, new(AccountWire.OperationId(), cookies, profileId), cancellationToken);

    public Task SignOutAsync(string key) =>
        AccountWire.MutateAsync(client, ProtocolMethods.AccountsVoiceSignOut, new VoiceSignOutParams(AccountWire.OperationId(), key));

}
