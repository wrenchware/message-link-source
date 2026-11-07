using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Messenger;

// Typing from every network into the one indicator, and this account's typing out to whichever network owns the
// conversation.
internal sealed class TypingRouter(GoogleMessagesBackend google, MessengerBackend messenger, InstagramBackend instagram,
    NativeBeeper.Presentation.Services.ComposerActivity typing, NativeBeeper.Presentation.Services.PrivateReading? reading = null) : ITypingService, IComposerActivitySink
{
    internal ProviderAccounts? Accounts { get; set; }
    private GoogleMessagesBackend Google(string id) => Accounts?.Google(id) ?? google;
    private MessengerBackend Messenger(string id) => Accounts?.Messenger(id) ?? messenger;
    private NativeBeeper.App.Services.Instagram.InstagramBackend InstagramAccount(string id) => Accounts?.Instagram(id) ?? instagram;
    public bool CanReceive => Sources().Any(source => source.CanReceive);
    public bool CanSend => Sources().Any(source => source.CanSend);
    private IEnumerable<ITypingService> Sources() => Accounts is null ? new ITypingService[] { google, messenger, instagram } : Accounts.Googles.Cast<ITypingService>().Concat(Accounts.Messengers).Concat(Accounts.Instagrams);

    // Every actual composer edit yields Google history indexing, including edits inside the five-second window
    // where the remote typing notification is deliberately throttled. Both are noted: Google yields its background
    // indexing, and the shared one lets the synchronization apply stand out of the way for every provider, not just
    // that one. Messenger and Instagram put off telling the other end the typing stopped.
    public void NoteComposerActivity(string? chatId)
    {
        foreach (var account in Accounts?.Googles ?? new[] { google }) account.NoteComposerActivity(chatId);
        try
        {
            if (chatId is not null && InstagramIdentity.OwnsAny(chatId)) InstagramAccount(chatId).NoteComposerActivity(chatId);
            else if (chatId is not null && MessengerModels.Owns(chatId)) Messenger(chatId).NoteComposerActivity(chatId);
        }
        catch (NativeBeeper.Core.Backend.BackendException) { /* A removed account can remain open in a client until its next refresh. */ }
        typing.Note(chatId);
    }

    // As MessagingRouter.MarkReadAsync: a typing start for a private-reading service that did not come through
    // PrivateReadingGate is refused and logged. A stop is never refused; it only ends a start that was sent.
    // A start that goes on to a provider is noted (TypingPings) for the send line's since_typing_ms.
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default)
    {
        if (isTyping && NativeBeeper.Presentation.Services.PrivateReadingGate.Refuses(reading, chatId, "typing")) return Task.CompletedTask;
        var route = TypingRoute.Of(chatId);
        if (isTyping && route is TypingProvider.Instagram or TypingProvider.Messenger or TypingProvider.Google)
            NativeBeeper.Presentation.Services.TypingPings.Shared.Sent(chatId);
        return route switch
        {
            TypingProvider.Instagram => InstagramAccount(chatId).SetTypingAsync(chatId, isTyping, cancellationToken),
            TypingProvider.Messenger => Messenger(chatId).SetTypingAsync(chatId, isTyping, cancellationToken),
            TypingProvider.Google => Google(chatId).SetTypingAsync(chatId, isTyping, cancellationToken),
            _ => Task.CompletedTask,
        };
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
        if (Accounts is not null) Accounts.Changed += Changed;
        Changed();
        try
        {
            await foreach (var update in merged.Reader.ReadAllAsync(cancellationToken)) yield return update;
        }
        finally { if (Accounts is not null) Accounts.Changed -= Changed; stop.Cancel(); Task[] tasks; lock (pumps) tasks = pumps.Values.ToArray(); await Task.WhenAll(tasks); }
    }
}
