using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

/// <summary>
/// Warms the newest photos of the conversations used most recently, once the app has settled after launch (2026-10-04).
/// IncomingPhotos prepares what arrives and what a catch-up names; this is the rest: the last few pictures already in the
/// conversations at the top of the list, which opening one would otherwise download first.
/// </summary>
/// <remarks>
/// <para>Rows come only from what this PC already holds: the copies kept in memory and on disk for conversations opened
/// before, and the list's own last message. Nothing asks a service for history to find a photo, nothing opens a
/// conversation, and nothing marks one read; the only request is each picture's own download, through the same read a
/// conversation draws it with (the caller's <c>prepare</c>), which puts it in the media cache for next time.</para>
/// <para>Bounded every way: <see cref="Limits.Conversations"/> conversations, <see cref="Limits.PerConversation"/> photos
/// each, <see cref="Limits.Budget"/> bytes downloaded in all for the launch, one at a time with a pause after each
/// download, and each attempt given up after <see cref="Limits.Attempt"/>. A picture is tried once per launch whatever
/// happened. On a fresh install the first pass finds little (no copies kept yet), so two more passes follow, a few minutes
/// apart, while fewer conversations than asked for had any rows; the budget is shared by all three.</para>
/// <para>The caller leaves out what must not be fetched in the background at all (skip): Instagram, whose every request is
/// the user's real account and whose background budget is already spent on catch-up reads.</para>
/// </remarks>
public sealed class RecentPhotos(
    Func<CancellationToken, Task<IReadOnlyList<ChatSummary>>> listed,
    Func<ChatSummary, CancellationToken, Task<IReadOnlyList<ChatMessage>>> rows,
    Func<MessageAttachment, CancellationToken, Task<long?>> prepare,
    Action<string>? log = null, TimeProvider? clock = null, RecentPhotos.Limits? limits = null,
    Func<ChatSummary, bool>? skipChat = null, Func<MessageAttachment, bool>? skipPhoto = null, Func<MessageAttachment, bool>? hasStill = null)
{
    public sealed record Limits(int Conversations = 8, int PerConversation = 3, long Budget = 40L * 1024 * 1024,
        long ItemLimit = 8L * 1024 * 1024, double PaceSeconds = 1.5, double AttemptSeconds = 15)
    {
        public TimeSpan Pace => TimeSpan.FromSeconds(PaceSeconds);
        public TimeSpan Attempt => TimeSpan.FromSeconds(AttemptSeconds);
    }

    /// <summary>When each pass runs, from the moment the app has settled. Later ones only while rows were missing.</summary>
    public static readonly TimeSpan[] PassDelays = [TimeSpan.Zero, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(10)];

    /// <summary>What one pass did, by count: the line app.log gets, and what the checks read. Bytes is everything downloaded
    /// this launch so far, against the budget.</summary>
    public readonly record struct Pass(int Number, int Conversations, int WithRows, int Photos, int Held, int Fetched, int Failed,
        int Skipped, long Bytes, bool BudgetSpent)
    {
        public string LogLine(long ms) =>
            $"media warmed recent pass={Number} conversations={Conversations} with_rows={WithRows} photos={Photos} held={Held} " +
            $"fetched={Fetched} failed={Failed} skipped={Skipped} spent_kb={Bytes / 1024} budget={(BudgetSpent ? "spent" : "left")} ms={ms}";
    }

    private readonly Limits bounds = limits ?? new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    // Every picture tried this launch, whatever came of it: none is tried twice.
    private readonly HashSet<string> tried = new(StringComparer.Ordinal);
    private long spent;
    private int passes;

    public long Spent => Interlocked.Read(ref spent);

    /// <summary>The conversations to warm: the most recently active, leaving out archived, spam and anything skipped.</summary>
    public static IReadOnlyList<ChatSummary> Choose(IEnumerable<ChatSummary> listed, int count, Func<ChatSummary, bool>? skip = null) =>
        listed.Where(chat => !chat.IsArchived && !chat.IsSpam && !MergedConversations.IsMerged(chat.Id) && skip?.Invoke(chat) != true)
            .DistinctBy(chat => chat.Id, StringComparer.Ordinal)
            .OrderByDescending(chat => chat.LastActivity ?? DateTimeOffset.MinValue).ThenBy(chat => chat.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, count)).ToArray();

    /// <summary>
    /// A conversation's newest pictures, newest message first: what its opening draws at the bottom of the screen. The same
    /// pictures IncomingPhotos prepares (a photo, a sticker, a card's picture, a video's still where its provider names one),
    /// but only one already whole: never a Google photo the phone has not downloaded, and never one whose size is known to
    /// be over <paramref name="itemLimit"/>.
    /// </summary>
    public static IReadOnlyList<MessageAttachment> Newest(IEnumerable<ChatMessage> messages, int limit, long itemLimit,
        Func<MessageAttachment, bool>? hasStill = null, Func<MessageAttachment, bool>? skip = null)
    {
        var chosen = new List<MessageAttachment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages.Where(m => !m.IsDeleted && !m.IsHidden).OrderByDescending(m => m.Timestamp)
            .ThenByDescending(m => m.SortKey, StringComparer.Ordinal))
        {
            foreach (var photo in message.Attachments)
            {
                if (chosen.Count >= limit) return chosen;
                if (!IncomingPhotos.Eligible(photo, hasStill) || photo.Availability is not null || photo.Size > itemLimit ||
                    skip?.Invoke(photo) == true || !seen.Add(Identity(photo))) continue;
                chosen.Add(photo);
            }
            if (chosen.Count >= limit) break;
        }
        return chosen;
    }

    private static string Identity(MessageAttachment photo) => photo.Id + "\n" + photo.SourceUrl;

    /// <summary>The passes, from <paramref name="settle"/> on: the first, then the later ones while rows were missing.</summary>
    public async Task RunAsync(TimeSpan settle, CancellationToken token)
    {
        try
        {
            var began = time.GetUtcNow() + settle;
            foreach (var delay in PassDelays)
            {
                var wait = began + delay - time.GetUtcNow();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, time, token);
                if (!WantsAnother(await RunPassAsync(token))) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>Whether another pass may find more: the budget is not spent, and fewer conversations than asked for had
    /// rows (a fresh install, or a list still loading).</summary>
    public bool WantsAnother(Pass pass) => !pass.BudgetSpent && pass.WithRows < bounds.Conversations;

    /// <summary>One pass: chooses, gathers held rows, prepares what is new, and logs what it did.</summary>
    public async Task<Pass> RunPassAsync(CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var number = ++passes;
        var chats = Choose(await listed(token), bounds.Conversations, skipChat);
        var picks = new List<MessageAttachment>();
        var withRows = 0;
        foreach (var chat in chats)
        {
            IReadOnlyList<ChatMessage> held;
            try { held = await rows(chat, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { continue; } // A copy that cannot be read is only a conversation not warmed.
            if (held.Count > 0) withRows++;
            foreach (var photo in Newest(held, bounds.PerConversation, bounds.ItemLimit, hasStill, skipPhoto))
                if (tried.Add(Identity(photo))) picks.Add(photo);
        }
        int alreadyHeld = 0, fetched = 0, failed = 0, skipped = 0;
        var budgetSpent = Spent >= bounds.Budget;
        foreach (var photo in picks)
        {
            if (Spent >= bounds.Budget) { budgetSpent = true; break; }
            if (photo.Size is long size && size > bounds.Budget - Spent) { skipped++; continue; }
            long? got;
            using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                attempt.CancelAfter(bounds.Attempt);
                try { got = await prepare(photo, attempt.Token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { failed++; continue; } // Tried once; opening the conversation asks again.
            }
            if (got is null) { skipped++; continue; }
            if (got.Value <= 0) { alreadyHeld++; continue; }
            fetched++;
            Interlocked.Add(ref spent, got.Value);
            // One download at a time, and a pause after each, so the conversation being read now always comes first.
            await Task.Delay(bounds.Pace, time, token);
        }
        budgetSpent |= Spent >= bounds.Budget;
        var pass = new Pass(number, chats.Count, withRows, picks.Count, alreadyHeld, fetched, failed, skipped, Spent, budgetSpent);
        log?.Invoke(pass.LogLine(watch.ElapsedMilliseconds));
        return pass;
    }
}
