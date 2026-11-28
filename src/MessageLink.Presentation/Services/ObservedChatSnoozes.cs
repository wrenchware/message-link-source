using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// A snooze store whose snoozes can change without a <c>SetAsync</c> through it: a client of a Messages Server whose replica took another device's snooze,
/// or that went from its host's store to its rows (ReplicaChatSnoozes). <see cref="ObservedChatSnoozes"/> passes it on, so the reminders follow.
/// </summary>
public interface IChangingChatSnoozes
{
    event EventHandler? ChangedElsewhere;
}

// The snooze store, announcing every change. A snooze is set or ended from the conversation menu, the console, a
// notification's button, reading the conversation and the Google reset; the reminder Windows holds for it has to follow
// each of them at once (NotificationSnoozeService), and this is the one place they all pass. A store that changes by
// itself (IChangingChatSnoozes) is announced the same way.
public sealed class ObservedChatSnoozes : IChatSnoozes
{
    private readonly IChatSnoozes inner;

    public ObservedChatSnoozes(IChatSnoozes inner)
    {
        this.inner = inner;
        if (inner is IChangingChatSnoozes changing) changing.ChangedElsewhere += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public Task<DateTimeOffset?> GetAsync(string profile, string chatId, CancellationToken cancellationToken = default) =>
        inner.GetAsync(profile, chatId, cancellationToken);

    public async Task SetAsync(string profile, string chatId, DateTimeOffset? until, CancellationToken cancellationToken = default)
    {
        await inner.SetAsync(profile, chatId, until, cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<IReadOnlyList<ChatSnooze>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
}
