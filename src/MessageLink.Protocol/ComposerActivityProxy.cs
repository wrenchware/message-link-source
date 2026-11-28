using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// The composer's edits as the client sends them to a server (<c>typing.noteActivity</c>). Fire and forget: the composer
/// never waits for a call or sees it fail, and an edit that cannot be told to the server is lost, which at worst lets a typing stop go
/// out when it was due. At most one call per <see cref="Interval"/> per conversation, and the first edit after a quiet interval goes at once.
/// Every stop timer in the backends is five seconds, so a pause of up to <see cref="Interval"/> in what the server hears leaves a stop due
/// between four and five seconds after the last edit, never earlier. The shared composer clock the interface thread reads
/// (<c>ComposerActivity</c>, 0.9 s) is not served through here: the client notes it for every edit.
/// </summary>
public sealed class ComposerActivityProxy(ProtocolClient client, TimeProvider? clock = null) : IComposerActivitySink
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, long> sent = new(StringComparer.Ordinal);

    public void NoteComposerActivity(string? chatId)
    {
        if (string.IsNullOrEmpty(chatId) || !Acquire(chatId)) return;
        // Off the caller's thread: the caller is a keystroke. A failure is not the composer's business.
        _ = Task.Run(() => client.CallAsync<ChatIdParams, Done>(ProtocolMethods.TypingNoteActivity, new(chatId)))
            .ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    private bool Acquire(string chatId)
    {
        var now = time.GetTimestamp();
        lock (gate)
        {
            if (sent.TryGetValue(chatId, out var previous) && time.GetElapsedTime(previous, now) < Interval) return false;
            // A conversation not typed in for a minute is forgotten, so the table stays as large as the conversations typed in lately.
            if (sent.Count >= 16)
                foreach (var stale in sent.Where(pair => time.GetElapsedTime(pair.Value, now) > TimeSpan.FromMinutes(1)).Select(pair => pair.Key).ToArray()) sent.Remove(stale);
            sent[chatId] = now;
            return true;
        }
    }
}
