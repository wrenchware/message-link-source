using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.Terminal.Session;

/// <summary>The window's interface-thread dispatcher, for a terminal: the work is posted to the session's loop and its task completes when the work has.</summary>
internal sealed class LoopDispatcher(SynchronizationContext loop) : IUiDispatcher
{
    public Task InvokeAsync(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Post(async _ =>
        {
            try
            {
                await action();
                done.TrySetResult();
            }
            catch (OperationCanceledException canceled) { done.TrySetCanceled(canceled.CancellationToken); }
            catch (Exception failure) { done.TrySetException(failure); }
        }, null);
        return done.Task;
    }
}

/// <summary>
/// Typing in every conversation, for the typing strip and the activity stream, with sending taken away: mlink never tells anyone it is typing, so a call to
/// start or stop does nothing and asks nothing of the server.
/// </summary>
internal sealed class WatchOnlyTyping(ITypingService watched) : ITypingService
{
    public bool CanReceive => watched.CanReceive;
    public bool CanSend => false;
    public IAsyncEnumerable<TypingUpdate> WatchAsync(CancellationToken cancellationToken = default) => watched.WatchAsync(cancellationToken);
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
