using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed partial class ConversationViewModel
{
    private long previewGeneration = -1;
    private readonly Dictionary<string, (string? Text, string Links)> previewInputs = [];
    private CancellationTokenSource? previewRequests;
    private long previewRequestRevision;
    // Held links the user asked for with Load preview (LoadLinkPreview), by MessageIdentity.Key: never held again this
    // session. Few, since each is a click; cleared rather than trimmed if it ever grows.
    private readonly HashSet<string> previewsAsked = [];
    private bool linkPreviewsEnabled = true;
    public bool LinkPreviewsEnabled
    {
        get => linkPreviewsEnabled;
        set
        {
            if (linkPreviewsEnabled == value) return;
            linkPreviewsEnabled = value;
            previewGeneration = -1; previewInputs.Clear();
            previewRequestRevision++;
            previewRequests?.Cancel(); previewRequests?.Dispose(); previewRequests = null;
            if (value && current is not null)
                LoadVisibleLinkPreviews(WithOutgoing(current.Messages).Where(message => !message.IsHidden).ToArray());
            else ShowHeldLinkPreviews();
        }
    }

    /// <summary>
    /// Load preview on a link held back as the conversation opened (see <see cref="HoldsLinkPreview"/>): fetches that one
    /// message's page through the same request as any other, and does not hold it back again this session.
    /// </summary>
    public void LoadLinkPreview(ChatMessage message)
    {
        var row = Messages.FirstOrDefault(item => MessageIdentity.Key(item.Message) == MessageIdentity.Key(message));
        if (row is not { PreviewHeld: true }) return;
        if (previewsAsked.Count >= 512) previewsAsked.Clear();
        previewsAsked.Add(MessageIdentity.Key(message));
        LoadVisibleLinkPreviews([.. Messages.Select(item => item.Message)]);
    }

    /// <summary>Weighs the conversation's links again after private reading changed for a service.</summary>
    public void ReconsiderLinkPreviews()
    {
        if (current is not null) LoadVisibleLinkPreviews([.. Messages.Select(item => item.Message)]);
    }

    private void LoadVisibleLinkPreviews(IReadOnlyList<ChatMessage> visible)
    {
        RequestLinkPreviews(visible);
        ShowHeldLinkPreviews();
    }

    private void RequestLinkPreviews(IReadOnlyList<ChatMessage> visible)
    {
        // Saved history can render before the helper knows these messages. Let the authoritative page request the
        // previews, even when its text is identical to that first saved render.
        if (!LinkPreviewsEnabled || IsShowingSavedHistory || reader is not ILinkPreviewLoader loader || selection is null || selection.IsCancellationRequested || ActiveChatId is not { } chatId)
            return;
        if (previewGeneration != generation)
        {
            previewGeneration = generation;
            previewInputs.Clear();
            previewRequests?.Cancel(); previewRequests?.Dispose();
            previewRequests = CancellationTokenSource.CreateLinkedTokenSource(selection.Token);
        }
        var written = WrittenIn(visible);
        var changed = new List<ChatMessage>();
        var retained = new HashSet<string>();
        foreach (var message in visible)
        {
            // Optimistic outbox rows have no provider ID, so a backend cannot enrich them. Recording one as submitted
            // can make a newly sent link look completed before its canonical row arrives.
            if (message.IsDeleted || message.IsHidden || message.Id.StartsWith("local:", StringComparison.Ordinal) || IsSpam(message.ChatId)) continue;
            var key = MessageIdentity.Key(message);
            var textHasLink = message.Text?.Contains("http", StringComparison.OrdinalIgnoreCase) == true;
            var input = (message.Text, textHasLink ? "" : string.Join('\n', message.Attachments.Select(item => item.Link).Where(link => link is not null)));
            // Left unrecorded, like spam: a held link is weighed again on every render, so it is asked for once its sender
            // is known (the user writes there) or private reading goes off, and not before.
            if ((textHasLink || input.Item2.Length > 0) && HoldsLinkPreview(message, written)) continue;
            retained.Add(key);
            if (previewInputs.TryGetValue(key, out var prior) && prior == input) continue;
            previewInputs[key] = input;
            if (textHasLink || input.Item2.Length > 0)
                changed.Add(message);
        }
        foreach (var id in previewInputs.Keys.Where(id => !retained.Contains(id)).ToArray()) previewInputs.Remove(id);
        if (changed.Count == 0) return;
        // Rendering is already complete. The helper queues the work and reports arriving previews through normal
        // message-change events. Receipts/reactions do not requeue links; edits and newly loaded older pages do.
        foreach (var group in changed.TakeLast(200).GroupBy(m => m.ChatId))
        {
            var requested = group.ToDictionary(m => m.Id, m => previewInputs[MessageIdentity.Key(m)]);
            _ = LoadVisibleLinkPreviewsAsync(loader, group.Key, requested, generation, previewRequestRevision, previewRequests!.Token);
        }
    }

    // Each drawn row offers Load preview while its link is held and has no card yet. Weighed from the rows themselves, so
    // a saved render shows the same offer the live page will, and a render that asks nothing (saved history, previews off)
    // still takes it away where it no longer applies.
    private void ShowHeldLinkPreviews()
    {
        var offering = LinkPreviewsEnabled && reader is ILinkPreviewLoader && ActiveChatId is not null;
        var written = offering ? WrittenIn([.. Messages.Select(row => row.Message)]) : null;
        foreach (var row in Messages)
        {
            var message = row.Message;
            // The cheap tests first: this runs for every row on every render.
            row.SetPreviewHeld(written is not null && !message.IsOutgoing && !message.IsDeleted && !message.IsHidden && message.Type != "system" &&
                message.Text?.Contains("http", StringComparison.OrdinalIgnoreCase) == true && !message.Id.StartsWith("local:", StringComparison.Ordinal) &&
                FetchesLinkPages(message.ChatId) && !IsSpam(message.ChatId) && HoldsLinkPreview(message, written) && IncomingPhotos.WantsAnyCard(message));
        }
    }

    // Whose links have their page fetched by themselves (2026-10-14). The fetch tells the link's site, and so
    // whoever sent it, that the message was read, when, and from which address, so it is made unasked only where that
    // tells nobody anything new:
    // - the user's own messages, always;
    // - nothing while private reading is on for the conversation's service, which is the promise that reading there tells
    //   nobody;
    // - otherwise only a known sender's. Known is the same on every service: someone the service says is a saved contact
    //   (ChatParticipant.IsContact, which only Google Messages says, never for a short code), or anyone in a conversation
    //   this account has written in (an outgoing message among those loaded). Everyone else is a stranger. The two
    //   signals are the ones GoogleIncomingCards uses for links as they arrive.
    // A held link without its card offers Load preview instead (LoadLinkPreview).
    private bool HoldsLinkPreview(ChatMessage message, IReadOnlySet<string> written) =>
        !message.IsOutgoing && !previewsAsked.Contains(MessageIdentity.Key(message)) &&
        (readHere?.Wants(message.ChatId) == true || !written.Contains(message.ChatId) && !SenderIsContact(message));

    // The conversations among these, and the history loaded behind them, where this account has written: a message it
    // sent or tried to, not one still waiting on this PC (an outbox row, a scheduled send) nor a hidden draft.
    private HashSet<string> WrittenIn(IReadOnlyList<ChatMessage> visible)
    {
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in visible.Concat(current?.Messages ?? []))
            if (message.IsOutgoing && !message.IsHidden && !message.Id.StartsWith("local:", StringComparison.Ordinal) && !ScheduledSendRows.Is(message))
                written.Add(message.ChatId);
        return written;
    }

    // The sender as the conversation's own summary lists them: the open conversation's, or a merged member's as the list has it.
    private bool SenderIsContact(ChatMessage message)
    {
        foreach (var chat in new[] { current?.Chat, merges?.Source(message.ChatId), selectedChat })
            if (chat is not null && chat.Id == message.ChatId && chat.Participants.FirstOrDefault(person => person.Id == message.SenderId) is { } sender)
                return sender is { IsSelf: not true, IsContact: true };
        return false;
    }

    // Where a card is made by fetching the linked page, which is where Load preview does anything: Google Messages and
    // Messenger. Instagram, Google Voice and Hermes make none (MessagingRouter.LoadLinkPreviewsAsync answers them with nothing).
    private static bool FetchesLinkPages(string chatId) =>
        !HermesThreads.Is(chatId) && PrivateReading.Of(chatId) is ReadService.GoogleMessages or ReadService.Messenger;

    // A conversation the list files as spam fetches no page for its links as it opens: the fetch tells the sender's site
    // the message was read, and from where. The incoming preload refuses spam for the same reason (GoogleIncomingCards).
    // Spam by the list's summary as opened, the conversation's own, or (for a merged member) the list's row of it. Its
    // messages are left unrecorded, so once it is out of spam and opened again the previews come as for any other.
    private bool IsSpam(string chatId) =>
        selectedChat is { IsSpam: true } || current?.Chat is { IsSpam: true } || merges?.Source(chatId) is { IsSpam: true };

    private async Task LoadVisibleLinkPreviewsAsync(ILinkPreviewLoader loader, string chatId,
        IReadOnlyDictionary<string, (string? Text, string Links)> requested, long version, long requestRevision, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await loader.LoadLinkPreviewsAsync(chatId, requested.Keys.ToArray(), token);
        }
        catch (Exception)
        {
            // A disconnected helper may become available on the next history/realtime render. Do not mark failed
            // submissions as completed, or let an older request erase an edited message's newer submission.
            if (version != generation || requestRevision != previewRequestRevision) return;
            foreach (var (id, input) in requested)
                if (previewInputs.TryGetValue(MessageIdentity.Key(chatId, id), out var currentInput) && currentInput == input) previewInputs.Remove(MessageIdentity.Key(chatId, id));
        }
    }
}
