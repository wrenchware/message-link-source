using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MessageLink.App.Services.GoogleMessages;
using MessageLink.App.Services.GoogleVoice;
using MessageLink.App.Services.Instagram;
using MessageLink.App.Services.Messenger;
using MessageLink.GoogleMessages.Spike;

namespace MessageLink.App.Services;

// How long a shutdown waits for each of its steps (BackendShutdown.StopAsync). A session end has only the few seconds
// Windows allows before it ends the process anyway.
internal sealed record ShutdownBudget(TimeSpan Sends, TimeSpan Providers, TimeSpan Sweep)
{
    public static ShutdownBudget Normal { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(4));
    public static ShutdownBudget Session { get; } = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(0.5));
    public TimeSpan Total => Sends + Providers + Sweep;
}

// The end of a host's run for the providers: in the backends so that every host closes them the same way, a server's
// stop as much as the app's quit (SWITCHBOARD.md 1.7e).
internal static class BackendShutdown
{
    // All four providers' helpers, closed together and waited for within a bound. Before, only Google's close was
    // awaited: Messenger's and Voice's were started and then cut off by the exit, Voice's last cookie save with them,
    // and Instagram's helper was never closed at all.
    //
    // A send still waiting for its answer gets the budget's Sends bound first. The window has already refused to close while
    // a transfer or action was running, and asked about unsettled sends; this covers the one already on the wire, and
    // the fire-and-forget calls that ask covers not (a read mark). Past that, its helper is closed and the outcome is
    // unknown, as it would be had the app crashed. It is never resent.
    //
    // Worst case, with the window already gone: 5 s for sends, 10 s for the providers, 4 s for anything left, or 1 s,
    // 2 s and 0.5 s when Windows is ending the session (ShutdownBudget). Usually well under a second. Whatever still runs when the process ends is ended by the kill-on-close job (HelperJob).
    public static async Task StopAsync(IServiceProvider provider, ShutdownBudget budget)
    {
        var clock = Stopwatch.StartNew();
        // A reconnect or recovery that races this cannot start a helper nothing would close.
        HelperConnection.RefuseStarts = true;
        var (endedBefore, killedBefore) = HelperConnection.Exits;
        var sending = HelperConnection.MutationsInFlight;
        var settled = sending == 0 || await HelperConnection.WhenMutationsSettledAsync(budget.Sends);
        AppLog.Write($"shutdown sends before={sending} after={HelperConnection.MutationsInFlight} settled={settled} ms={clock.ElapsedMilliseconds}");

        var google = provider.GetRequiredService<GoogleMessagesBackend>();
        var messenger = provider.GetRequiredService<MessengerBackend>();
        var instagram = provider.GetRequiredService<InstagramBackend>();
        var voice = provider.GetRequiredService<GoogleVoiceAccounts>();
        var hermes = provider.GetRequiredService<MessageLink.App.Services.Hermes.HermesBackend>();
        (string Name, Func<Task> Stop)[] stops =
        [
            ("google", google.StopAsync),
            ("messenger", messenger.ShutdownAsync),
            ("instagram", instagram.CloseAsync),
            ("hermes", hermes.CloseAsync),
            // Every account at once: one after another, at up to three seconds each, a few accounts would outrun the
            // bound by themselves. Each finishes its protected cookie save before its close completes.
            ("voice", async () =>
            {
                await Task.WhenAll(voice.Accounts.Select(account => account.DisposeAsync().AsTask()));
                await voice.DisposeAsync();
            }),
            .. AddedStops(provider.GetRequiredService<ProviderAccounts>()),
        ];
        var finished = new HashSet<string>();
        async Task Timed(string name, Func<Task> stop)
        {
            var took = Stopwatch.StartNew();
            var outcome = "ok";
            try { await stop(); }
            catch (Exception failure) { outcome = failure.GetType().Name; }
            lock (finished) finished.Add(name);
            AppLog.Write($"shutdown provider={name} outcome={outcome} ms={took.ElapsedMilliseconds}");
        }
        var closed = await HelperConnection.WithinAsync([.. stops.Select(stop => (Func<Task>)(() => Timed(stop.Name, stop.Stop)))], budget.Providers);
        lock (finished)
            foreach (var (name, _) in stops.Where(stop => !finished.Contains(stop.Name)))
                AppLog.Write($"shutdown provider={name} outcome=late ms={clock.ElapsedMilliseconds}");

        // Whatever no backend owns (a pairing window, a session renewal) or a late backend left open.
        var swept = await HelperConnection.StopAllAsync(budget.Sweep);
        var (ended, killed) = HelperConnection.Exits;
        AppLog.Write($"shutdown helpers closed={ended - endedBefore} killed={killed - killedBefore} providers={closed}/{stops.Length} swept={swept} left={HelperConnection.LiveCount} ms={clock.ElapsedMilliseconds}");
    }

    // Each added account, stopped as its service's first account is and in the same step. Left to the sweep, an added
    // Instagram account's helper was gone before ProviderAccounts' disposal asked it to save the session, so the next run
    // resumed from an older one. Named by the account's slot key, which is also its folder name.
    internal static IEnumerable<(string Name, Func<Task> Stop)> AddedStops(ProviderAccounts accounts)
    {
        foreach (var account in accounts.Added)
        {
            var key = account.Registration.Key;
            if (account.Google is { } google) yield return ($"google:{key}", google.StopAsync);
            if (account.Messenger is { } messenger) yield return ($"messenger:{key}", messenger.ShutdownAsync);
            if (account.Instagram is { } instagram) yield return ($"instagram:{key}", instagram.CloseAsync);
        }
    }
}
