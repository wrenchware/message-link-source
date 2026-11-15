using System.Security.Cryptography;
using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host;

/// <summary>
/// The server's repeating sends (protocol 31): "weekdays at 7:00, ask: Morning brief", in Hermes threads only. Hermes's own cron cannot deliver into an API
/// session and Hermes has no push of its own (PROVIDERS.md, Hermes), so the server asks at the time and the answer arrives as an ordinary reply with its
/// notification. The rows are the host's own (<c>repeat:</c>), drawn by every client like the scheduled sends.
/// </summary>
/// <remarks>
/// Built beside the one-shot schedule (HostScheduledSends), not inside it: that engine removes a message once sent and retires its id for good, holds a missed
/// one as "not sent" for the person to decide, and records an uncertain send for every client, all of which are right for a real contact and wrong for a repeat
/// to an agent. A repeat keeps its id, moves to its next time, and a missed time is skipped unless it is only a little late (RepeatRule.Grace): after downtime at
/// most one late send, never a backlog. The next time is written before the prompt goes, so a crash mid-send never asks twice.
/// </remarks>
internal sealed class HostRepeats : IRepeatCommands, IDisposable
{
    public const int MaxRepeats = 20, MaxPromptLength = 2000;
    private const string Prefix = SharedCategories.Repeat + ":";
    private readonly SharedStateStore store;
    private readonly Func<CancellationToken, Task<string?>> localKey;
    private readonly IHermesPrompts prompts;
    private readonly TimeProvider clock;
    private readonly TimeZoneInfo zone;
    private readonly Action<string> log;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? loop;

    /// <summary>The longest the timer sleeps between looks, so a clock that jumped or a machine that slept is noticed within it.</summary>
    internal static TimeSpan Longest { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary>A pass finished: what the harness waits on instead of real time.</summary>
    internal event Action? Passed;

    public HostRepeats(SharedStateStore store, Func<CancellationToken, Task<string?>> localKey, IHermesPrompts prompts, TimeProvider? clock = null,
        TimeZoneInfo? zone = null, Action<string>? log = null)
    {
        this.store = store;
        this.localKey = localKey;
        this.prompts = prompts;
        this.clock = clock ?? TimeProvider.System;
        // The server's own time zone (the container's TZ on Unraid), as the owner chose (2026-11-14).
        this.zone = zone ?? this.clock.LocalTimeZone;
        this.log = log ?? (_ => { });
    }

    public void Start() => loop ??= Task.Run(() => RunAsync(stop.Token));

    public async Task<RepeatAnswer> CreateAsync(RepeatCreateParams request, CancellationToken cancellationToken = default)
    {
        var text = request.Text?.Trim() ?? "";
        var rule = new RepeatRule(request.Days, request.Minute);
        if (!HermesIds.Owns(request.ChatId) || !prompts.Accepts(request.ChatId)) return Refused("Repeats are for Hermes conversations.");
        if (text.Length == 0) return Refused("Enter what to ask.");
        if (text.Length > MaxPromptLength) return Refused("That prompt is too long.");
        if (!rule.Valid) return Refused("Choose at least one day and a time.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await ProfileAsync().ConfigureAwait(false) ?? throw new IOException("There is no key to file the repeat under.");
            var rows = await ReadAsync(profile).ConfigureAwait(false);
            if (rows.Count >= MaxRepeats) return Refused($"There can be up to {MaxRepeats} repeats.");
            var now = clock.GetUtcNow();
            var repeat = new RepeatingSend(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12)), request.ChatId, text, rule.Days, rule.Minute, now, rule.Next(now, zone));
            await WriteAsync(profile, repeat).ConfigureAwait(false);
            log($"repeats created days={rule.Days} count={rows.Count + 1}");
            Wake();
            return new(RepeatStatuses.Done, repeat.Id, NextDue: repeat.NextDue);
        }
        finally { gate.Release(); }
    }

    public async Task<RepeatAnswer> CancelAsync(RepeatCancelParams request, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var profile = await ProfileAsync().ConfigureAwait(false);
            // One that is not there is done: cancelling twice, or after another device did, is not an error.
            if (profile is not null) await store.WriteOwnBatchAsync([(KeyOf(profile, request.Id), null)]).ConfigureAwait(false);
            log("repeats cancelled");
            Wake();
            return new(RepeatStatuses.Done, request.Id);
        }
        finally { gate.Release(); }
    }

    private static RepeatAnswer Refused(string words) => new(RepeatStatuses.Refused, Message: words);

    private void Wake() => Interlocked.Exchange(ref wake, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var woken = Volatile.Read(ref wake).Task;
            TimeSpan wait;
            try
            {
                var next = await PassAsync(cancellationToken).ConfigureAwait(false);
                failures = 0;
                var until = next is { } due ? due - clock.GetUtcNow() : Longest;
                wait = until <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : until < Longest ? until : Longest;
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // A store that cannot be read is tried again, slowly: nothing is sent from a list that could not be read.
                failures++;
                log($"repeats pass failed error={failure.GetType().Name} failures={failures}");
                wait = TimeSpan.FromSeconds(Math.Min(60, 5 * failures));
            }
            try { Passed?.Invoke(); } catch (Exception) { }
            try { await Task.WhenAny(woken, Task.Delay(wait, clock, cancellationToken)).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Settles every repeat that is due now: sends it (on time or a little late) or skips it, and moves it to its next time. Returns the soonest next time.</summary>
    internal async Task<DateTimeOffset?> PassAsync(CancellationToken cancellationToken = default)
    {
        List<(RepeatingSend Repeat, bool Send)> due = [];
        DateTimeOffset? soonest = null;
        string? profile;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            profile = await ProfileAsync().ConfigureAwait(false);
            if (profile is null) return null;
            var now = clock.GetUtcNow();
            foreach (var repeat in await ReadAsync(profile).ConfigureAwait(false))
            {
                if (!repeat.Rule.Valid) continue;
                var current = repeat;
                if (repeat.NextDue <= now)
                {
                    var (send, next) = repeat.Rule.Settle(repeat.NextDue, now, zone);
                    // Moved on before anything is sent: whatever happens to the send, this time is never asked again.
                    current = repeat with { NextDue = next, LastAt = now, LastOutcome = send ? "sending" : "skipped" };
                    await WriteAsync(profile, current).ConfigureAwait(false);
                    if (!send) log($"repeats skipped late_minutes={(int)(now - repeat.NextDue).TotalMinutes}");
                    else due.Add((current, true));
                }
                if (soonest is null || current.NextDue < soonest) soonest = current.NextDue;
            }
        }
        finally { gate.Release(); }
        foreach (var (repeat, _) in due)
        {
            // Each send holds the gate a cancel takes, and looks again first: a repeat cancelled while an earlier one was being sent is not sent. The
            // backend answers once Hermes has accepted the turn or within its own short wait, so a cancel waits seconds at most.
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var rows = await ReadAsync(profile).ConfigureAwait(false);
                if (rows.FirstOrDefault(r => r.Id == repeat.Id) is not { LastOutcome: "sending" } still || still.LastAt != repeat.LastAt)
                {
                    log("repeats fired outcome=cancelled");
                    continue;
                }
                string outcome;
                try
                {
                    await prompts.SendScheduledAsync(repeat.ChatId, repeat.Text, cancellationToken).ConfigureAwait(false);
                    outcome = "sent";
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    // Never asked again: an uncertain send is never sent again by itself; the thread's own note says what Hermes did.
                    outcome = "failed";
                    log($"repeats send failed error={failure.GetType().Name}");
                }
                log($"repeats fired outcome={outcome}");
                await WriteAsync(profile, still with { LastOutcome = outcome }).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
        return soonest;
    }

    internal async Task<IReadOnlyList<RepeatingSend>> AllAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await ProfileAsync().ConfigureAwait(false) is { } profile ? await ReadAsync(profile).ConfigureAwait(false) : []; }
        finally { gate.Release(); }
    }

    private async Task<string?> ProfileAsync() => NotificationIdentity.ProfileOf(await localKey(CancellationToken.None).ConfigureAwait(false));

    private async Task<List<RepeatingSend>> ReadAsync(string profile)
    {
        var items = new List<RepeatingSend>();
        foreach (var (key, value, _) in await store.LiveRowsWithRevAsync(Prefix).ConfigureAwait(false))
        {
            RepeatingSend? item = null;
            try { item = JsonSerializer.Deserialize<RepeatingSend>(value); }
            catch (JsonException) { }
            if (item is { Id.Length: > 0, ChatId.Length: > 0 } && key == KeyOf(profile, item.Id)) items.Add(item);
        }
        return items;
    }

    private Task WriteAsync(string profile, RepeatingSend repeat) =>
        store.WriteOwnBatchAsync([(KeyOf(profile, repeat.Id), JsonSerializer.SerializeToUtf8Bytes(repeat))], pin: true);

    private static string KeyOf(string profile, string id) => SharedCategories.KeyOf(profile, SharedCategories.Repeat, id);

    public void Dispose() => stop.Cancel();
}
