using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using System.Threading.Channels;

namespace NativeBeeper.App.Services;

internal sealed partial class ProviderAccounts
{
    internal Action<Account, CancellationToken>? StartGoogleKeeper { get; set; }
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> keeperStarted = [];
    private CancellationToken lifetime;

    internal static string Scope(Account account) => account.Slot.Ids.Qualify(account.Registration.Service switch
    { "google" => "google-direct:", "messenger" => "messenger:", _ => "instagram:" });

    internal async Task<IReadOnlyList<ChatAccount>> CatalogAsync(CancellationToken token = default)
    {
        await LoadAsync(token);
        var result = new List<ChatAccount>();
        foreach (var account in Added)
        {
            if (account.Google is { } g && g.Files.SessionSaved)
                result.Add(new(g.Models.Account, "Google Messages", g.OwnNumber ?? "Google Messages account", g.CanReceive ? "Connected" : "Signed in", "", "direct"));
            if (account.Messenger is { } m && m.SessionSaved)
                result.Add(new(m.Models.Account, "Messenger", m.OwnName ?? "Messenger account", m.IsConnected ? "Connected" : "Signed in", "", "messenger"));
            if (account.Instagram is { HasSavedSession: true } i)
                foreach (var entry in await i.GetAccountsAsync(token)) result.Add(entry with { DisplayName = i.AccountName ?? "Instagram account" });
        }
        return result;
    }

    internal void NotifyChanged() => Changed?.Invoke();
    internal void BeginAccount(Account account)
    {
        if (account.Registration.Pending) return;
        if (account.Google is not null)
            lock (keeperStarted)
                if (StartGoogleKeeper is not null && keeperStarted.Add(account.Registration.Key))
                {
                    lifetime.Register(() => account.Lifetime.Cancel());
                    StartGoogleKeeper(account, account.Lifetime.Token);
                }
        Changed?.Invoke();
    }

    internal async Task RestoreAsync(CancellationToken stop)
    {
        lifetime = stop;
        await LoadAsync(stop);
        foreach (var account in Added)
        {
            stop.ThrowIfCancellationRequested();
            BeginAccount(account);
            try
            {
                if (account.Google is { } g && g.Files.SessionSaved) await g.DiscoverAsync(stop);
                else if (account.Messenger is { SessionSaved: true } m) await new MessengerAccount(m).RestoreAsync(() => stop.IsCancellationRequested);
                else if (account.Instagram is { HasSavedSession: true } i) await new InstagramAccountLifecycle(i).RestoreAsync(() => stop.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
            catch (Exception failure) { AppLog.Write($"account restore service={account.Registration.Service} outcome={failure.GetType().Name}"); }
        }
        started.TrySetResult();
        Changed?.Invoke();
    }

    internal IRealtimeFeed Over(IRealtimeFeed original) => new AccountFeed(this, original);
    private sealed class AccountFeed(ProviderAccounts owner, IRealtimeFeed original) : IRealtimeFeed
    {
        public async Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var wake = Channel.CreateBounded<bool>(1);
            var pumps = new Dictionary<string, (CancellationTokenSource Stop, Task Task)>();
            using var serial = new SemaphoreSlim(1);
            async Task Deliver(RealtimeEvent change, CancellationToken token)
            {
                await serial.WaitAsync(token);
                try { await receive(change, token); }
                finally { serial.Release(); }
            }
            var primary = original.RunAsync(Deliver, stop.Token);
            void Changed() => wake.Writer.TryWrite(true);
            owner.Changed += Changed;
            try
            {
                await owner.started.Task.WaitAsync(stop.Token);
                Changed();
                await foreach (var _ in wake.Reader.ReadAllAsync(stop.Token))
                {
                    var accounts = owner.Added;
                    foreach (var key in pumps.Keys.Except(accounts.Select(a => a.Registration.Key)).ToArray())
                    {
                        pumps[key].Stop.Cancel();
                        try { await pumps[key].Task; } catch (OperationCanceledException) { }
                        pumps[key].Stop.Dispose();
                        pumps.Remove(key);
                    }
                    foreach (var account in accounts.Where(a => !pumps.ContainsKey(a.Registration.Key)))
                    {
                        // A Google feed connects by itself; never start it for an unfinished sign-in.
                        if (account.Google is { } google && !google.Files.SessionSaved) continue;
                        var ownStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        IRealtimeFeed feed = (IRealtimeFeed?)account.Google ?? (IRealtimeFeed?)account.Messenger ?? account.Instagram!;
                        var scope = Scope(account);
                        pumps.Add(account.Registration.Key, (ownStop, feed.RunAsync((change, token) => Deliver(change with { Scope = scope }, token), ownStop.Token)));
                    }
                }
            }
            finally
            {
                owner.Changed -= Changed;
                stop.Cancel();
                try { await Task.WhenAll(pumps.Values.Select(p => p.Task).Append(primary)); } catch (OperationCanceledException) { }
                foreach (var pump in pumps.Values) pump.Stop.Dispose();
            }
        }
    }
}
