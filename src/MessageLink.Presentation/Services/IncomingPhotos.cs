using System.Text.RegularExpressions;
using System.Threading.Channels;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

// Photo bytes/decode may be fetched, but message lookup stays local: no thread opening, history paging or read marks.
// One worker leaves the other visible-image download slots free. Old reconnect history is not a prefetch backlog, except
// what a provider's catch-up names: the messages that came while the app was closed, in the few conversations it caught
// up (OfferCaughtUp). A shared reel or post is prepared as a photo is: its card's picture is the tallest thing in its
// conversation, and it was the one left to arrive after the conversation had opened (2026-10-02). Stickers and the still
// drawn for a video are pictures like any other; a GIF clip's bytes are kept ahead on disk (prepareClip), bounded by the
// caller, never decoded here and never a whole video.
//
// wantsCard decides, per provider, which messages are worth asking a card for (by default only a Facebook or Instagram
// share, which is what Messenger's encrypted conversations need); requestCard is where the provider's own rules about
// whose links may be fetched are applied. hasStill says whether a video's SourceUrl names a still its provider can draw.
public sealed class IncomingPhotos(IMessagePeek messages, Func<MessageAttachment, CancellationToken, Task> warm,
    TimeProvider? clock = null, int capacity = 64, Func<string, string, CancellationToken, Task>? requestCard = null,
    Func<ChatMessage, bool>? wantsCard = null, Func<MessageAttachment, bool>? hasStill = null,
    Func<MessageAttachment, CancellationToken, Task>? prepareClip = null)
{
    private sealed record Work(string Chat, string Message, DateTimeOffset At, bool CaughtUp);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(Math.Max(1, capacity))
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private static readonly TimeSpan FreshWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan[] PeekDelays = [TimeSpan.Zero, TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(1250)];
    // What a catch-up named stays eligible for a while whatever its age, so its own event, and the later one that brings
    // a link's card, still prepare it. Bounded, oldest out first.
    private static readonly TimeSpan CaughtUpHold = TimeSpan.FromMinutes(30);
    private const int CaughtUpLimit = 256, CardLimit = 256;
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> caughtUp = new(StringComparer.Ordinal);
    private readonly Queue<string> caughtOrder = new();
    // A card is asked for once per message; the provider answers with the message's own change when it has one.
    private readonly HashSet<string> cardsAsked = new(StringComparer.Ordinal);
    private readonly Queue<string> cardsOrder = new();

    public void Offer(AppliedBatch batch)
    {
        var now = time.GetUtcNow();
        foreach (var change in batch.Events)
        {
            if (change.Kind != RealtimeKind.MessageChanged || change.ReceiptOnly || change.ChatId is not { } chat || change.Ids is null) continue;
            var at = change.ReceivedAt ?? now;
            if (now - at > FreshWindow) continue;
            foreach (var id in change.Ids) queue.Writer.TryWrite(new(chat, id, at, IsCaughtUp(chat, id, now)));
        }
    }

    /// <summary>
    /// Messages a provider caught up on after connecting: what came while the app was closed. Prepared whatever their age
    /// and whoever sent them (the user's own, from another device, open on them too). <paramref name="announced"/> says
    /// their own events follow through <see cref="Offer"/>, so they are only remembered here rather than queued twice.
    /// </summary>
    public void OfferCaughtUp(string chat, IEnumerable<string> ids, bool announced = false)
    {
        var now = time.GetUtcNow();
        foreach (var id in ids)
        {
            var key = Key(chat, id);
            lock (gate)
            {
                if (!caughtUp.ContainsKey(key)) caughtOrder.Enqueue(key);
                caughtUp[key] = now + CaughtUpHold;
                while (caughtOrder.Count > CaughtUpLimit) caughtUp.Remove(caughtOrder.Dequeue());
            }
            if (!announced) queue.Writer.TryWrite(new(chat, id, now, true));
        }
    }

    private bool IsCaughtUp(string chat, string id, DateTimeOffset now)
    {
        lock (gate) return caughtUp.TryGetValue(Key(chat, id), out var until) && now < until;
    }

    private static string Key(string chat, string id) => chat + "\n" + id;

    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(token))
            {
                try
                {
                    if (time.GetUtcNow() - item.At > FreshWindow) continue;
                    ChatMessage? message = null;
                    foreach (var delay in PeekDelays)
                    {
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, time, token);
                        message = await messages.PeekAsync(item.Chat, item.Message, token);
                        if (message is not null) break;
                    }
                    if (message is null || message.ChatId != item.Chat || message.Id != item.Message || message.IsDeleted || message.IsHidden ||
                        message.Id.StartsWith("local:", StringComparison.Ordinal)) continue;
                    // Outgoing also means sent from the phone: its canonical arrival needs the same card and picture
                    // preparation as an incoming message. Only optimistic local rows lack a provider ID to ask for.
                    if (!item.CaughtUp && time.GetUtcNow() - message.Timestamp > FreshWindow) continue;
                    var prepared = false;
                    foreach (var photo in message.Attachments)
                    {
                        // A GIF clip: its still is drawn under it as a picture, and its bytes are kept on disk ahead.
                        var clip = IsClip(photo);
                        var picture = clip ? ClipStill(photo) : Eligible(photo, hasStill) ? photo : null;
                        if (picture is null && !(clip && prepareClip is not null)) continue;
                        // Only a photo or a card stands in for the card a share's words would otherwise be asked for, as
                        // before stickers, stills and clips were prepared too.
                        prepared |= PhotoOrCard(photo);
                        // A slow or unavailable provider must not hold the background queue forever. No download retry:
                        // another metadata arrival or the ordinary visible loader can try again later.
                        if (picture is { } drawn) await AttemptAsync(attempt => warm(drawn, attempt), token);
                        if (clip && prepareClip is { } keep) await AttemptAsync(attempt => keep(photo, attempt), token);
                    }
                    if (!prepared && requestCard is not null && (wantsCard ?? WantsCard)(message) && FirstAsk(Key(item.Chat, item.Message)))
                    {
                        try { await requestCard(item.Chat, item.Message, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception) { } // The conversation asks for its cards again when it is drawn.
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { } // A missing local record cannot break synchronization or the next photo.
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task AttemptAsync(Func<CancellationToken, Task> work, CancellationToken token)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        attempt.CancelAfter(TimeSpan.FromSeconds(15));
        try { await work(attempt.Token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { } // Background failure leaves the normal visible loading/retry path intact.
    }

    private bool FirstAsk(string key)
    {
        lock (gate)
        {
            if (!cardsAsked.Add(key)) return false;
            cardsOrder.Enqueue(key);
            while (cardsOrder.Count > CardLimit) cardsAsked.Remove(cardsOrder.Dequeue());
            return true;
        }
    }

    // A picture drawn as it is sent: a photo, a sticker, or the picture of a shared link's card (a reel's still among them);
    // or the still a video is drawn behind its play button, where its provider supplies one (hasStill). A GIF is not
    // (its clip is prepareClip's, its animated bytes are left for the conversation), and nothing the phone has not
    // downloaded yet is.
    public static bool Eligible(MessageAttachment photo, Func<MessageAttachment, bool>? hasStill = null) =>
        Identified(photo) && photo.Availability is null or "preview" && !photo.IsGif &&
        (PhotoPresentation.IsPhoto(photo) && (photo.Link is not null || !photo.Play) || IsVideo(photo) && hasStill?.Invoke(photo) == true);

    // What was prepared before stickers and stills were: a photo, or a card's picture.
    private static bool PhotoOrCard(MessageAttachment photo) => Eligible(photo) && !photo.IsSticker;

    private static bool Identified(MessageAttachment photo) => !string.IsNullOrEmpty(photo.Id) || !string.IsNullOrEmpty(photo.SourceUrl);
    private static bool IsVideo(MessageAttachment a) =>
        a.Type == "video" || a.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    // A GIF delivered as a short clip (Facebook's and Instagram's), which a conversation loops by itself over its still.
    public static bool IsClip(MessageAttachment a) => a.IsGif && IsVideo(a) && !string.IsNullOrEmpty(a.Id);

    // The still drawn under a GIF clip, exactly as the conversation draws it (AnimatedAttachment), so that what is prepared
    // here is the very picture the conversation asks for. Null when the clip has none.
    public static MessageAttachment? ClipStill(MessageAttachment clip) =>
        clip.SourceUrl is { Length: > 0 } still ? clip with { Id = still, Type = "img" } : null;

    // What a prepared picture is, for the log line: fixed words only.
    public static string KindOf(MessageAttachment picture) =>
        picture.Link is not null ? "card" : picture.IsSticker ? "sticker" : IsVideo(picture) ? "still" : "photo";

    // A Facebook or Instagram address in a message's words with no card under it yet: a share the provider draws a card
    // for itself once asked (an encrypted Messenger conversation carries only the words). Only those two services' own
    // addresses: preparing a card fetches the page it names, and any other address someone sends is theirs to follow.
    // A card of words alone (no picture: Messenger's shareCardWords, MessengerModels.Pictureless) still wants one: the
    // card made from the page takes its place, and left to the opening it drew its picture in late (2026-11-23).
    public static bool WantsCard(ChatMessage message) => !message.Attachments.Any(item => item.Link is not null && Identified(item)) &&
        LinksIn(message).Any(uri => SharedLinkPresentation.FacebookKind(uri) is not null || SharedLinkPresentation.InstagramKind(uri) is not null);

    // Any web address in a message's words with no card under it yet. For a provider whose cards the app asks for itself
    // (Google Messages), and only through that provider's own rules about whose links may be fetched.
    public static bool WantsAnyCard(ChatMessage message) => !HasCard(message) && LinksIn(message).Any();

    private static bool HasCard(ChatMessage message) => message.Attachments.Any(item => item.Link is not null);
    private static IEnumerable<Uri> LinksIn(ChatMessage message) =>
        Links.Matches(message.Text ?? "").Select(match => SharedLinkPresentation.Destination(match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')')))
            .OfType<Uri>();

    private static readonly Regex Links = new(@"https?://[^\s<>""'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
