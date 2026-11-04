using System.Text.RegularExpressions;
using System.Threading.Channels;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

// The account methods of the host (SWITCHBOARD.md 2.6c): what the four account ports do, served from the objects that own the
// sessions. A mutation (connect, sign-in, restore, sign out, encryption on, reset, history indexing, forgetting avatars) is journaled
// like a send: at most once per client operation id, an interrupted one an unknown outcome, never retried. An account action's answer
// follows the state it caused (SettleAsync), and a sign-in's progress reaches only the connection that made the call.
//
// Credentials. The cookies of a sign-in and the pairing result are parameters of that one mutation. Nothing here writes a parameter
// anywhere: the journal keeps a hash (OperationJournal.Fingerprint) and the answer, the answer carries a count or a snapshot, an
// error carries a fixed sentence or a fixed code word (AccountFailure), and the only log lines of an account call are the shared
// "protocol journal ..." vocabulary and a refusal to any device on a server (PageOnly), which names the method and nothing else.
public sealed partial class ProtocolDispatcher
{
    /// <summary>The most a connection waits for its feed to carry a state before it answers anyway.</summary>
    public static TimeSpan SettleLimit { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most people one <see cref="ProtocolMethods.ContactsFindDirect"/> looks up.</summary>
    public const int MaxLookups = 64;

    // A connect or sign-in that fails for a reason the window explains in its own words says it by a fixed code word (Messenger's
    // "missing_cookies", the helper's "login_failed", "win32_5"), as an InvalidOperationException. That crosses as an account_failed
    // error whose message is the word; a message of any other shape (a sentence, a path, a value) is not carried, and becomes the
    // generic internal error like any other exception. The words come from the app's own code, never from what a person or a service sent.
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex CodeWord();

    /// <summary>Whether <paramref name="text"/> is a fixed code word and so safe to carry as an <c>account_failed</c> message.</summary>
    public static bool IsCodeWord(string? text) => text is not null && CodeWord().IsMatch(text);

    private static async Task<T> Coded<T>(Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (InvalidOperationException ex) when (IsCodeWord(ex.Message))
        {
            throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.AccountFailed, ex.Message));
        }
    }

    // A journaled mutation on an account port, which settles the state it changes before it answers.
    private static Route AccountMutation<TPort, TParams, TResult>(Func<ProtocolServices, TPort?> port, Func<TPort, TParams, CancellationToken, Task<TResult>> call)
        where TPort : class where TParams : MutationParams
        => Mutation<TPort, TParams, TResult>(port, (_, target, parameters, _, cancellationToken) => Coded(() => call(target, parameters, cancellationToken)), null, true);

    private static Route AccountMutation<TPort, TParams, TResult>(Func<ProtocolServices, TPort?> port,
        Func<TPort, TParams, IProgress<string>, CancellationToken, Task<TResult>> call)
        where TPort : class where TParams : MutationParams
        => Mutation<TPort, TParams, TResult>(port, (_, target, parameters, progress, cancellationToken) => Coded(() => call(target, parameters, progress, cancellationToken)), null, true);

    // A restore of a provider the host restores itself answers the host's, and is waited for if the host's is still under way: a second login on one
    // account (Messenger and Instagram above all) is the harm this exists to prevent. The host's slot exists from the moment it is composed, so a client
    // that asks before the host's restore has begun waits for it rather than starting its own. Cancelling the call stops the wait, never the host's restore.
    private static Route RestoreMutation<TPort, TResult>(Func<ProtocolServices, TPort?> port, string provider, TResult skipped, Func<TPort, CancellationToken, Task<TResult>> restore)
        where TPort : class
        => Mutation<TPort, AccountOpParams, TResult>(port, (services, target, _, _, cancellationToken) =>
            Coded(() => services.Restores?.Joined(provider, skipped) is { } host ? host.WaitAsync(cancellationToken) : restore(target, cancellationToken)), null, true);

    // Connecting Google Messages while the host's own connect is still under way would start a second one beside it. The wait ends however the host's
    // ended (a failed connect is the host's to report, and the call then does what it always did), and a connect asked for later is the user's own.
    private static async Task WaitForHostGoogleAsync(ProtocolServices services, CancellationToken cancellationToken)
    {
        if (services.Restores?.Joined(RestoreOnce.Google, false) is not { } hosting || hosting.IsCompleted) return;
        try { await hosting.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
    }

    // An account was signed out (or Google Messages' local data reset) and the host has done it: the host's own part that follows (it keeps the saved photos'
    // originals for its clients, HostSavedPhotos) is told, as the window tells its own (App.AccountSignedOut). A host that keeps nothing is told nothing, and a
    // part that fails is never the sign-out's.
    private static void Ended(ProtocolServices services, string account, string? key)
    {
        try { services.AccountEnded?.Invoke(account, key); }
        catch (Exception) { }
    }

    private static async Task<Done> Void(Task task)
    {
        await task.ConfigureAwait(false);
        return new Done();
    }

    // ---- Where an account changes (owner, 2026-11-03) ----

    // A server's accounts are connected and removed on its page, so no device changes one over the door: not a sign-in handed up, one in the server's own browser, a pairing by QR code,
    // a sign-out, or registering the server as a Messenger device. Every paired device keeps what keeps an account connected with what the server already holds: a restore, Instagram's
    // resume, Messenger's connect with the saved session, Voice's restore. Google Messages' local reset, its history indexing and its avatars are left to any device too: they change
    // what the server keeps, not the account, and the reset's caller has already cleared the shared rows by the time it asks (MainWindow.GoogleReset), so a refusal there would leave it
    // half done. A host with no paired devices (the app's own, a local pipe, the loopback) has one user, who goes on as before.
    private const string AccountRefusal = "Accounts are connected and removed on the server's page.";

    // The mutation `route`, refused to every device on a server. Checked after the route's own checks (its params, its port, its version) and before anything else on every
    // call, a journal replay included (as host.removeAccount is): a refused request never reaches the journal or the port, and leaves one fixed line naming `method`.
    // `applies`: whether this request changes the account at all (a Messenger connect with no cookies reconnects the saved session, as a restore does).
    private static Route PageOnly(string method, Route route, Func<RequestFrame, bool>? applies = null)
    {
        var prepare = route.Prepare ?? throw new InvalidOperationException(method + " is not a mutation");
        return route with
        {
            Prepare = (connection, services, request) =>
            {
                var prepared = prepare(connection, services, request);
                if (applies is not null && !applies(request)) return prepared;
                var before = prepared.Before;
                return prepared with
                {
                    Before = async cancellationToken =>
                    {
                        connection.RefuseOnServer("account", AccountRefusal, method);
                        if (before is not null) await before(cancellationToken).ConfigureAwait(false);
                    },
                };
            },
        };
    }

    private static Dictionary<string, Route> AddAccountRoutes(Dictionary<string, Route> routes)
    {
        // ---- Messenger ----
        routes[ProtocolMethods.AccountsMessengerHasSavedSession] = Of<IMessengerAccount, bool>(s => s.MessengerAccount, (port, ct) => port.HasUsableSavedSessionAsync(ct));
        routes[ProtocolMethods.AccountsMessengerDescribe] = Of<IMessengerAccount, string>(s => s.MessengerAccount, (port, ct) => port.DescribeAsync(ct));
        // A read that can move the encrypted connection's state, so its answer follows the state like an action's.
        routes[ProtocolMethods.AccountsMessengerRefreshEncrypted] = Of<IMessengerAccount, bool>(s => s.MessengerAccount, (port, ct) => port.RefreshEncryptedStateAsync(ct)) with { Settles = true };
        routes[ProtocolMethods.AccountsMessengerConnect] = PageOnly(ProtocolMethods.AccountsMessengerConnect, AccountMutation<IMessengerAccount, MessengerConnectParams, int>(
            s => s.MessengerAccount, (port, p, ct) => port.ConnectAsync(p.Cookies, ct)), request => ReadParams<MessengerConnectParams>(request).Cookies is not null);
        // The caller's "stop" is the request's cancel (the client turns its own flag into one), so the host's is the token.
        routes[ProtocolMethods.AccountsMessengerRestore] = RestoreMutation<IMessengerAccount, AccountRestore>(s => s.MessengerAccount, RestoreOnce.Messenger,
            new AccountRestore(null, Skipped: true), (port, ct) => port.RestoreAsync(() => ct.IsCancellationRequested));
        // Turning encrypted conversations on registers the server as one of the account's Messenger devices.
        routes[ProtocolMethods.AccountsMessengerEnableEncrypted] = PageOnly(ProtocolMethods.AccountsMessengerEnableEncrypted,
            AccountMutation<IMessengerAccount, AccountOpParams, FailureCode>(s => s.MessengerAccount,
                async (port, _, ct) => new FailureCode(await port.EnableEncryptedAsync(ct).ConfigureAwait(false))));
        routes[ProtocolMethods.AccountsMessengerEnableEncryptedAfterSignIn] = PageOnly(ProtocolMethods.AccountsMessengerEnableEncryptedAfterSignIn,
            AccountMutation<IMessengerAccount, AccountOpParams, FailureCode>(s => s.MessengerAccount,
                async (port, _, _) => new FailureCode(await port.EnableEncryptedAfterSignInAsync().ConfigureAwait(false))));
        routes[ProtocolMethods.AccountsMessengerSignOut] = PageOnly(ProtocolMethods.AccountsMessengerSignOut, Mutation<IMessengerAccount, AccountOpParams, Done>(s => s.MessengerAccount,
            (services, port, _, _, _) => Coded(async () => { await port.SignOutAsync().ConfigureAwait(false); Ended(services, "messenger", null); return new Done(); }), null, true));

        // ---- Instagram ----
        routes[ProtocolMethods.AccountsInstagramAnswersFromMemory] = Of<IInstagramAccount, MessageIdParams, bool>(s => s.InstagramAccount,
            (port, p, _) => Task.FromResult(port.AnswersFromMemory(p.ChatId, p.MessageId)));
        routes[ProtocolMethods.AccountsInstagramResume] = AccountMutation<IInstagramAccount, AccountOpParams, int>(s => s.InstagramAccount, (port, _, ct) => port.ResumeAsync(ct));
        routes[ProtocolMethods.AccountsInstagramConnect] = PageOnly(ProtocolMethods.AccountsInstagramConnect, AccountMutation<IInstagramAccount, InstagramConnectParams, int>(
            s => s.InstagramAccount, (port, p, ct) => port.ConnectAsync(p.Cookies, ct)));
        routes[ProtocolMethods.AccountsInstagramRestore] = RestoreMutation<IInstagramAccount, AccountRestore>(s => s.InstagramAccount, RestoreOnce.Instagram,
            new AccountRestore(null, Skipped: true), (port, ct) => port.RestoreAsync(() => ct.IsCancellationRequested));
        routes[ProtocolMethods.AccountsInstagramSignOut] = PageOnly(ProtocolMethods.AccountsInstagramSignOut, Mutation<IInstagramAccount, AccountOpParams, Done>(s => s.InstagramAccount,
            (services, port, _, _, _) => Coded(async () => { await port.SignOutAsync().ConfigureAwait(false); Ended(services, "instagram", null); return new Done(); }), null, true));

        // ---- Google Messages ----
        // The host's keeper is told of a sign-in by hand once it has been accepted (the window told its own keeper when it hosted it): a profile whose
        // refresh failed before is tried again, and the early renewal that gave up is on again. Not told of a sign-in that failed, and a failure to tell
        // is never the sign-in's: it went through, and reporting it failed would invite a second pairing.
        routes[ProtocolMethods.AccountsGoogleSignIn] = PageOnly(ProtocolMethods.AccountsGoogleSignIn, Mutation<IGoogleMessagesAccount, GoogleSignInParams, Done>(s => s.GoogleAccount,
            (services, port, p, progress, ct) => Coded(async () =>
            {
                await port.SignInAsync(p.Cookies, progress, ct, p.AsNewDevice, p.ProfileCookies).ConfigureAwait(false);
                try { services.GoogleSignedIn?.Invoke(); }
                catch (Exception) { }
                return new Done();
            }), null, true));
        routes[ProtocolMethods.AccountsGoogleSetHistoryIndexing] = AccountMutation<IGoogleMessagesAccount, GoogleIndexingParams, Done>(s => s.GoogleAccount,
            (port, p, ct) => Void(port.SetHistoryIndexingAsync(p.Enabled, ct)));
        routes[ProtocolMethods.AccountsGoogleReset] = Mutation<IGoogleMessagesAccount, GoogleResetParams, Done>(s => s.GoogleAccount,
            (services, port, p, _, _) => Coded(async () => { await port.ResetLocalAsync(p.Profile).ConfigureAwait(false); Ended(services, "google-reset", null); return new Done(); }), null, true);
        routes[ProtocolMethods.AccountsGoogleForgetAvatars] = AccountMutation<IGoogleMessagesAccount, AccountOpParams, Done>(s => s.GoogleAccount,
            (port, _, ct) => Void(port.ForgetAvatarsAsync(ct)));
        routes[ProtocolMethods.AccountsGoogleSimFor] = Of<IGoogleMessagesAccount, SimForParams, GoogleSimChip?>(s => s.GoogleAccount,
            (port, p, _) => Task.FromResult(port.SimFor(p.ChatId)));
        routes[ProtocolMethods.AccountsGoogleLoadRenumbered] = Of<IGoogleMessagesAccount, Done>(s => s.GoogleAccount, (port, _) => Void(port.LoadRenumberedAsync()));
        routes[ProtocolMethods.AccountsGoogleWarmArchive] = Of<IGoogleMessagesAccount, Done>(s => s.GoogleAccount, (port, _) =>
        {
            port.WarmArchive();
            return Task.FromResult(new Done());
        });
        routes[ProtocolMethods.AccountsGoogleKnownConversations] = Of<IGoogleMessagesAccount, IReadOnlyList<ChatSummary>>(s => s.GoogleAccount,
            (port, _) => Task.FromResult(port.KnownConversations));
        routes[ProtocolMethods.AccountsGoogleKnownChats] = Of<IGoogleMessagesAccount, IReadOnlyList<GoogleKnownChat>>(s => s.GoogleAccount, (port, ct) => port.KnownChatsForResetAsync(ct));
        routes[ProtocolMethods.AccountsGoogleAvatarKeys] = Of<IGoogleMessagesAccount, AvatarKeysParams, IReadOnlyList<string>>(s => s.GoogleAccount,
            (port, p, ct) => port.AvatarCacheKeysAsync(p.NativeIds, ct));

        // ---- Google Voice ----
        routes[ProtocolMethods.AccountsVoiceRestore] = RestoreMutation<IGoogleVoiceAccounts, IReadOnlyList<string>>(s => s.VoiceAccounts, RestoreOnce.Voice,
            [], (port, ct) => port.RestoreAsync(ct));
        routes[ProtocolMethods.AccountsVoiceAdd] = PageOnly(ProtocolMethods.AccountsVoiceAdd, AccountMutation<IGoogleVoiceAccounts, VoiceAddParams, GoogleVoiceAccountState>(
            s => s.VoiceAccounts, (port, p, ct) => port.AddAsync(p.Cookies, p.ProfileId, ct)));
        routes[ProtocolMethods.AccountsVoiceSignOut] = PageOnly(ProtocolMethods.AccountsVoiceSignOut, Mutation<IGoogleVoiceAccounts, VoiceSignOutParams, Done>(s => s.VoiceAccounts,
            (services, port, p, _, _) => Coded(async () => { await port.SignOutAsync(p.Key).ConfigureAwait(false); Ended(services, "voice", p.Key); return new Done(); }), null, true));

        // ---- What 2.6b's proxies asked the object in this process ----
        routes[ProtocolMethods.HistoryCanReadWindow] = Of<IHistoryWindowReader, ChatIdParams, bool>(s => s.History, (port, p, _) => Task.FromResult(port.CanReadWindow(p.ChatId)));
        routes[ProtocolMethods.ContactsFindDirect] = Of<IDirectChatLookup, FindDirectParams, FindDirectResult>(s => s.Contacts as IDirectChatLookup, (port, p, _) =>
        {
            if (p.Contacts.Count > MaxLookups) throw Invalid();
            return Task.FromResult(new FindDirectResult([.. p.Contacts.Select(contact => port.FindDirectChat(contact))]));
        });
        routes[ProtocolMethods.AttachmentsCanSendBatch] = Of<IAttachmentBatchBackend, CanSendBatchParams, bool>(s => s.AttachmentBatches, (port, p, ct) =>
        {
            if (p.Files.Count > MaxBatchFiles || p.Files.Any(file => file is null || !ParamsShape.IsComplete(file))) throw Invalid();
            return port.CanSendBatchAsync(p.ChatId, [.. p.Files.Select(Described)], ct);
        });
        routes[ProtocolMethods.SearchScopeDescription] = Of<ISearchScopeInfo, string>(s => s.Search as ISearchScopeInfo, (port, _) => Task.FromResult(port.SearchScopeDescription));
        return routes;
    }

    // ---- An answer follows the state it caused ----

    // The host looks at its state again now and, when this connection has a feed, waits until the feed has written the newest state
    // into the channel; the answer is written after it. On the one connection frames are in order, so the client has read the state
    // when it reads the answer. It never holds the answer back for long, and never fails it.
    private async Task SettleAsync()
    {
        try
        {
            if (services.State is not { } publisher) return;
            publisher.Refresh();
            if (publisher.Latest is not { } newest || feed is not { } active) return;
            using var patience = new CancellationTokenSource(SettleLimit);
            await active.Sent.ReachedAsync(newest.Seq).WaitAsync(patience.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { log?.Invoke("protocol settle gave up"); }
        catch (Exception) { /* the state follows the answer, as it would for a client with no feed */ }
    }

    // ---- Progress ----

    /// <summary>
    /// What a running mutation reports to its caller: each report becomes a <c>progress</c> event frame on this connection and no other,
    /// written in order by one drain, and finished (every report written) before the call is, so before its answer. A report is never
    /// logged and never stored.
    /// </summary>
    private sealed class ConnectionProgress(ProtocolDispatcher connection, ProtocolServices services, string clientOpId) : IProgress<string>, IAsyncDisposable
    {
        private readonly object gate = new();
        private Channel<string>? queue;
        private Task? drain;
        private bool closed;

        public void Report(string value)
        {
            lock (gate)
            {
                if (closed) return;
                if (queue is null)
                {
                    var created = queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
                    drain = Task.Run(() => DrainAsync(created.Reader));
                }
                queue.Writer.TryWrite(value);
            }
        }

        private async Task DrainAsync(ChannelReader<string> reader)
        {
            var epoch = services.Events?.Epoch ?? "none";
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
                while (reader.TryRead(out var text))
                    await connection.SendTextAsync(Frames.Serialize(new EventFrame(0, epoch, EventKinds.Progress,
                        ProtocolJson.ToElement(new ProgressPayload(clientOpId, text))))).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            Task? pending;
            lock (gate)
            {
                closed = true;
                queue?.Writer.TryComplete();
                pending = drain;
            }
            if (pending is not null) await pending.ConfigureAwait(false);
        }
    }
}
