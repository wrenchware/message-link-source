using System.Runtime.CompilerServices;
using System.Text.Json;
using MessageLink.Core.Services;
using MessageLink.GoogleMessages.Spike;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services.Instagram;

// Typing for Instagram, in both directions, the way Messenger does it. The helper reports who started or stopped in
// the app's own ids, and anyone not reported again within a few seconds is taken to have stopped — upstream's fixed
// six seconds, since Instagram's own time-to-live has no documented unit. Only inbox conversations: the helper drops
// reports from anywhere else, and a message request or spam thread is never told this account is typing.
internal sealed partial class InstagramBackend : ITypingService
{
    private static readonly TimeSpan TypingLasts = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan TypingIdle = TimeSpan.FromSeconds(5);
    private readonly object typingLock = new();
    private readonly DirectTypingState typingNow = new();
    private readonly HashSet<string> typingChats = new(StringComparer.Ordinal);
    private readonly OutgoingTyping typingStops = new(TypingIdle);

    public bool CanReceive => IsConnected;
    public bool CanSend => IsConnected;

    private void ReceiveTyping(JsonElement data)
    {
        if (identity is not { } known || InstagramTypingEvent.Read(data, known) is not { } report) return;
        var now = DateTimeOffset.UtcNow;
        lock (typingLock)
        {
            typingChats.Add(report.ChatId);
            typingNow.Apply(report.ChatId, report.PersonId, report.Active, now + TypingLasts, now);
        }
    }

    // Nobody is typing on a connection that has gone; the watchers then say so for every conversation that had somebody.
    private void ForgetTyping()
    {
        lock (typingLock)
        {
            typingNow.Clear();
            typingStops.Clear();
        }
    }

    // One per open conversation window, all over the one shared set.
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
                var known = identity;
                var list = chats;
                List<TypingUpdate> current = [];
                foreach (var chatId in typingChats.ToArray())
                {
                    var people = IsConnected && known is not null ? typingNow.Active(chatId, now) : [];
                    var detail = known is null ? null : list.FirstOrDefault(chat => known.Chat(chat.Id) == chatId);
                    current.Add(new(chatId, people.Select(person => new TypingParticipant(person,
                        detail?.Participants.FirstOrDefault(p => known!.Person(p.Id) == person)?.Name ?? "Someone",
                        typingNow.ExpiresAt(chatId, person))).ToArray()));
                    if (people.Count == 0) typingChats.Remove(chatId);
                }
                updates = watch.Next(current);
            }
            foreach (var update in updates) yield return update;
        }
    }

    // A start as the person types, returning once it is sent; the stop once they have paused, on a timer the composer's
    // edits put off, and not tied to the window, so one closed mid-sentence still says the typing stopped.
    public async Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default)
    {
        if (identity is not { } known || helper is not { } started || !IsConnected || !known.Owns(chatId)) return;
        var native = known.NativeChat(chatId);
        if (chats.FirstOrDefault(chat => chat.Id == native) is not { Folder: "inbox", ReadOnly: false }) return;
        var timer = false;
        lock (typingLock)
        {
            if (isTyping) timer = typingStops.Started(chatId, DateTimeOffset.UtcNow);
            else typingStops.Take(chatId);
        }
        await SendTypingAsync(started, native, isTyping);
        if (timer) _ = StopWhenIdleAsync(started, chatId, native);
    }

    /// <summary>An edit in the composer, which puts off the stop for a conversation typing was sent to.</summary>
    internal void NoteComposerActivity(string? chatId)
    {
        if (chatId is null || !InstagramIdentity.OwnsAny(chatId)) return;
        lock (typingLock) typingStops.Activity(chatId, DateTimeOffset.UtcNow);
    }

    // A message that went ends the typing that led to it, so the other person is not left watching dots under it.
    private void TypingEndedBySend(HelperConnection started, string chatId, string native)
    {
        bool pending;
        lock (typingLock) pending = typingStops.Take(chatId);
        if (pending) _ = SendTypingAsync(started, native, false);
    }

    private async Task StopWhenIdleAsync(HelperConnection started, string chatId, string native)
    {
        while (true)
        {
            TimeSpan? wait;
            lock (typingLock)
            {
                // After a recovery the pending stop belongs to the new helper's own start and timer; taking it here would
                // drop it, since this helper can no longer send it.
                if (!ReferenceEquals(helper, started)) return;
                var now = DateTimeOffset.UtcNow;
                if (typingStops.TakeDue(chatId, now)) break;
                wait = typingStops.Remaining(chatId, now);
            }
            if (wait is not { } delay) return;
            await Task.Delay(delay);
        }
        await SendTypingAsync(started, native, false);
    }

    // The helper answers typing beside its queue within six seconds, and at most one more waits per conversation, so
    // this allows for one ahead of it. Best effort and never retried: a typing call that fails is simply not shown. Only
    // a start is skipped when the pipe is busy (a conversation full of pictures can fill it); the next edit brings
    // another start, but nothing brings another stop.
    private static async Task SendTypingAsync(HelperConnection started, string native, bool typing)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await started.CallAsync("ui.typing", new { chatId = native, typing }, timeout.Token, skipIfBusy: typing);
        }
        catch (Exception) { }
    }
}
