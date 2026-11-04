using System.Text.Json;
using NativeBeeper.Core.Backend;

namespace NativeBeeper.App.Services.GoogleMessages;

// Gradually downloads older history into the encrypted search archive. It waits for a quiet
// period after user activity, never queues behind the helper (skipIfBusy), pauses between
// pages, backs off on errors and resumes from saved cursors after a restart.
internal sealed partial class GoogleMessagesBackend
{
    // Gentle on the phone: avatars and history for what is on screen share the same phone
    // connection, and the helper allows avatar thumbnails only 5 s.
    private static readonly TimeSpan IndexStartDelay = TimeSpan.FromSeconds(45), IndexQuietPeriod = TimeSpan.FromSeconds(15), IndexPageDelay = TimeSpan.FromMilliseconds(2500), IndexRealtimePause = TimeSpan.FromSeconds(30);
    private const int IndexPageSize = 50;
    private readonly GoogleIndexRun indexing = new();
    // Always on (owner, 2026-11-04: no switch for it, and an old choice of off no longer counts). It pauses while someone is active.
    private volatile bool historyIndexingEnabled = true;
    internal bool HistoryIndexingEnabled
    {
        get => historyIndexingEnabled;
        set
        {
            historyIndexingEnabled = value;
            if (value) { if (connected) StartHistoryIndexing(); }
            else indexing.Cancel();
        }
    }
    private bool IsIndexing => indexing.IsRunning;

    private void StartHistoryIndexing()
    {
        if (!historyIndexingEnabled)
        {
            // Saved history still loads for search.
            _ = Task.Run(async () => { try { await LoadArchiveAsync(CancellationToken.None); } catch { } });
            return;
        }
        // Turned off and on again while the stopped run is still finishing: a fresh run follows it (GoogleIndexRun).
        if (indexing.Start(() => !stopping && historyIndexingEnabled, IndexHistoryAsync) == GoogleIndexStart.Queued)
            GoogleMessagesLog.Write("index restart queued");
    }

    private async Task StopHistoryIndexingAsync()
    {
        try { await indexing.Cancel().WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        try { await archive.FlushAsync(); } catch { }
    }

    private async Task IndexHistoryAsync(CancellationToken token)
    {
        try
        {
            GoogleMessagesLog.Write("index starting");
            var profile = await LoadArchiveAsync(token);
            if (profile is null) { GoogleMessagesLog.Write("index unavailable"); return; }
            // Let the chat list, visible avatars and the open conversation load first.
            await Task.Delay(IndexStartDelay, token);
            var listed = new Dictionary<string, GoogleChat>();
            foreach (var folder in new[] { "inbox", "archive" })
                foreach (var chat in (await IndexCallAsync(profile, "ui.chatList", new { count = 2000, folder }, token)).Deserialize<GoogleChat[]>(Json) ?? [])
                    if (chat.Id.Length > 0) listed[chat.Id] = chat;
            archive.RememberChats(profile, listed.Values);
            var ordered = listed.Values.OrderByDescending(c => c.At).ToArray();
            GoogleMessagesLog.Write($"index conversations={ordered.Length}");
            // The connection's own catch-up (PriorityCatchUp) pages the same conversations; two walks at once would each
            // record the other's cursor.
            try { await priorityCatchUp.WaitAsync(token); } catch (Exception ex) when (ex is not OperationCanceledException) { }
            // Messages that arrived while the app was closed are quick to catch up on, so go first.
            foreach (var chat in ordered)
                if (archive.CrawlState(chat.Id) is { Done: true } state && chat.At > state.UpdatedAt)
                    await IndexNewestAsync(profile, chat, state, token);
            foreach (var chat in ordered)
                if (archive.CrawlState(chat.Id) is not { Done: true })
                    await IndexOlderAsync(profile, chat, token);
            var (done, known) = archive.CrawlProgress();
            GoogleMessagesLog.Write($"index completed={done} known={known}");
        }
        catch (OperationCanceledException) { GoogleMessagesLog.Write("index cancelled"); }
        catch (BackendException failure) { GoogleMessagesLog.Write($"index stopped reason={GoogleMessagesLog.Reason(failure)}"); } // Authentication and pairing problems surface through the main connection.
    }

    private async Task IndexOlderAsync(string profile, GoogleChat chat, CancellationToken token)
    {
        var state = archive.CrawlState(chat.Id) ?? new GoogleCrawlState(chat.Id, null, false, 0, 0, DateTimeOffset.MinValue);
        var failures = 0;
        while (!state.Done)
        {
            JsonElement page;
            try { page = await IndexPageAsync(profile, chat.Id, state.Cursor, token); }
            catch (BackendException ex) when (Retryable(ex) && ++failures < 3)
            { await Task.Delay(TimeSpan.FromSeconds(30 * failures), token); continue; }
            catch (BackendException ex) when (Retryable(ex)) { return; } // A later run retries this conversation.
            failures = 0;
            var (list, next, total) = ReadIndexPage(page, chat.Id);
            archive.RememberMessages(profile, list);
            var done = list.Length == 0 || next is null || next == state.Cursor;
            state = state with
            {
                Cursor = next, Done = done,
                Fetched = state.Fetched + list.Length, Total = total ?? state.Total, UpdatedAt = done ? chat.At : state.UpdatedAt,
                NewestBoundary = state.NewestBoundary ?? (state.Cursor is null ? list.FirstOrDefault()?.Id : null)
            };
            archive.SetCrawlState(profile, state);
            await Task.Delay(IndexPageDelay, token);
        }
    }

    // Newest pages until the exact head of the last contiguous crawl is reached. Cached-message membership is not a
    // boundary: opening a conversation or receiving realtime events can populate arbitrary newer rows first.
    private async Task IndexNewestAsync(string profile, GoogleChat chat, GoogleCrawlState state, CancellationToken token)
    {
        var cursor = state.CatchUpCursor;
        for (var pages = 0; pages < 20; pages++)
        {
            JsonElement page;
            try { page = await IndexPageAsync(profile, chat.Id, cursor, token); }
            catch (BackendException ex) when (Retryable(ex)) { return; }
            var (list, next, _) = ReadIndexPage(page, chat.Id);
            // State saved before NewestBoundary was introduced has no exact watermark. Preserve the former
            // already-archived stop for that legacy state instead of walking the entire conversation every pass.
            var reachedLegacyKnown = state.NewestBoundary is null && list.Any(message => archive.Contains(message.Id));
            archive.RememberMessages(profile, list);
            var progress = GoogleCrawlProgress.CatchUp(state, chat.At,
                list.Select(message => message.Id).ToArray(), cursor, next, reachedLegacyKnown);
            state = progress.State;
            archive.SetCrawlState(profile, state);
            await Task.Delay(IndexPageDelay, token);
            if (progress.Complete) return;
            cursor = state.CatchUpCursor;
        }
        // The cap is a pause, not completion. The continuation and original watermark remain, so a later run resumes
        // this exact interval instead of advancing past a gap.
    }

    // Authentication problems, rate limits and access denial end the whole run instead of
    // retrying chat by chat; the next connection starts a fresh run.
    private static bool Retryable(BackendException ex) =>
        ex.Failure is not (BackendFailure.AuthenticationRequired or BackendFailure.RateLimited or BackendFailure.Forbidden);

    private static (GoogleMessage[] Messages, string? Cursor, long? Total) ReadIndexPage(JsonElement page, string chatId)
    {
        var list = (page.GetProperty("messages").Deserialize<GoogleMessage[]>(Json) ?? []).Where(m => m.ChatId == chatId).ToArray();
        var cursor = page.GetProperty("cursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("cursor").GetRawText();
        long? total = page.TryGetProperty("total", out var value) && value.TryGetInt64(out var count) ? count : null;
        return (list, cursor, total);
    }

    private async Task<JsonElement> IndexPageAsync(string profile, string chatId, string? cursor, CancellationToken token)
    {
        using var document = cursor is null ? null : JsonDocument.Parse(cursor);
        return await IndexCallAsync(profile, "ui.archivePage", new { chatId, cursor = document?.RootElement, count = IndexPageSize }, token);
    }

    private async Task<JsonElement> IndexCallAsync(string profile, string method, object parameters, CancellationToken token)
    {
        var pausedForActivity = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (stopping || !historyIndexingEnabled || archive.Profile != profile) throw new OperationCanceledException();
            var recentlyActive = RecentlyActive(IndexQuietPeriod);
            if (!connected || helper is not { } connection || recentlyActive || RecentRealtimeMessage(IndexRealtimePause))
            {
                if (recentlyActive && !pausedForActivity)
                {
                    GoogleMessagesLog.Write("index paused reason=activity");
                    pausedForActivity = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                continue;
            }
            if (pausedForActivity)
            {
                GoogleMessagesLog.Write("index resumed reason=quiet");
                pausedForActivity = false;
            }
            JsonElement result;
            try
            {
                // Never cancel an in-flight helper call from here: HelperConnection ends the helper
                // process on cancellation. Stop requests are honored between calls instead.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(135));
                result = await connection.CallAsync(method, parameters, timeout.Token, skipIfBusy: true);
            }
            catch (InvalidOperationException ex) { throw GoogleReadFailure.Map(ex.Message); }
            catch (Exception ex) when (ex is not BackendException) { throw new BackendException(BackendFailure.Unavailable, "Couldn't search Google Messages history just now. Try again."); }
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("skipped", out var skipped) && skipped.ValueKind == JsonValueKind.True)
            { await Task.Delay(TimeSpan.FromSeconds(1), token); continue; }
            return result;
        }
    }
}
