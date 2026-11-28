using MessageLink.Core.Services;
using MessageLink.GoogleMessages.Spike;

namespace MessageLink.App.Services.Instagram;

// What came while nothing was reading Instagram, read before anyone opens it. Instagram announces nothing that arrived
// while the app was closed — a new connection counts everything it can see as already considered — and the list carries
// only a message's words, never its picture, so a reel shared while the app was closed was drawn only when its
// conversation's own read came back: a second after the click, 683 px of reel sliding in (2026-10-02). Reading those
// conversations as the connection starts holds their newest pages (ReadHeldAsync draws them at once) and lets their
// pictures be prepared (CaughtUp). A photo, video or share arriving while the app runs is read whole the same way before
// it is announced. Each read is a request on the real account that nobody asked for by opening anything, so they are few,
// a moment apart and under one hourly ceiling; reading a conversation sends nothing and marks nothing read.
internal sealed partial class InstagramBackend
{
    private const int CatchUpChats = 5, ArrivalReadsPerPoll = 3, OwnReadsPerHour = 30;
    // After the launch's own opening has had its turn; a moment between reads; and a bound that leaves the conversation to
    // its opening's own read when Instagram is slow. An arrival's bound is shorter: its announcement waits on it.
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromSeconds(2), OwnReadGap = TimeSpan.FromMilliseconds(1500),
        CatchUpTimeout = TimeSpan.FromSeconds(15), ArrivalTimeout = TimeSpan.FromSeconds(8);
    private readonly InstagramReadBudget ownReads = new(OwnReadsPerHour);

    /// <summary>
    /// A read the server's whole history makes on its own (filling or catching up a conversation nobody opened), taken from the same
    /// hourly budget as the catch-up and the arrivals, and only while half of it is left for them: an arrival read late is a notification late.
    /// </summary>
    internal bool TryTakeHistoryRead(DateTimeOffset now) => ownReads.TryTake(now, leaveFree: OwnReadsPerHour / 2);

    /// <summary>
    /// The messages a connection caught up on in one conversation, by the app's ids: what came while the app was closed,
    /// now held whole, so their pictures can be prepared before the conversation opens. Raised on a background thread.
    /// </summary>
    internal event Action<string, IReadOnlyList<string>>? CaughtUp;

    private void StartCatchUp(HelperConnection started, InstagramChat[] previous, InstagramChat[] fresh)
    {
        var candidates = InstagramCatchUp.Candidates(previous, fresh, CatchUpChats);
        if (candidates.Length == 0)
        {
            InstagramLog.Write($"instagram catchup outcome={(previous.Length == 0 ? "no_baseline" : "none")}");
            return;
        }
        // Counted as running until it ends, so the launch is not called settled in the middle of it (StartupProgress).
        _ = MessageLink.Presentation.Services.CatchUpRuns.Shared.Track("instagram:", Task.Run(() => CatchUpAsync(started, candidates)));
    }

    private async Task CatchUpAsync(HelperConnection started, (string Chat, long Since)[] candidates)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int read = 0, skipped = 0, failed = 0, messages = 0;
        var outcome = "ok";
        try
        {
            await Task.Delay(CatchUpDelay);
            foreach (var (native, since) in candidates)
            {
                if (!ReferenceEquals(helper, started) || !IsConnected || identity is not { } known) { outcome = "superseded"; break; }
                var chatId = known.Chat(native);
                // Opened meanwhile, and so read already.
                if (chats.FirstOrDefault(chat => chat.Id == native) is not { } listed ||
                    heldPages.Current(started, chatId, listed.At, listed.Last?.Id)) { skipped++; continue; }
                if (!ownReads.TryTake(DateTimeOffset.UtcNow)) { outcome = "budget"; break; }
                if (read + failed > 0) await Task.Delay(OwnReadGap);
                try
                {
                    using var bound = new CancellationTokenSource(CatchUpTimeout);
                    var page = await ReadMessagesAsync(chatId, null, bound.Token);
                    read++;
                    var newer = page.Items.Where(message => message.Timestamp.ToUnixTimeMilliseconds() > since).Select(message => message.Id).ToArray();
                    messages += newer.Length;
                    if (newer.Length > 0) CaughtUp?.Invoke(chatId, newer);
                }
                // Its opening reads it as it always did.
                catch (Exception) { failed++; }
            }
        }
        catch (Exception) { outcome = "failed"; }
        finally
        {
            InstagramLog.Write($"instagram catchup outcome={outcome} candidates={candidates.Length} read={read} skipped={skipped} " +
                $"failed={failed} messages={messages} ms={clock.ElapsedMilliseconds}");
        }
    }

    // The conversations a poll found a photo, a video or a share in, read whole before the poll announces them. One that
    // cannot be read in time, or is over the ceiling, is announced with the list's words for it, as before.
    private async Task ReadArrivalsAsync(HelperConnection started, InstagramIdentity known, string[] partial, CancellationToken cancellationToken)
    {
        int read = 0, failed = 0, refused = 0;
        foreach (var native in partial)
        {
            if (!ReferenceEquals(helper, started) || !IsConnected) break;
            if (read + failed >= ArrivalReadsPerPoll || !ownReads.TryTake(DateTimeOffset.UtcNow)) { refused++; continue; }
            if (read + failed > 0) await Task.Delay(OwnReadGap, cancellationToken);
            try
            {
                using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bound.CancelAfter(ArrivalTimeout);
                await ReadMessagesAsync(known.Chat(native), null, bound.Token);
                read++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { failed++; }
        }
        InstagramLog.Write($"instagram arrivals read={read} failed={failed} refused={refused}");
    }

    /// <summary>
    /// What came after an opening conversation's saved copy, from the newest page this connection read of it (a catch-up,
    /// an arrival read whole, or an earlier opening), while the list shows nothing newer than that page. Memory only:
    /// never a request. Null leaves the opening to wait for its own read, as before.
    /// </summary>
    public Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || helper is not { } current || identity is not { } known || !known.Owns(chatId)) return Task.FromResult<HeldHistory?>(null);
        var native = known.NativeChat(chatId);
        var held = chats.FirstOrDefault(chat => chat.Id == native) is { } listed
            ? heldPages.Answer(current, chatId, listed.At, listed.Last?.Id, after, limit) : null;
        InstagramLog.Write($"instagram held outcome={(held is null ? "none" : held.Complete ? "ok" : "partial")} " +
            $"newer={held?.Messages.Count ?? 0} chat={InstagramLog.Tag(chatId)}");
        return Task.FromResult(held);
    }
}
