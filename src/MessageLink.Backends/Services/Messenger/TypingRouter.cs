using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.Messenger;

// Typing from every network into the one indicator, and this account's typing out to whichever network owns the
// conversation (ConversationOwners). An id nobody owns goes nowhere: an Instagram conversation used to fall through to
// Google Messages, whose id parsing threw on every typing burst.
internal sealed class TypingRouter(ConversationOwners owners,
    MessageLink.Presentation.Services.ComposerActivity typing, MessageLink.Presentation.Services.PrivateReading? reading = null) : ITypingService, IComposerActivitySink
{
    internal ProviderAccounts? Accounts { get => owners.Accounts; set => owners.Accounts = value; }
    public bool CanReceive => Sources().Any(source => source.CanReceive);
    public bool CanSend => Sources().Any(source => source.CanSend);
    private IEnumerable<ITypingService> Sources() => owners.Services.SelectMany(service => service.TypingSources);

    // Every actual composer edit yields Google history indexing, including edits inside the five-second window
    // where the remote typing notification is deliberately throttled. Both are noted: Google yields its background
    // indexing, and the shared one lets the synchronization apply stand out of the way for every provider, not just
    // that one. Messenger and Instagram put off telling the other end the typing stopped.
    public void NoteComposerActivity(string? chatId)
    {
        foreach (var service in owners.Services) service.NoteComposerActivity(chatId);
        typing.Note(chatId);
    }

    // As MessagingRouter.MarkReadAsync: a typing start for a private-reading service that did not come through
    // PrivateReadingGate is refused and logged. A stop is never refused; it only ends a start that was sent.
    // A start that goes on to a provider is noted (TypingPings) for the send line's since_typing_ms.
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default)
    {
        if (isTyping && MessageLink.Presentation.Services.PrivateReadingGate.Refuses(reading, chatId, "typing")) return Task.CompletedTask;
        var owner = owners.Of(chatId);
        if (!owner.SendsTyping) return Task.CompletedTask;
        if (isTyping) MessageLink.Presentation.Services.TypingPings.Shared.Sent(chatId);
        return owner.SetTypingAsync(chatId, isTyping, cancellationToken);
    }

    public async IAsyncEnumerable<TypingUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var merged = Channel.CreateUnbounded<TypingUpdate>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task Pump(ITypingService source)
        {
            try { await foreach (var update in source.WatchAsync(stop.Token)) await merged.Writer.WriteAsync(update, stop.Token); }
            catch (OperationCanceledException) { }
        }
        var pumps = new Dictionary<ITypingService, Task>();
        void Changed() { lock (pumps) foreach (var source in Sources()) if (!pumps.ContainsKey(source)) pumps.Add(source, Pump(source)); }
        var accounts = Accounts;
        if (accounts is not null) accounts.Changed += Changed;
        Changed();
        try
        {
            await foreach (var update in merged.Reader.ReadAllAsync(cancellationToken)) yield return update;
        }
        finally { if (accounts is not null) accounts.Changed -= Changed; stop.Cancel(); Task[] tasks; lock (pumps) tasks = pumps.Values.ToArray(); await Task.WhenAll(tasks); }
    }
}
