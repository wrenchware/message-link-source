using System.Runtime.CompilerServices;
using System.Text.Json;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.Messenger;

// Typing for Messenger, on both of its connections. The helper reports who started or stopped; anyone not reported
// again within a few seconds is taken to have stopped, which is how Facebook's own clients behave.
internal sealed partial class MessengerBackend : ITypingService
{
    private static readonly TimeSpan TypingLasts = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TypingIdle = TimeSpan.FromSeconds(5);
    private readonly object typingLock = new();
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> typingNow = [];
    private readonly OutgoingTyping typingStops = new(TypingIdle);
    private NativeBeeper.GoogleMessages.Spike.HelperConnection? typingHelper; // whose starts typingStops holds

    public bool CanReceive => helper is not null;
    public bool CanSend => helper is not null;

    // Called from OnHelperEvent for "typing" events.
    private void ReceiveTyping(JsonElement data)
    {
        var chat = data.TryGetProperty("chatId", out var c) ? c.GetString() : null;
        var sender = data.TryGetProperty("sender", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(chat) || string.IsNullOrEmpty(sender)) return;
        var typing = data.TryGetProperty("typing", out var t) && t.GetBoolean();
        var chatId = Models.Qualify(chat);
        lock (typingLock)
        {
            if (!typingNow.TryGetValue(chatId, out var people)) typingNow[chatId] = people = [];
            if (typing) people[MessengerModels.Person(sender)] = DateTimeOffset.UtcNow + TypingLasts;
            else people.Remove(MessengerModels.Person(sender));
        }
    }

    // One of these runs per open conversation window, all over the one shared set.
    public async IAsyncEnumerable<TypingUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var watch = new TypingWatch();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(300));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            List<TypingUpdate> updates;
            lock (typingLock)
            {
                var now = DateTimeOffset.UtcNow;
                List<TypingUpdate> current = [];
                foreach (var (chatId, people) in typingNow.ToArray())
                {
                    foreach (var expired in people.Where(p => p.Value <= now || helper is null).Select(p => p.Key).ToArray()) people.Remove(expired);
                    var native = Models.Native(chatId);
                    var detail = chats.FirstOrDefault(chat => chat.Id == native);
                    current.Add(new(chatId, people.Select(p => new TypingParticipant(p.Key,
                        detail?.Participants.FirstOrDefault(person => MessengerModels.Person(person.Id) == p.Key)?.Name ?? "Someone", p.Value)).ToArray()));
                    if (people.Count == 0) typingNow.Remove(chatId);
                }
                updates = watch.Next(current);
            }
            foreach (var update in updates) yield return update;
        }
    }

    // Starting is sent as the person types; stopping is sent once they have paused, so the other end does not show
    // them typing long after they stopped without sending. The start returns as soon as it is sent: the view waits on
    // it and drops every edit meanwhile, so waiting out the pause here too is what made the stop go out five seconds
    // into every burst however long it lasted.
    public async Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started || !Models.Owns(chatId)) return;
        var timer = false;
        lock (typingLock)
        {
            // Stops still pending from a helper that has since been replaced (a socket that failed for good restarts it)
            // can no longer be sent; left in place they would keep this start from getting a timer of its own.
            if (!ReferenceEquals(typingHelper, started)) { typingStops.Clear(); typingHelper = started; }
            // A start keeps any stop pending and puts it off; a stop sent now is the pending one.
            if (isTyping) timer = typingStops.Started(chatId, DateTimeOffset.UtcNow);
            else typingStops.Take(chatId);
        }
        await SendTypingAsync(started, chatId, isTyping);
        if (timer) _ = StopWhenIdleAsync(started, chatId);
    }

    /// <summary>An edit in the composer, which puts off the stop for a conversation typing was sent to.</summary>
    internal void NoteComposerActivity(string? chatId)
    {
        if (chatId is null || !Models.Owns(chatId)) return;
        lock (typingLock) typingStops.Activity(chatId, DateTimeOffset.UtcNow);
    }

    // Not tied to the conversation window: one closed mid-sentence still tells the other end the typing stopped.
    private async Task StopWhenIdleAsync(NativeBeeper.GoogleMessages.Spike.HelperConnection started, string chatId)
    {
        while (true)
        {
            TimeSpan? wait;
            lock (typingLock)
            {
                // A pending stop belongs to the helper now running, whose own start timed it; this one has nothing to send.
                if (!ReferenceEquals(helper, started)) return;
                var now = DateTimeOffset.UtcNow;
                if (typingStops.TakeDue(chatId, now)) break;
                wait = typingStops.Remaining(chatId, now);
            }
            if (wait is not { } delay) return;
            await Task.Delay(delay);
        }
        await SendTypingAsync(started, chatId, false);
    }

    // A start that finds the pipe busy is skipped: the next edit brings another. A stop is not, because nothing brings
    // another, and a skipped one leaves the other person watching dots until Facebook times them out.
    private async Task SendTypingAsync(NativeBeeper.GoogleMessages.Spike.HelperConnection started, string chatId, bool typing)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await started.CallAsync("ui.typing", new { chatId = Models.Native(chatId), typing }, timeout.Token, skipIfBusy: typing);
        }
        catch (Exception) { /* Typing is best effort and never retried. */ }
    }
}
