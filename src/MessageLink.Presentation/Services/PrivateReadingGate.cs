using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

/// <summary>
/// The one place private reading is decided. The app registers this as its <see cref="IReadStateWriter"/> and its
/// <see cref="ITypingService"/>, so every reader of those — an open conversation, a merged one member by member, a
/// reopened window once it is engaged, anything added later — goes through it without knowing it exists:
/// <list type="bullet">
/// <item>A read marker for a conversation whose service has private reading on is not sent. The call completes as if it
/// had been, so the caller shows the conversation read (the list, badges, tray and notifications all follow its local
/// read state) and <see cref="ReadHereMarks"/> keeps it read on this PC.</item>
/// <item>A typing start for such a conversation is not sent. A stop always is: one only follows a start, and a start sent
/// just before the switch was turned on must not leave the other person watching dots.</item>
/// <item><see cref="Explicit"/> is the writer for what the user asked for by name (a notification's Mark read, the
/// conversation menu's Send read receipt, the console's /read). It always tells the service.</item>
/// </list>
/// Whatever passes is marked as admitted for the length of the call, and the routers that talk to the providers refuse
/// a private-reading conversation's read or typing that arrives unadmitted (<see cref="Refuses"/>): a path that reached a
/// router some other way is stopped there and logged, rather than quietly telling the service.
/// </summary>
public sealed class PrivateReadingGate : IReadStateWriter, ITypingService
{
    private static readonly AsyncLocal<bool> admitted = new();
    private readonly IReadStateWriter reads;
    private readonly ITypingService typing;
    private readonly PrivateReading reading;

    public PrivateReadingGate(IReadStateWriter reads, ITypingService typing, PrivateReading reading)
    {
        this.reads = reads; this.typing = typing; this.reading = reading;
        Explicit = new ExplicitWriter(this);
    }

    /// <summary>The writer for a read the user asked for by name. It tells the service whatever private reading says.</summary>
    public IReadStateWriter Explicit { get; }

    /// <summary>Raised with a conversation id when its read marker was kept from its service.</summary>
    public event EventHandler<string>? Withheld;

    // Reading a conversation here is always possible when its service is not to be told: nothing is asked of it. Otherwise
    // the service's own rules decide (an account not connected, an Instagram message request), as before.
    public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default) =>
        reading.IsOn(chatId) ? Task.FromResult(true) : reads.CanMarkReadAsync(chatId, cancellationToken);

    public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        if (!reading.IsOn(chatId)) return AdmitReadAsync(chatId, messageId, false, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        reading.WithheldRead();
        Withheld?.Invoke(this, chatId);
        return Task.CompletedTask;
    }

    public bool CanReceive => typing.CanReceive;
    public bool CanSend => typing.CanSend;
    public IAsyncEnumerable<TypingUpdate> WatchAsync(CancellationToken cancellationToken = default) => typing.WatchAsync(cancellationToken);

    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default)
    {
        if (isTyping && reading.IsOn(chatId)) { reading.WithheldTyping(); return Task.CompletedTask; }
        return AdmitTypingAsync(chatId, isTyping, cancellationToken);
    }

    /// <summary>
    /// For the routers in front of the providers: whether a read (<paramref name="kind"/> "read") or typing start
    /// ("typing") for this conversation must not be sent, because private reading is on for its service and the call did
    /// not come through the gate. Counted and logged when it refuses.
    /// </summary>
    public static bool Refuses(PrivateReading? reading, string chatId, string kind)
    {
        if (reading is null || admitted.Value || !reading.IsOn(chatId)) return false;
        reading.Refused(kind, PrivateReading.Of(chatId));
        return true;
    }

    /// <summary>Whether the current call came through the gate (for the offline checks' stand-in routers).</summary>
    public static bool Admitted => admitted.Value;

    // Each is its own async method so the admission set here stays inside this call: an async method's changes to an
    // AsyncLocal are undone for its caller when it returns, and the inner call sees it from its first line.
    // A writer that takes its admission as an argument (the protocol's proxy: an AsyncLocal does not cross the wire, SWITCHBOARD.md 2.6d)
    // is told it here, and only here, so the one place that can say "admitted" is still this gate. The flag is also set, as it
    // always was, for whatever sits between the gate and the writer in this process.
    // `explicitly` is whether the person asked for this read by name. It is the one thing the gate says that a host keeping its own view of the choice
    // does not overrule: a host admits an automatic read only for a service it does not read privately itself (PrivateReadingHostReadState).
    private async Task AdmitReadAsync(string chatId, string messageId, bool explicitly, CancellationToken cancellationToken)
    {
        admitted.Value = true;
        if (reads is IAdmittedReadStateWriter admitting) await admitting.MarkReadAsync(chatId, messageId, true, explicitly, cancellationToken);
        else await reads.MarkReadAsync(chatId, messageId, cancellationToken);
    }

    private async Task AdmitTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken)
    {
        admitted.Value = true;
        if (typing is IAdmittedTypingService explicitly) await explicitly.SetTypingAsync(chatId, isTyping, true, cancellationToken);
        else await typing.SetTypingAsync(chatId, isTyping, cancellationToken);
    }

    /// <summary>
    /// For the host end of an explicit admission (<see cref="PrivateReadingHostReadState"/>): runs <paramref name="call"/> as an admitted call. The
    /// host does this only for a read its client said the person asked for by name, or a typing stop; see that class for what it trusts.
    /// </summary>
    internal static async Task RunAdmittedAsync(Func<Task> call)
    {
        admitted.Value = true;
        await call();
    }

    // What the user asked for by name. It reports what the service itself says it can do, since that is what it will reach.
    private sealed class ExplicitWriter(PrivateReadingGate owner) : IReadStateWriter
    {
        public Task<bool> CanMarkReadAsync(string chatId, CancellationToken cancellationToken = default) =>
            owner.reads.CanMarkReadAsync(chatId, cancellationToken);

        public Task MarkReadAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
        {
            if (owner.reading.IsOn(chatId)) owner.reading.SentExplicitly();
            return owner.AdmitReadAsync(chatId, messageId, true, cancellationToken);
        }
    }
}
