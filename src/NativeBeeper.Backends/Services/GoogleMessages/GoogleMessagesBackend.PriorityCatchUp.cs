using System.Collections.Concurrent;
using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// Messages that came while the app was closed, fetched as soon as the connection's first list shows which conversations
// have them, instead of after the background crawl's 45 s start delay and the archive's paced load (minutes after a launch,
// since both pause while the app is in use). Seen 2026-10-02: a conversation opened 7 s after launch still drew those
// messages only when the phone answered. Each conversation's newest pages are read until they reach where the archive's
// last catch-up ended, as the crawl does, but through the same page an opening reads, so the helper can fetch their photos
// and make their link cards, and they are held in memory: from then on the conversation can be drawn from what is held
// (ReadHeldAsync) without waiting for the archive. Bounded (PriorityChats × PriorityPages, under the helper's 5,000 kept
// messages), and every call is skipIfBusy, so it never holds up anything the user opens.
internal sealed partial class GoogleMessagesBackend
{
    private const int PriorityChats = 10, PriorityPages = 2, FreshChecks = 40;
    private static readonly TimeSpan PriorityPageDelay = TimeSpan.FromMilliseconds(250);
    // Per conversation, the time from which this connection holds every message: the archive boundary its catch-up reached,
    // or that nothing had followed at connect. Cleared on every connection, with the heads it was measured against.
    private readonly ConcurrentDictionary<string, DateTimeOffset> freshFrom = new(StringComparer.Ordinal);
    private int priorityConnection;
    private Task priorityCatchUp = Task.CompletedTask;

    /// <summary>
    /// The messages this catch-up read past the archive's boundary in one conversation, by the app's ids: what came while
    /// the app was closed, now held and handed out by the helper, so their photos and link cards can be prepared before the
    /// conversation opens (IncomingPhotos.OfferCaughtUp), as Instagram's and Messenger's catch-ups already were. Their own
    /// events do not follow: these are page reads. Raised on a background thread, once per page.
    /// </summary>
    internal event Action<string, IReadOnlyList<string>>? CaughtUp;

    // Once per connection, from its first list.
    private void StartPriorityCatchUp(int connection, GoogleChat[] listed)
    {
        if (!historyIndexingEnabled || Interlocked.Exchange(ref priorityConnection, connection) == connection) return;
        // Tracked, so the launch's still-loading sign waits for it (StartupProgress).
        priorityCatchUp = CatchUpRuns.Shared.Track(Models.Qualify(""), Task.Run(() => PriorityCatchUpAsync(connection, listed)));
    }

    private async Task PriorityCatchUpAsync(int connection, GoogleChat[] listed)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        int caught = 0, unfinished = 0, pages = 0, fresh = 0;
        var outcome = "ok";
        bool Current() => !stopping && connected && connection == Volatile.Read(ref connectionNumber);
        try
        {
            if (await NotificationIdentity.ProfileAsync(tokens, CancellationToken.None) is not { } profile) { outcome = "no_profile"; return; }
            // Sets the archive's profile (and starts its paced load if the crawl has not yet), so what is read here is kept.
            _ = archive.LoadAsync(profile, YieldArchiveWarmupAsync);
            if (!await archive.LoadCrawlStatesAsync(profile)) { outcome = "unavailable"; return; }
            var recent = listed.OrderByDescending(chat => chat.At).ToArray();
            // Nothing after the boundary at connect: every message after it from now on comes live.
            foreach (var chat in recent.Take(FreshChecks))
            {
                if (!Current()) { outcome = "superseded"; return; }
                if (archive.CrawlState(chat.Id) is { NewestBoundary: { } boundary } state && GoogleHeldRules.Refusal(true, state, chat.At) is null &&
                    await BoundaryTimeAsync(profile, boundary) is { } at && freshFrom.TryAdd(chat.Id, at)) fresh++;
            }
            foreach (var chat in recent.Where(chat => archive.CrawlState(chat.Id) is { CatchUpCursor: null, NewestBoundary: not null } state &&
                GoogleHeldRules.NeedsCatchUp(state, chat.At)).Take(PriorityChats))
            {
                if (!Current()) { outcome = "superseded"; return; }
                var (done, read) = await CatchUpNowAsync(profile, chat, Current);
                pages += read;
                if (done) caught++; else unfinished++;
            }
        }
        catch (OperationCanceledException) { outcome = "cancelled"; }
        catch (BackendException failure) { outcome = GoogleMessagesLog.Reason(failure); }
        catch (Exception) { outcome = "failed"; }
        finally
        {
            GoogleMessagesLog.Write($"catchup priority outcome={outcome} caught={caught} unfinished={unfinished} pages={pages} fresh={fresh} " +
                $"ms={(long)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds}");
        }
    }

    private async Task<DateTimeOffset?> BoundaryTimeAsync(string profile, string boundary) =>
        messages.TryGetValue(boundary, out var held) ? held.At
        : await archive.ReadMessageAsync(profile, boundary, CancellationToken.None) is { } kept ? kept.At : null;

    // Newest pages until the archive's boundary, recorded as the crawl's catch-up records them, so the background crawl later
    // finds this conversation caught up or continues a capped one from where this stopped.
    private async Task<(bool Done, int Pages)> CatchUpNowAsync(string profile, GoogleChat chat, Func<bool> current)
    {
        var state = archive.CrawlState(chat.Id)!;
        var boundary = state.NewestBoundary!;
        DateTimeOffset? boundaryAt = null;
        // Where the archive's last catch-up ended, from memory or the archive, so only what came after it is named as
        // caught up; otherwise from the pages themselves once one reaches the boundary.
        var since = await BoundaryTimeAsync(profile, boundary);
        string? cursor = null;
        for (var read = 1; read <= PriorityPages; read++)
        {
            var (list, next) = await PriorityPageAsync(chat.Id, cursor);
            boundaryAt ??= list.FirstOrDefault(message => message.Id == boundary)?.At;
            NameCaughtUp(chat.Id, list, since ?? boundaryAt);
            var progress = GoogleCrawlProgress.CatchUp(state, chat.At, list.Select(message => message.Id).ToArray(), cursor, next);
            state = progress.State;
            archive.SetCrawlState(profile, state);
            if (progress.Complete)
            {
                // Held from the boundary up only when the pages reached it; a conversation that simply ran out stays the archive's.
                if (boundaryAt is { } at && current()) freshFrom[chat.Id] = at;
                return (true, read);
            }
            if (!current()) return (false, read);
            cursor = state.CatchUpCursor;
            await Task.Delay(PriorityPageDelay);
        }
        return (false, PriorityPages);
    }

    // What a page names as caught up (GoogleCaughtUp). A listener's failure is its own: the catch-up goes on.
    private void NameCaughtUp(string native, GoogleMessage[] list, DateTimeOffset? after)
    {
        if (CaughtUp is not { } listener) return;
        var named = GoogleCaughtUp.Named(native, list, after).Select(Slot.Ids.Qualify).ToArray();
        if (named.Length == 0) return;
        try { listener(Models.Qualify(native), named); }
        catch (Exception) { }
    }

    // One newest-first page through the opening's own read (ui.messagePage), so the helper keeps what it needs for each
    // message's photos and link card; waits rather than queueing behind anything else the helper is doing.
    private async Task<(GoogleMessage[] Messages, string? Next)> PriorityPageAsync(string native, string? cursor)
    {
        while (true)
        {
            if (stopping || !connected || helper is not { } connection) throw new OperationCanceledException();
            using var document = cursor is null ? null : JsonDocument.Parse(cursor);
            JsonElement result;
            try
            {
                // Never cancelled from here: HelperConnection ends the helper process on cancellation (as the crawl's calls).
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(135));
                result = await connection.CallAsync("ui.messagePage", new { chatId = native, cursor = document?.RootElement, count = IndexPageSize },
                    timeout.Token, skipIfBusy: true);
            }
            catch (InvalidOperationException ex) { throw GoogleReadFailure.Map(ex.Message); }
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True)
            { await Task.Delay(PriorityPageDelay); continue; }
            var list = (result.GetProperty("messages").Deserialize<GoogleMessage[]>(Json) ?? []).Where(message => message.ChatId == native).ToArray();
            foreach (var message in list) RememberMessage(message);
            var next = result.GetProperty("cursor").ValueKind == JsonValueKind.Null ? null : result.GetProperty("cursor").GetRawText();
            return (list, next);
        }
    }
}
