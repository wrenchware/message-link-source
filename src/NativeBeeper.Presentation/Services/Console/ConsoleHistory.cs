using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

/// <summary>A message from before the console's stream began, and the conversation it belongs to.</summary>
public sealed record ConsoleHistoryItem(ChatSummary Chat, ChatMessage Message);

/// <summary>
/// What one service keeps on this PC well enough to answer "every message between these times" by itself: Google
/// Messages' search archive. A failure means it cannot answer now, and its conversations fall back to what the app
/// saved of each one.
/// </summary>
public interface IRecentMessages
{
    /// <summary>The conversation id prefix the answer covers, such as "google-direct:".</summary>
    string Scope { get; }
    /// <summary>Newest first, at most <paramref name="limit"/>, each with the time in [since, until).</summary>
    Task<IReadOnlyList<ConsoleHistoryItem>> RecentAsync(DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken);
}

/// <summary>
/// The console's history (/history): the messages of a recent span as the host already holds them (in mlink, what the
/// server has sent this terminal), rather than only what arrived since the console started. Read only from what is held — a service's own
/// archive where it has one, each conversation's saved history, and failing both its newest message from the list — so
/// no service is asked for anything and nothing is marked read.
/// </summary>
/// <remarks>
/// A conversation that was never opened on this PC and has no archive shows only its newest message; that is all this
/// PC knows of it. The console decides where the span ends: at the oldest line its stream still holds, so the two meet.
/// </remarks>
public sealed class ConsoleHistory(Func<IReadOnlyList<ChatSummary>> chats, Func<string, Task<HistorySnapshot?>> saved,
    IReadOnlyList<IRecentMessages> sources, MergedConversations? merges = null)
{
    public static readonly TimeSpan DefaultSpan = TimeSpan.FromHours(24);
    public static readonly TimeSpan LongestSpan = TimeSpan.FromDays(7);
    /// <summary>At most this many lines, the newest: a day of a busy group should not push the live lines off the buffer.</summary>
    public const int Limit = 500;
    private const int Readers = 4;

    /// <summary>Oldest first. Called on the interface thread; the list is read once before anything is awaited.</summary>
    public async Task<IReadOnlyList<ConsoleHistoryItem>> ReadAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        var listed = new Dictionary<string, ChatSummary>(StringComparer.Ordinal);
        foreach (var chat in chats()) listed.TryAdd(chat.Id, chat);
        var found = new Dictionary<(string Chat, string Id), ConsoleHistoryItem>();
        void Keep(ChatSummary chat, ChatMessage message)
        {
            if (message.Timestamp < since || message.Timestamp >= until || message.IsHidden || message.IsDeleted || message.Type == "system") return;
            // The list's copy of a conversation carries this PC's names and flags; a source's own is the fallback.
            found.TryAdd((message.ChatId, message.Id), new(listed.GetValueOrDefault(message.ChatId) ?? chat, message));
        }

        var answered = new List<string>();
        foreach (var source in sources)
        {
            try
            {
                foreach (var item in await source.RecentAsync(since, until, Limit, cancellationToken)) Keep(item.Chat, item.Message);
                answered.Add(source.Scope);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* Its conversations fall back to what was saved of each. */ }
        }

        var active = listed.Values.Where(chat => (chat.LastActivity ?? chat.Preview?.Timestamp) >= since &&
            !answered.Any(scope => chat.Id.StartsWith(scope, StringComparison.Ordinal))).ToArray();
        using var gate = new SemaphoreSlim(Readers);
        var reads = active.Select(async chat =>
        {
            await gate.WaitAsync(cancellationToken);
            try { return (Chat: chat, Messages: await SavedAsync(chat, cancellationToken)); }
            finally { gate.Release(); }
        }).ToArray();
        foreach (var (chat, messages) in await Task.WhenAll(reads))
        {
            var before = found.Count;
            foreach (var message in messages) Keep(chat, message);
            // Nothing saved in the span: the list's newest message is still something this PC knows.
            if (found.Count == before && chat.Preview is { } preview && preview.ChatId == chat.Id) Keep(chat, preview);
        }
        return [.. found.Values.OrderByDescending(item => item.Message.Timestamp).ThenByDescending(item => item.Message.SortKey, StringComparer.Ordinal)
            .Take(Limit).Reverse()];
    }

    // What the app saved of the conversation, and of the merged conversation it is shown in, which is saved under the
    // merged id and holds every member's messages.
    private async Task<IReadOnlyList<ChatMessage>> SavedAsync(ChatSummary chat, CancellationToken cancellationToken)
    {
        var keys = new List<string> { chat.Id };
        if (merges?.DisplayId(chat.Id) is { } display && display != chat.Id) keys.Add(display);
        var messages = new List<ChatMessage>();
        foreach (var key in keys)
        {
            try
            {
                if (await saved(key).WaitAsync(cancellationToken) is { } snapshot)
                    messages.AddRange(snapshot.Messages.Where(message => message.ChatId == chat.Id));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { /* An unreadable saved copy is a conversation this PC knows less of. */ }
        }
        return messages;
    }
}
