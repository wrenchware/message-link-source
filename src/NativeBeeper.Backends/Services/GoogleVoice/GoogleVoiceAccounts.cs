using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NativeBeeper.App;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.Core.Services;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.GoogleVoice;

// Google Voice accounts signed in on this PC. Each account has its own helper process and its own protected session
// folder, keyed by a hash of the account's Voice number, so accounts never share a session and one failing cannot take
// another down. A Voice account is independent of the Google Messages pairing, whether or not it is the same Google
// account.
internal sealed partial class GoogleVoiceAccounts : IAsyncDisposable, IDisposable
{
    private static string Root => AppPaths.Combine("GoogleVoice");
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly IGoogleBrowser browser;
    private readonly IStateProtector protector;
    private readonly SemaphoreSlim gate = new(1);
    private readonly List<GoogleVoiceAccount> accounts = [];
    public event EventHandler? Changed;
    public IReadOnlyList<GoogleVoiceAccount> Accounts { get { lock (accounts) return accounts.ToArray(); } }
    // How often sign-in ages are looked at, and how long a helper has to check and take a renewed sign-in.
    private static readonly TimeSpan AgeCheck = TimeSpan.FromSeconds(30), HandOverBudget = TimeSpan.FromSeconds(90);
    private readonly Timer ageTimer;
    // Per account key, so a helper replaced after a refusal keeps what its predecessor learned about early renewal.
    private readonly Dictionary<string, GoogleVoiceEarlyRenewal> early = new(StringComparer.Ordinal);

    // MadeAt: when Google made the sign-in these cookies carry (GoogleVoiceSignIn.MadeAt); null when not known.
    internal sealed record SavedSession(string Number, Dictionary<string, string> Cookies, string? ProfileId = null, DateTimeOffset? MadeAt = null);

    public GoogleVoiceAccounts(IGoogleBrowser browser, IStateProtector protector)
    {
        this.browser = browser;
        this.protector = protector;
        ageTimer = new(_ => CheckSignInAges(), null, AgeCheck, AgeCheck);
    }

    // Whether any Voice account is saved on this PC, known before any of them has reconnected.
    public static bool HasSavedSessions => Directory.Exists(Root) && Directory.EnumerateFiles(Root, "session.protected", SearchOption.AllDirectories).Any();

    // Saved accounts that have not restored yet still own their cached inbox rows and retry cursors.
    public IReadOnlyList<string> KnownAccountKeys => Accounts.Select(account => account.Key)
        .Concat(Directory.Exists(Root) ? Directory.EnumerateDirectories(Root)
            .Where(folder => File.Exists(Path.Combine(folder, "session.protected")))
            .Select(Path.GetFileName).OfType<string>() : [])
        .Distinct(StringComparer.Ordinal).ToArray();

    // The folder for one account. The number itself is never used as a path, so it does not appear on disk in the clear.
    internal static string KeyFor(string number) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(number.Trim())))[..16].ToLowerInvariant();

    // The browser profile ids the saved sessions name, for a host that deletes the profiles nothing names (the Linux server's start). Null when any saved session cannot be listed or read: a profile
    // may then be the one that session needs, and nothing is deleted on a guess. Ids only (opaque names); no cookie leaves the session.
    internal static async Task<IReadOnlySet<string>?> SavedProfileIdsAsync(IStateProtector protector)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(Root)) return ids;
            foreach (var folder in Directory.EnumerateDirectories(Root))
            {
                if (!Directory.EnumerateFiles(folder, "session.protected").Any()) continue;
                var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(Path.Combine(folder, "session.protected")));
                SavedSession? saved;
                try { saved = JsonSerializer.Deserialize<SavedSession>(plain, Json); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                if (saved is null) return null;
                try { if (saved.ProfileId is { Length: > 0 } id) ids.Add(id); }
                finally { saved.Cookies?.Clear(); }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or CryptographicException or InvalidDataException or InvalidOperationException) { return null; }
        return ids;
    }

    // Reconnects every saved account. Returns why each one that could not reconnect failed, as fixed codes.
    public async Task<IReadOnlyList<string>> RestoreAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();
        if (!Directory.Exists(Root)) { GoogleVoiceLog.Write("restore saved=0"); return failures; }
        GoogleVoiceLog.Write($"restore saved={Directory.GetDirectories(Root).Count(folder => File.Exists(Path.Combine(folder, "session.protected")))}");
        foreach (var folder in Directory.GetDirectories(Root))
        {
            var path = Path.Combine(folder, "session.protected");
            if (!File.Exists(path)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var tag = GoogleVoiceLog.Tag(Path.GetFileName(folder));
            try
            {
                var saved = await ReadAsync(path);
                if (saved?.Cookies is not { Count: > 0 }) { GoogleVoiceLog.Write($"restore account={tag} failed reason=unreadable"); failures.Add("unreadable"); continue; }
                var expectedKey = Path.GetFileName(folder);
                if (saved.Number is not { Length: > 0 } || KeyFor(saved.Number) != expectedKey)
                    throw new InvalidOperationException("account_mismatch");
                saved = WithLiveProfile(saved);
                GoogleVoiceLog.Write($"restore account={tag} cookies={saved.Cookies?.Count ?? 0}");
                // A sign-in near or past Google's two hours is refreshed from the kept profile first, so the helper does not
                // start with one Google refuses now or within minutes. If the profile cannot, the saved one is tried as before.
                Dictionary<string, string>? renewed = null;
                if (saved.ProfileId is { Length: > 0 } kept && GoogleVoiceSignIn.StaleAtLaunch(saved.MadeAt, DateTimeOffset.UtcNow))
                {
                    GoogleVoiceLog.Write($"restore account={tag} browser=refreshing age_min={(int)(DateTimeOffset.UtcNow - saved.MadeAt!.Value).TotalMinutes}");
                    GoogleRefreshResult result;
                    // A browser that fails outright (WebView2 could not start, its process ended) is no reason to skip
                    // the saved sign-in, which Google may still accept.
                    try { result = await RefreshWithReasonFromBrowserAsync(kept, cancellationToken); }
                    catch (Exception failure) when (failure is not OperationCanceledException) { result = new(null, GoogleVoiceLog.Reason(failure)); }
                    renewed = result.Cookies;
                    GoogleVoiceLog.Write(renewed is null ? $"restore account={tag} browser=unavailable reason={result.Reason}"
                        : $"restore account={tag} browser=refreshed changed=[{string.Join(',', GoogleVoiceSignIn.Changed(saved.Cookies!, renewed))}] sidts_lifetime_min={result.SidtsLifetime}");
                    if (renewed is not null) saved = saved with { Cookies = renewed };
                }
                GoogleVoiceAccount account;
                try
                {
                    try { account = await StartAsync(saved, expectedKey, null, cancellationToken); }
                    catch (InvalidOperationException failure) when (failure.Message == "auth_required" && saved.ProfileId is { Length: > 0 } profileId)
                    {
                        GoogleVoiceLog.Write($"restore account={tag} browser=recovering");
                        var refreshed = await RefreshFromBrowserAsync(profileId, cancellationToken);
                        if (refreshed is null) throw;
                        try { account = await StartAsync(saved with { Cookies = refreshed }, expectedKey, null, cancellationToken); }
                        finally { refreshed.Clear(); }
                    }
                }
                finally { renewed?.Clear(); }
                if (account.ProfileId is null) account = await AttachProfileAsync(account, cancellationToken);
                GoogleVoiceLog.Write($"restore account={tag} connected match=true conversations={account.ConversationCount?.ToString() ?? "unknown"}");
            }
            catch (OperationCanceledException) { throw; }
            // One account that cannot restore leaves the others running; Settings explains it.
            catch (Exception failure) { var reason = GoogleVoiceLog.Reason(failure); GoogleVoiceLog.Write($"restore account={tag} failed reason={reason}"); failures.Add(reason); }
        }
        return failures;
    }

    // The saved session without its profile id when that profile's folder is gone, so a new profile is attached. Whether the folder is there is a
    // fact about the disk, so it is read from the disk and never asked of the browser: a host with no browser yet (MessagesServer.exe) answers "no"
    // to everything, and the start that follows writes the session back, which would lose the id of a profile that is still there.
    internal static SavedSession WithLiveProfile(SavedSession saved) =>
        saved.ProfileId is { Length: > 0 } id && !GoogleSignInProfile.TryOpen(id, out _) ? saved with { ProfileId = null } : saved;

    // Signs a new account in, or refreshes the session of an account that is already here.
    public async Task<GoogleVoiceAccount> AddAsync(Dictionary<string, string> cookies, string profileId, CancellationToken cancellationToken = default)
    {
        if (!browser.HasProfile(profileId)) throw new InvalidOperationException("profile_missing");
        try
        {
            var account = await StartAsync(new SavedSession("", cookies, profileId), null, null, cancellationToken);
            // A new sign-in, with a new profile: whatever stopped early renewal for this account no longer applies.
            lock (early) early.Remove(account.Key);
            return account;
        }
        finally { cookies.Clear(); }
    }

    public async Task SignOutAsync(GoogleVoiceAccount account)
    {
        GoogleVoiceAccount[] removed;
        await gate.WaitAsync();
        try
        {
            lock (accounts)
            {
                // Remove every generation of this account. A browser recovery may have committed a replacement just
                // before sign-out acquired the gate; signing out must retire that replacement too.
                removed = accounts.Where(current => current.Key == account.Key).ToArray();
                foreach (var current in removed) accounts.Remove(current);
            }
        }
        finally { gate.Release(); }
        // A queued cookie write also uses gate. Drain it only after the account is no longer listed, then remove the
        // durable session if no replacement for the same account appeared while shutdown completed.
        foreach (var current in removed.Append(account).Distinct()) await current.DisposeAsync();
        string[] profilesToDelete = [];
        await gate.WaitAsync();
        try
        {
            var live = Accounts.Where(current => current.Key == account.Key).ToArray();
            var liveProfiles = live.Select(current => current.ProfileId).Where(id => id is { Length: > 0 }).Cast<string>().ToHashSet(StringComparer.Ordinal);
            profilesToDelete = removed.Append(account).Select(current => current.ProfileId)
                .Where(id => id is { Length: > 0 } && !liveProfiles.Contains(id)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
            if (live.Length == 0)
            {
                var folder = Path.Combine(Root, account.Key);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }
        finally { gate.Release(); }
        foreach (var profileId in profilesToDelete) await browser.DeleteProfileAsync(profileId);
        lock (early) early.Remove(account.Key);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<GoogleVoiceAccount> StartAsync(SavedSession session, string? expectedKey, GoogleVoiceAccount? recoveryOf,
        CancellationToken cancellationToken)
    {
        var account = new GoogleVoiceAccount(this, session.ProfileId);
        try
        {
            // Listed from the start, unless it replaces a helper still running: that one keeps the account's line in
            // Connection health until this one has logged in.
            var number = await account.StartAsync(session.Cookies, recoveryOf is null || recoveryOf.HelperStopped ? expectedKey : null,
                cancellationToken);
            if (expectedKey is not null && account.Key != expectedKey) throw new InvalidOperationException("account_mismatch");
            var currentCookies = await account.SessionCookiesAsync(cancellationToken);
            GoogleVoiceAccount[] replaced;
            try
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    if (recoveryOf is not null && !Accounts.Contains(recoveryOf)) throw new InvalidOperationException("recovery_cancelled");
                    // Persist the fully validated candidate before changing the live account set. A disk/protection
                    // failure leaves the old connected account published and the candidate is disposed by the catch.
                    account.SignInMadeAt = await SaveAsync(account.Key, new(number, currentCookies, session.ProfileId));
                    lock (accounts)
                    {
                        replaced = accounts.Where(existing => existing.Key == account.Key).ToArray();
                        foreach (var old in replaced) accounts.Remove(old);
                        accounts.Add(account);
                    }
                }
                finally { gate.Release(); }
            }
            finally { currentCookies.Clear(); }
            // Old accounts may have a protected-cookie write waiting for gate; draining them while holding it deadlocks.
            // They are already unlisted, so any late writer is refused and cannot overwrite the replacement.
            foreach (var old in replaced) await old.DisposeAsync();
            foreach (var oldProfile in replaced.Select(old => old.ProfileId).Where(id => id is { Length: > 0 } && id != session.ProfileId).Distinct())
                await browser.DeleteProfileAsync(oldProfile!);
        }
        // Windows refused to start the helper (blocked, or gone since its check). Said by its fixed code, which Settings
        // explains; the exception's own message names the path, so only the number is logged.
        catch (Exception failure) when (HelperStartFailure.Code(failure) is { } code)
        {
            GoogleVoiceLog.Write($"helper start failed {HelperStartFailure.Fields(failure)}");
            await account.DisposeAsync();
            throw new InvalidOperationException(code);
        }
        catch
        {
            await account.DisposeAsync();
            throw;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return account;
    }

    internal async Task SaveCookiesAsync(GoogleVoiceAccount account, Dictionary<string, string> cookies)
    {
        if (account.Number is not { Length: > 0 } number) throw new InvalidOperationException("not_signed_in");
        await gate.WaitAsync();
        try { if (Accounts.Contains(account)) account.SignInMadeAt = await SaveAsync(account.Key, new(number, cookies, account.ProfileId)); else throw new InvalidOperationException("not_listed"); }
        finally { gate.Release(); }
    }

    internal void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    internal void Recover(GoogleVoiceAccount account)
    {
        if (!Accounts.Contains(account) || !account.Recovery.TryBegin()) return;
        _ = RecoverAsync(account);
    }

    private async Task RecoverAsync(GoogleVoiceAccount account)
    {
        try { await RecoverOwnedAsync(account); }
        finally { account.Recovery.End(); }
    }

    // The browser refresh, run by whoever owns the account's recovery: an 'auth' event, or a restart whose saved cookies
    // Google refused. Ending the ownership is the owner's job, so a restart can run it without letting a second recovery in.
    private async Task RecoverOwnedAsync(GoogleVoiceAccount account)
    {
        Dictionary<string, string>? refreshed = null;
        try
        {
            if (account.ProfileId is not { Length: > 0 } profileId || account.Number is not { Length: > 0 } number)
                throw new InvalidOperationException("profile_missing");
            account.ShowRecovering();
            GoogleVoiceLog.Write($"browser account={GoogleVoiceLog.Tag(account.Key)} recovery=starting");
            refreshed = await RefreshFromBrowserAsync(profileId, account.RecoveryToken);
            if (refreshed is null) throw new InvalidOperationException("auth_required");
            await StartAsync(new SavedSession(number, refreshed, profileId), account.Key, account, account.RecoveryToken);
            GoogleVoiceLog.Write($"browser account={GoogleVoiceLog.Tag(account.Key)} recovery=connected");
        }
        catch (OperationCanceledException) { }
        catch (Exception failure)
        {
            GoogleVoiceLog.Write($"browser account={GoogleVoiceLog.Tag(account.Key)} recovery=failed reason={GoogleVoiceLog.Reason(failure)}");
            if (Accounts.Contains(account)) account.RequireSignIn();
        }
        finally { refreshed?.Clear(); }
    }

    // Starts an account again after its helper process ended or its realtime channel gave up, paced by
    // GoogleVoiceRestart. Returns whether a restart is now under way. It owns the account's recovery throughout, so an
    // 'auth' event meanwhile cannot start a second replacement; when Google refuses the saved cookies it runs the
    // browser refresh itself.
    internal bool Restart(GoogleVoiceAccount account, string reason)
    {
        if (GoogleVoiceRestart.Refusal(Accounts.Contains(account), account.IsDisposed, account.IsConnected) is not null || !account.Recovery.TryBegin()) return false;
        CancellationToken token;
        try { token = account.RecoveryToken; }
        catch (ObjectDisposedException) { account.Recovery.End(); return false; }
        _ = RestartAsync(account, reason, token);
        return true;
    }

    private async Task RestartAsync(GoogleVoiceAccount account, string reason, CancellationToken token)
    {
        var tag = GoogleVoiceLog.Tag(account.Key);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var wait = GoogleVoiceRestart.Pause(attempt);
                GoogleVoiceLog.Write($"restart account={tag} reason={reason} attempt={attempt} wait_s={(int)wait.TotalSeconds}");
                await Task.Delay(wait, token);
                if (GoogleVoiceRestart.Refusal(Accounts.Contains(account), account.IsDisposed, account.IsConnected) is { } refusal)
                {
                    GoogleVoiceLog.Write($"restart account={tag} stopped why={refusal}");
                    return;
                }
                try
                {
                    var saved = await ReadAsync(Path.Combine(Root, account.Key, "session.protected"));
                    if (saved?.Cookies is not { Count: > 0 }) throw new InvalidOperationException("unreadable");
                    await StartAsync(saved with { ProfileId = account.ProfileId }, account.Key, account, token);
                    GoogleVoiceLog.Write($"restart account={tag} connected attempt={attempt}");
                    return;
                }
                catch (InvalidOperationException failure) when (failure.Message == "auth_required")
                {
                    // Google's two hours ran out on the saved sign-in; the browser profile renews it.
                    GoogleVoiceLog.Write($"restart account={tag} refused attempt={attempt}");
                    await RecoverOwnedAsync(account);
                    return;
                }
                catch (Exception failure) when (!token.IsCancellationRequested)
                {
                    GoogleVoiceLog.Write($"restart account={tag} failed reason={GoogleVoiceLog.Reason(failure)} attempt={attempt}");
                }
            }
        }
        catch (OperationCanceledException) { GoogleVoiceLog.Write($"restart account={tag} stopped why=gone"); }
        finally { account.Recovery.End(); }
    }

    private async Task<Dictionary<string, string>?> RefreshFromBrowserAsync(string profileId, CancellationToken cancellationToken) =>
        (await RefreshWithReasonFromBrowserAsync(profileId, cancellationToken)).Cookies;

    private Task<GoogleRefreshResult> RefreshWithReasonFromBrowserAsync(string profileId, CancellationToken cancellationToken) =>
        browser.RefreshAsync(GoogleSignInProfile.VoiceService, profileId, cancellationToken);

    // Every AgeCheck: an account whose sign-in has reached 110 minutes has it renewed before Google's two hours.
    private void CheckSignInAges()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var account in Accounts)
        {
            if (!account.IsConnected || account.IsDisposed || account.Key.Length == 0 || account.ProfileId is not { Length: > 0 } profileId) continue;
            var made = account.SignInMadeAt;
            GoogleVoiceEarlyRenewal schedule;
            lock (early)
            {
                if (!early.TryGetValue(account.Key, out var known)) early[account.Key] = known = new();
                schedule = known;
            }
            bool due;
            lock (schedule) due = schedule.Due(now, made);
            if (due) _ = RenewEarlyAsync(account, profileId, schedule, made!.Value);
        }
    }

    // The early renewal: the kept profile is loaded and, when Google issued a new sign-in, the running helper checks it and
    // takes it in place (cookies.replace). Nothing reconnects but realtime, once, and the helper's cookie event saves it,
    // which restarts the two hours (GoogleVoiceSignIn.MadeAt). It never owns the account's recovery: a recovery that starts
    // meanwhile replaces the helper as it always did, and a hand-over to the replaced helper is simply not saved.
    private async Task RenewEarlyAsync(GoogleVoiceAccount account, string profileId, GoogleVoiceEarlyRenewal schedule, DateTimeOffset made)
    {
        var tag = GoogleVoiceLog.Tag(account.Key);
        GoogleVoiceLog.Write($"browser account={tag} early=starting age_min={(int)(DateTimeOffset.UtcNow - made).TotalMinutes}");
        string outcome;
        Dictionary<string, string>? refreshed = null;
        SavedSession? saved = null;
        try
        {
            var token = account.RecoveryToken;
            if (account.Recovery.IsOwned) outcome = "busy";
            else
            {
                var result = await RefreshWithReasonFromBrowserAsync(profileId, token);
                refreshed = result.Cookies;
                if (refreshed is null) outcome = result.Reason;
                else if (!account.IsConnected || account.Recovery.IsOwned || !Accounts.Contains(account)) outcome = "busy";
                else
                {
                    // What the helper holds is what was saved last; its cookie events save every change it sees.
                    saved = await ReadAsync(Path.Combine(Root, account.Key, "session.protected"));
                    var changed = GoogleVoiceSignIn.Changed(saved?.Cookies ?? [], refreshed);
                    if (!changed.Contains(GoogleVoiceSignIn.Timestamp)) outcome = "same";
                    else
                    {
                        GoogleVoiceLog.Write($"browser account={tag} early=renewed changed=[{string.Join(',', changed)}] sidts_lifetime_min={result.SidtsLifetime}");
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                        budget.CancelAfter(HandOverBudget);
                        await account.CallAsync("cookies.replace", new { cookies = refreshed }, budget.Token);
                        account.SignInMadeAt = DateTimeOffset.UtcNow;
                        outcome = "adopted";
                    }
                }
            }
        }
        catch (ObjectDisposedException) { outcome = "gone"; }
        catch (OperationCanceledException) { outcome = account.IsDisposed ? "gone" : "timeout"; }
        catch (InvalidOperationException failure) when (failure.Message == "auth_required") { outcome = "refused"; }
        catch (InvalidOperationException failure) when (failure.Message == "invalid_input") { outcome = "missing_cookies"; }
        catch (InvalidOperationException failure) when (failure.Message is "not_connected" or "login_required") { outcome = "gone"; }
        catch (Exception failure) { outcome = GoogleVoiceLog.Reason(failure); }
        finally
        {
            refreshed?.Clear();
            saved?.Cookies?.Clear();
        }
        GoogleVoiceLog.Write($"browser account={tag} early=adopt outcome={outcome}");
        bool stopped;
        lock (schedule) stopped = schedule.Ended(outcome);
        if (stopped) GoogleVoiceLog.Write($"browser account={tag} early=off reason={outcome}");
    }

    private async Task<GoogleVoiceAccount> AttachProfileAsync(GoogleVoiceAccount account, CancellationToken cancellationToken)
    {
        Dictionary<string, string>? cookies = null;
        GoogleSignInResult? profile = null;
        try
        {
            cookies = await account.SessionCookiesAsync(cancellationToken);
            profile = await browser.CreateProfileAsync(cookies, cancellationToken);
            if (profile is null) return account;
            var replacement = await StartAsync(new SavedSession(account.Number!, profile.Cookies, profile.ProfileId), account.Key,
                account, cancellationToken);
            profile.TransferProfileOwnership();
            GoogleVoiceLog.Write($"browser account={GoogleVoiceLog.Tag(account.Key)} recovery=profile_created");
            return replacement;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure)
        {
            GoogleVoiceLog.Write($"browser account={GoogleVoiceLog.Tag(account.Key)} recovery=profile_failed reason={GoogleVoiceLog.Reason(failure)}");
            return account;
        }
        finally
        {
            cookies?.Clear();
            if (profile is not null) await profile.DisposeAsync();
        }
    }

    // Conversations and messages that moved in one account, as its helper reported them.
    public event Action<GoogleVoiceAccount, JsonElement>? ConversationsChanged;
    internal void NotifyConversations(GoogleVoiceAccount account, JsonElement changes) => ConversationsChanged?.Invoke(account, changes);

    // Saves the session with the time its sign-in was made, which it returns: kept from the session saved before while
    // the timestamp cookie is the same, and unknown when that session exists but cannot be read. Always called under gate,
    // so the comparison and the write belong together. Written through to disk, since losing it means signing in again.
    private async Task<DateTimeOffset?> SaveAsync(string key, SavedSession session)
    {
        var folder = Path.Combine(Root, key);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session.protected");
        SavedSession? previous = null;
        var unreadable = false;
        if (File.Exists(path))
        {
            try { previous = await ReadAsync(path); }
            catch (Exception failure) when (failure is not OperationCanceledException) { unreadable = true; }
        }
        var made = unreadable ? null : GoogleVoiceSignIn.MadeAt(previous?.Cookies, previous?.MadeAt, session.Cookies, DateTimeOffset.UtcNow);
        previous?.Cookies?.Clear();
        var plain = JsonSerializer.SerializeToUtf8Bytes(session with { MadeAt = made }, Json);
        try
        {
            var protectedBytes = await protector.ProtectAsync(plain);
            await NativeBeeper.Storage.DurableFile.WriteAsync(path + ".tmp", protectedBytes);
            File.Move(path + ".tmp", path, true);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        return made;
    }

    private async Task<SavedSession?> ReadAsync(string path)
    {
        var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(path));
        try { return JsonSerializer.Deserialize<SavedSession>(plain, Json); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async ValueTask DisposeAsync()
    {
        await ageTimer.DisposeAsync();
        foreach (var account in Accounts) await account.DisposeAsync();
        lock (accounts) accounts.Clear();
    }

    // The service provider disposes synchronously at shutdown. Closing each pipe ends its helper process, so the rest of
    // the teardown does not need to be waited for.
    public void Dispose()
    {
        ageTimer.Dispose();
        foreach (var account in Accounts) _ = account.DisposeAsync().AsTask();
        lock (accounts) accounts.Clear();
    }
}

internal sealed class GoogleVoiceAccount : IAsyncDisposable
{
    private const int ProtocolVersion = 1;
    private readonly GoogleVoiceAccounts owner;
    private readonly LatestGenerationWriter<Dictionary<string, string>> cookieWriter;
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    private HelperConnection? helper;
    public string Key { get; private set; } = "";
    public string? Number { get; private set; }
    public string State { get; private set; } = "Connecting…";
    public int? ConversationCount { get; private set; }
    public bool IsConnected { get; private set; }
    public string? ProfileId { get; }
    private readonly object madeSync = new();
    private DateTimeOffset? signInMadeAt;
    // When Google made the sign-in this account's helper holds (GoogleVoiceSignIn), as last saved; null when not known.
    public DateTimeOffset? SignInMadeAt { get { lock (madeSync) return signInMadeAt; } internal set { lock (madeSync) signInMadeAt = value; } }
    internal CancellationToken RecoveryToken => lifetime.Token;
    internal RecoveryOwnership Recovery { get; } = new();
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;
    // Its helper ended by itself (not by a sign-out, a replacement or shutdown).
    internal bool HelperStopped { get; private set; }

    public GoogleVoiceAccount(GoogleVoiceAccounts owner, string? profileId)
    {
        this.owner = owner;
        ProfileId = profileId;
        cookieWriter = new(SaveRenewedAsync, cookies => cookies.Clear());
    }

    public async Task<string> StartAsync(Dictionary<string, string> cookies, string? knownKey, CancellationToken cancellationToken)
    {
        var path = HelperExecutables.PathFor("gvoice-helper");
        if (!File.Exists(path)) throw new InvalidOperationException("helper_missing");
        var started = await HelperConnection.StartAsync(path, null, cancellationToken);
        // A saved account's key is known before login, so a restore or restart that fails at login still shows in
        // Connection health as a helper that stopped. A new sign-in has no key until login names the number.
        if (knownKey is { Length: > 0 }) started.Scope = GoogleVoiceModels.Qualify(knownKey, "");
        GoogleVoiceLog.Write("helper starting");
        helper = started;
        started.Received += (kind, data) => { if (ReferenceEquals(helper, started)) OnEvent(kind, data); };
        // A helper that ends by itself (a crash, or HelperRequests ending one whose reply was overdue) is started again.
        // Signing out, replacing and shutting down clear helper before it ends, so they never get here.
        started.Disconnected += () =>
        {
            if (!ReferenceEquals(helper, started)) return;
            HelperStopped = true;
            GoogleVoiceLog.Write($"helper stopped account={GoogleVoiceLog.Tag(Key)}");
            IsConnected = false;
            State = owner.Restart(this, "helper_stopped") ? "Reconnecting…" : "Disconnected";
            owner.NotifyChanged();
        };
        var hello = await started.CallAsync("hello", null, cancellationToken);
        if (hello.GetProperty("protocol").GetInt32() != ProtocolVersion) throw new InvalidOperationException("unsupported_protocol");
        GoogleVoiceLog.Write("helper hello ok");
        var login = await started.CallAsync("login", new { cookies }, cancellationToken);
        // Google accepted the account but it has no Voice number: told apart from a failed login, which reads as a network
        // problem and sent someone without a number to check their connection.
        if (login.GetProperty("number").GetString() is not { Length: > 0 } number)
        {
            GoogleVoiceLog.Write("login no_number");
            throw new InvalidOperationException("no_voice_number");
        }
        Number = number;
        Key = GoogleVoiceAccounts.KeyFor(Number);
        // Kept when already set: on an account_mismatch the helper stays listed under the saved account it was started for.
        started.Scope ??= GoogleVoiceModels.Qualify(Key, "");
        GoogleVoiceLog.Write($"login ok account={GoogleVoiceLog.Tag(Key)} attempt={(login.TryGetProperty("attempt", out var attempt) ? attempt.GetString() : "unknown")}");
        await started.CallAsync("connect", null, cancellationToken);
        GoogleVoiceLog.Write($"connect ok account={GoogleVoiceLog.Tag(Key)}");
        IsConnected = true;
        State = "Connected";
        await RefreshCountAsync(cancellationToken);
        return Number;
    }

    public Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        helper is { } started ? started.CallAsync(method, parameters, cancellationToken) : throw new InvalidOperationException("not_connected");

    public async Task<Dictionary<string, string>> SessionCookiesAsync(CancellationToken cancellationToken)
    {
        if (helper is not { } started) throw new InvalidOperationException("not_connected");
        var session = await started.CallAsync("session", null, cancellationToken);
        return session.GetProperty("cookies").Deserialize<Dictionary<string, string>>() ?? [];
    }

    // Only at connect, where it also proves the session can read. After that the count comes from the list reads the
    // inbox makes anyway (NoteConversationCount); a change event never costs a list read of its own.
    private async Task RefreshCountAsync(CancellationToken cancellationToken)
    {
        if (helper is not { } started) return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var chats = await started.CallAsync("ui.chats", null, cancellationToken);
            GoogleVoiceLog.Write($"chats read account={GoogleVoiceLog.Tag(Key)} reason=count ms={watch.ElapsedMilliseconds}");
            NoteConversationCount(chats.GetArrayLength());
        }
        catch (InvalidOperationException failure) when (failure.Message == "auth_required")
        {
            if (owner.Accounts.Contains(this)) owner.Recover(this);
            else throw;
        }
    }

    // The number of recent conversations Settings shows. Settings is rebuilt only when it actually changes.
    internal void NoteConversationCount(int count)
    {
        if (ConversationCount == count) return;
        ConversationCount = count;
        owner.NotifyChanged();
    }

    private void OnEvent(string kind, JsonElement data)
    {
        switch (kind)
        {
            case "diagnostic":
                GoogleVoiceLog.Write($"helper account={GoogleVoiceLog.Tag(Key)} {GoogleVoiceLog.Fields(data)}");
                break;
            case "cookies":
                if (data.TryGetProperty("generation", out var generation) && generation.TryGetInt64(out var version) && version > 0 &&
                    data.TryGetProperty("cookies", out var cookies) && cookies.Deserialize<Dictionary<string, string>>() is { } rotated)
                    cookieWriter.Offer(version, rotated);
                break;
            case "auth":
                GoogleVoiceLog.Write($"event auth account={GoogleVoiceLog.Tag(Key)}");
                owner.Recover(this);
                break;
            case "disconnected":
                GoogleVoiceLog.Write($"event disconnected account={GoogleVoiceLog.Tag(Key)}");
                IsConnected = false;
                // The helper gave up on realtime after repeated failures. A recovered read or a renewed sign-in can still
                // bring it back by itself, which the restart checks for before it replaces the helper.
                var final = data.TryGetProperty("final", out var ended) && ended.ValueKind == JsonValueKind.True;
                State = !final || owner.Restart(this, "realtime_final") ? "Reconnecting…" : "Disconnected";
                owner.NotifyChanged();
                break;
            case "connected":
                GoogleVoiceLog.Write($"event connected account={GoogleVoiceLog.Tag(Key)}");
                IsConnected = true; State = "Connected"; owner.NotifyChanged();
                break;
            case "changed":
                owner.NotifyConversations(this, data);
                break;
        }
    }

    private async Task SaveRenewedAsync(Dictionary<string, string> cookies)
    {
        try { await owner.SaveCookiesAsync(this, cookies); GoogleVoiceLog.Write($"cookies saved account={GoogleVoiceLog.Tag(Key)} count={cookies.Count}"); }
        catch (Exception failure) { GoogleVoiceLog.Write($"cookies not saved account={GoogleVoiceLog.Tag(Key)} reason={GoogleVoiceLog.Reason(failure)}"); /* The running session still works; the next launch may need to sign in again. */ }
        finally { cookies.Clear(); }
    }

    internal void ShowRecovering() { IsConnected = false; State = "Reconnecting…"; owner.NotifyChanged(); }
    internal void RequireSignIn() { NeedsSignIn = true; IsConnected = false; State = "Sign in again"; owner.NotifyChanged(); }
    // Google refused the session and the browser could not renew it: only signing in again helps.
    internal bool NeedsSignIn { get; private set; }

    // Contact-visible calls on this account's helper still waiting for their answer.
    internal int MutationsInFlight => helper?.OwnMutationsInFlight ?? 0;

    // Which helper this is and how many contact-visible calls have begun on it: see IHelperRestartTarget.Mark.
    internal string? MutationMark => helper?.OwnMutationMark;

    // For a restart somebody asked for: the helper is let go as one that stopped by itself would leave the account —
    // detached, so its events and its end are ignored as a replaced helper's are, and marked stopped, so the replacement
    // is listed under this account from the start. Unless forced, only while no send is under way on it; forced with
    // a mark, only while none has begun since the mark was read; false, with nothing changed, otherwise.
    internal async Task<bool> StopForRestartAsync(bool force, string? through = null)
    {
        if (helper is not { } started) return true;
        if (!(force ? through is null || started.SealThrough(through) : started.SealIfIdle())) return false;
        helper = null;
        HelperStopped = true;
        IsConnected = false;
        State = "Reconnecting…";
        owner.NotifyChanged();
        await started.DisposeAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        var started = helper;
        helper = null;
        IsConnected = false;
        if (started is not null) await started.DisposeAsync();
        await cookieWriter.Completion;
        lifetime.Dispose();
    }
}
