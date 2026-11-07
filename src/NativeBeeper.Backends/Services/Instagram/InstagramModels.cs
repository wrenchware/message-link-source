using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.Instagram;

/// <summary>
/// The account a session is signed in as, read from the helper's login answer. Instagram is the one provider whose
/// account cannot be taken from a conversation — it does not reliably list the viewer among a thread's participants —
/// so the helper reports it from the viewer record it validated the session against, on a fresh login and a resume
/// alike.
/// </summary>
internal static class InstagramAccount
{
    /// Instagram's full name where the account has set one, and the handle where it has not: the same rule the app
    /// names everybody else by. An answer that names neither produces nothing, and Settings then shows no line at all
    /// rather than a placeholder.
    public static string? Named(JsonElement signedIn) =>
        signedIn.ValueKind != JsonValueKind.Object ? null
        : Text(signedIn, "accountName") is { } name ? name
        : Text(signedIn, "account") is { } handle ? "@" + handle
        : null;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && text.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}

internal sealed record InstagramPerson(string Id, string Name, bool Self = false, string? Avatar = null, string? Username = null);
// How far one other person has read: the time of the newest message they have read, by their messaging id.
internal sealed record InstagramRead(string Person, long At);
// Reads, Unnamed and Last are appended: this record is built positionally. Unnamed counts the people in the thread the
// helper could not give a messaging id, who therefore cannot be matched to a read position. Last is the newest message
// as the list carries it — without pictures, so complete only for text — and is never written to disk.
internal sealed record InstagramChat(string Id, string Title, InstagramPerson[] Participants, string? Preview,
    long At, bool Unread, bool Group, bool Muted, bool Pinned, string Folder, bool ReadOnly = false, string? Avatar = null,
    InstagramRead[]? Reads = null, int Unnamed = 0, InstagramMessage? Last = null,
    // This account's read watermark (Unix ms), zero when Instagram gave none.
    long ReadAt = 0);
internal sealed record InstagramReaction(string Key, string Sender);
// Audio, Duration (ms), Gif, Sticker and Mime are appended: a voice message, a GIF — a silent looping clip when Mime is
// video/mp4, an animated picture when it is image/webp — and a sticker. See the helper's mapVoice and mapAnimated.
internal sealed record InstagramMedia(string Id, string? Preview = null, bool Video = false, double Width = 0, double Height = 0, bool Play = false,
    bool Audio = false, long Duration = 0, bool Gif = false, bool Sticker = false, string? Mime = null);
internal sealed record InstagramMessage(string Id, string ChatId, string Sender, string? Name, string? Text,
    long At, bool Outgoing, string? ReplyTo = null, InstagramReaction[]? Reactions = null, long EditedAt = 0,
    string? Kind = null, string? Title = null, string? Link = null, string? Snippet = null,
    InstagramMedia[]? Media = null, bool ReactionLog = false);
// Reads come with the newest page only, which is read with its whole thread.
internal sealed record InstagramMessagePage(InstagramMessage[] Messages, bool HasMore, string? Cursor, InstagramRead[]? Reads = null);

/// <summary>
/// How Instagram's identifiers become the app's, and how a conversation becomes one the list can draw.
/// </summary>
/// <remarks>
/// The projection is the one stage 3 verified rather than a fresh invention. Three different Instagram values look
/// like a conversation id — <c>ThreadInfo.ID</c>, <c>ThreadKey</c> and the long <c>ThreadID</c> — and stage 3
/// confirmed they are distinct concepts that must not be collapsed into one guessed id, so only <c>ThreadInfo.ID</c>
/// names a conversation here. It is an opaque provider string, so it is encoded rather than concatenated: a colon
/// arriving inside one would otherwise silently repartition the identity.
///
/// Identities are account-scoped because the partition is, which is what lets a second Instagram account exist later
/// without every stored id meaning something different than it did.
/// </remarks>
internal sealed class InstagramIdentity(string accountKey, AccountNamespace? slot = null)
{
    public const string Network = "Instagram";
    private readonly string prefix = (slot ?? new()).Qualify("instagram:" + accountKey + ":");
    public string Media(string? id) => (slot ?? new()).Qualify(QualifyMedia(id));

    public string AccountKey { get; } = accountKey;
    public string Account => prefix + "account";

    public string Chat(string threadId) => prefix + "chat:" + Encode(threadId);
    public string Message(string messageId) => prefix + "msg:" + Encode(messageId);
    // Participants carry the messaging FBID, because that is the number a message's sender carries. The Instagram id
    // is a different number for the same person and stage 3 went looking for exactly that confusion.
    public string Person(string messagingId) => string.IsNullOrEmpty(messagingId) ? "" : prefix + "user:" + messagingId;

    public bool Owns(string id) => id.StartsWith(prefix, StringComparison.Ordinal);

    // Pictures are named by an id the helper issued, never by an Instagram address: the helper downloads them with
    // the session's own cookies and the app never reaches Instagram itself. Media ids are not account-scoped, because
    // the attachment ids Instagram gives are already unique and the cache keys them across sessions.
    private const string MediaPrefix = "instagram-media:";
    public static string QualifyMedia(string? id) => string.IsNullOrEmpty(id) ? "" : MediaPrefix + id;
    public static bool OwnsMedia(MessageAttachment attachment) =>
        attachment.Id?.StartsWith(MediaPrefix, StringComparison.Ordinal) == true ||
        attachment.SourceUrl?.StartsWith(MediaPrefix, StringComparison.Ordinal) == true;
    public static string NativeMedia(MessageAttachment attachment)
    {
        var id = attachment.Id?.StartsWith(MediaPrefix, StringComparison.Ordinal) == true ? attachment.Id
            : attachment.SourceUrl?.StartsWith(MediaPrefix, StringComparison.Ordinal) == true ? attachment.SourceUrl
            : throw new ArgumentException("Not Instagram media.", nameof(attachment));
        return id[MediaPrefix.Length..];
    }

    // Which backend a conversation belongs to, without knowing which account it belongs to. The router has to decide
    // that before any account is signed in, so it cannot ask an identity that does not exist yet.
    public static bool OwnsAny(string id) => id.StartsWith("instagram:", StringComparison.Ordinal);

    public string NativeChat(string id) => Decode(Native(id, "chat:"));
    public string NativeMessage(string id) => Decode(Native(id, "msg:"));

    private string Native(string id, string kind)
    {
        if (!Owns(id)) throw new ArgumentException("Not an Instagram identity.", nameof(id));
        var rest = id[prefix.Length..];
        if (!rest.StartsWith(kind, StringComparison.Ordinal)) throw new ArgumentException("Not an Instagram " + kind[..^1] + " identity.", nameof(id));
        return rest[kind.Length..];
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '=')));
    }

    // Instagram reports how far each person has read every conversation, so it has read receipts. Everything else it
    // can do is decided elsewhere, and unknown stays unknown.
    private static readonly ChatCapabilities Capabilities = ChatCapabilities.Unknown with { ReadReceipts = true };

    public ChatSummary Map(InstagramChat chat)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(chat.At);
        // Who wrote the newest message and which side it is on, from the newest message itself when the helper sends it: without them the phone drew
        // the owner's own last message as a received one, which moved across when the conversation's messages came (swap audit, 2026-10-27).
        var last = chat.Last is { Id.Length: > 0 } newest ? newest : null;
        var preview = string.IsNullOrWhiteSpace(chat.Preview) ? null
            : new ChatMessage(Chat(chat.Id) + ":preview", Chat(chat.Id), last is null ? "" : Person(last.Sender), last?.Name, at, "", chat.Preview, "text",
                last?.Outgoing ?? false, false, false, null, null, [], [], new(null, null, null, null, new Dictionary<string, bool?>()));
        return new(Chat(chat.Id), Account, chat.Title, Network, chat.Group ? "group" : "single", at,
            chat.Unread ? 1 : 0, chat.Muted, chat.Pinned, false, chat.ReadOnly,
            // An id the helper issued, not an Instagram address: the helper fetches the picture with the session's
            // own cookies and the app never reaches Instagram itself.
            Media(chat.Avatar), preview, [], null, null, Capabilities,
            chat.Participants.Select(person => new ChatParticipant(Person(person.Id), person.Name, Media(person.Avatar), person.Self,
                Username: person.Username)).ToArray(),
            // Somebody the helper could not name still counts, so a group is never called read before they have.
            chat.Participants.Length + Math.Max(0, chat.Unnamed),
            // Whether Instagram encrypts a conversation is not something this build knows, and unknown is never "no".
            IsEncrypted: null,
            IsSpam: chat.Folder == "spam",
            ReadUpTo: chat.ReadAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(chat.ReadAt) : null);
    }

    /// <summary>
    /// What to put in the bubble. Most Instagram messages are not text, and a shared reel arrives with an empty body,
    /// so taking the body alone left the conversation full of empty messages.
    /// </summary>
    /// <remarks>
    /// Shares have a native card even when Instagram supplies no preview. Everything else falls back to
    /// Instagram's own wording before it falls back to a noun. No creator or caption is inferred from a sender.
    /// </remarks>
    // A usable destination is enough for a native card; an absent thumbnail must not become a broken image request.
    private static bool Shared(InstagramMessage message) =>
        SharedLinkPresentation.Destination(message.Link) is not null;

    public static string? Describe(InstagramMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text)) return message.Text;
        // A share that became a card needs no words: the card already carries its title, its host and the link, and
        // the whole card follows that link even when the picture itself fails to load.
        if (Shared(message)) return null;
        // An unsupported destination is never handed to the system launcher.
        var share = string.Join('\n', new[] { message.Title, message.Link }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (share.Length > 0) return share;
        // A picture that draws needs no caption naming it. Everything below this line exists because there was
        // nothing to show, so saying "Photo" under a photograph would be describing what is already visible.
        if (message.Media is { Length: > 0 }) return null;
        if (!string.IsNullOrWhiteSpace(message.Snippet)) return message.Snippet;
        return message.Kind switch
        {
            "photo" => "Photo",
            "video" => "Video",
            "voice" => "Voice message",
            "gif" => "GIF",
            "sticker" => "Sticker",
            "music" => "Music",
            "media" => "Attachment",
            "share" => "Shared post",
            // A kind this build does not know is not described as though it were understood.
            _ => null,
        };
    }

    public ChatMessage Map(InstagramMessage message)
    {
        var text = Describe(message);
        // An admin notice (a call, a theme, a nickname) is a line across the conversation, as a call is in Google
        // Voice, rather than a bubble. One recording a reaction is left out, since the reaction shows on its message,
        // and so is one with no words at all, which would otherwise be drawn as the placeholder "[admin]".
        var notice = message.Kind == "admin";
        return new(Message(message.Id), Chat(message.ChatId), Person(message.Sender), message.Name,
            DateTimeOffset.FromUnixTimeMilliseconds(message.At),
            // Instagram orders by time and the app sorts by this key, so it has to be one that sorts as a string.
            DateTimeOffset.FromUnixTimeMilliseconds(message.At).UtcTicks.ToString("D19"),
            // The type is what the message is, not what this build wishes it were: when there is nothing to describe
            // it with, the app's own placeholder should read as the real kind rather than as "[text]".
            text, notice ? "system" : string.IsNullOrEmpty(message.Kind) ? "text" : message.Kind, message.Outgoing, false,
            notice && (message.ReactionLog || text is null),
            message.EditedAt == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(message.EditedAt),
            string.IsNullOrEmpty(message.ReplyTo) ? null : Message(message.ReplyTo),
            Attachments(message),
            (message.Reactions ?? []).Select(item => new MessageReaction(item.Key, Person(item.Sender))).ToArray(),
            // A message Instagram has a row for has been sent. Who has read it comes from the conversation's read
            // positions (WithReaders); Instagram says nothing about delivery, so nothing claims it.
            new(message.Outgoing ? "SUCCESS" : null, null, null, null, new Dictionary<string, bool?>()));
    }

    /// <summary>
    /// Who has read a message, from how far each other person has read the conversation. A position is the time of the
    /// newest message somebody has read, and it covers every message at or before it — the same reading upstream gives
    /// it. Returns the same message when the readers have not changed, so a caller can tell what moved.
    /// </summary>
    /// <remarks>
    /// A position is a time rather than a message, so no face is placed here: the conversation puts each reader's face
    /// on their newest loaded message that names them. Nobody reads their own message, and this account is never in
    /// the positions the helper reports. No read time is claimed either, because the position is not one.
    /// </remarks>
    public ChatMessage WithReaders(ChatMessage message, IReadOnlyDictionary<string, long>? reads)
    {
        Dictionary<string, bool?>? readers = null;
        if (reads is { Count: > 0 } && !message.IsDeleted)
        {
            var at = message.Timestamp.ToUnixTimeMilliseconds();
            foreach (var (person, position) in reads)
            {
                var id = Person(person);
                if (id.Length == 0 || id == message.SenderId || position < at) continue;
                (readers ??= new(StringComparer.Ordinal))[id] = true;
            }
        }
        var current = message.Delivery.SeenBy;
        if (current.Count == (readers?.Count ?? 0) && (readers is null || readers.Keys.All(id => current.TryGetValue(id, out var seen) && seen == true)))
            return message;
        return message with { Delivery = message.Delivery with { SeenBy = readers ?? new Dictionary<string, bool?>() } };
    }

    private MessageAttachment[] Attachments(InstagramMessage message)
    {
        if (Shared(message) && message.Media is not { Length: > 0 })
            return [new(message.Title ?? SharedLinkPresentation.InstagramKind(SharedLinkPresentation.Destination(message.Link)) ?? "Shared link",
                "img", null, null, false, false, false, Link: message.Link)];
        return (message.Media ?? []).Select(item => Attachment(message, item)).ToArray();
    }

    /// <summary>What a recording is called before it is played: the words the player shows under its waveform.</summary>
    public const string RecordingName = "Voice message";

    private MessageAttachment Attachment(InstagramMessage message, InstagramMedia item)
    {
        // A voice message plays in place, with the player the voicemails use. Nothing is downloaded until Play is
        // pressed (ProviderMedia: Instagram's recordings wait for it), and the length Instagram gives shows until then.
        // No media type: nothing Instagram sends names one, so the player goes by the bytes.
        if (item.Audio)
            return new(RecordingName, "audio", null, null, false, false, false, Media(item.Id),
                DurationSeconds: item.Duration > 0 ? item.Duration / 1000.0 : null);
        // A GIF sent as a clip loops silently over its still, as Messenger's do.
        if (item.Gif && item.Mime == "video/mp4")
            return new("GIF", "video", item.Mime, null, false, true, false, Media(item.Id),
                string.IsNullOrEmpty(item.Preview) ? null : Media(item.Preview),
                item.Width == 0 ? null : item.Width, item.Height == 0 ? null : item.Height);
        return new(
            // A share's title names its card; a photograph has no name to give.
            Shared(message) ? message.Title : null,
            item.Video ? "video" : "img",
            // A sticker or a GIF picture says what it is; a photograph's type is whatever arrives.
            item.Video ? null : item.Mime, null, false, item.Gif, item.Sticker,
            // SourceUrl carries the smaller copy, as the other providers do, so the conversation draws a thumbnail
            // inline and opening the picture asks for the full one.
            Media(item.Id), string.IsNullOrEmpty(item.Preview) ? null : Media(item.Preview),
            item.Width == 0 ? null : item.Width, item.Height == 0 ? null : item.Height,
            // A share becomes a card that opens the reel or post: its picture, its title and its host, following
            // one link. A photograph is just a photograph.
            Link: Shared(message) ? message.Link : null, Play: Plays(message, item));
    }

    // The helper marks every share's still as standing for something that plays, but only a reel is known to. A post
    // may be a photograph or a video and a profile is neither, so the badge on those promised a clip that was often not
    // there; unknown makes no claim. A video sent as a video is a clip and keeps its badge.
    private static bool Plays(InstagramMessage message, InstagramMedia item) =>
        item.Play && (item.Video || SharedLinkPresentation.InstagramKind(SharedLinkPresentation.Destination(message.Link)) == "Reel");

    // Message requests are read by the helper and deliberately not shown yet: a request is not an ordinary
    // conversation, and putting one in the inbox would say it is. Stage 7 decides how they appear, and because the
    // protocol side is already done that stage is a product decision rather than more protocol work.
    public static bool IsShown(InstagramChat chat) => chat.Folder is "inbox" or "spam";
}

/// <summary>
/// How far each other person has read each conversation: conversation → person → the time of the newest message they
/// have read, all by Instagram's own ids.
/// </summary>
/// <remarks>
/// Positions only move forward. A conversation somebody has read stays read whichever answer happens to arrive last,
/// the same rule the Messenger helper keeps its watermarks by. Not thread-safe; the backend holds its lock.
/// </remarks>
internal sealed class InstagramReadPositions
{
    private readonly Dictionary<string, Dictionary<string, long>> chats = new(StringComparer.Ordinal);

    /// <summary>Whether anybody's position in this conversation went further than it was.</summary>
    public bool Advance(string chat, InstagramRead[]? reads)
    {
        if (reads is not { Length: > 0 }) return false;
        if (!chats.TryGetValue(chat, out var positions)) chats[chat] = positions = new(StringComparer.Ordinal);
        var moved = false;
        foreach (var read in reads)
        {
            if (string.IsNullOrEmpty(read.Person) || read.At <= 0) continue;
            if (positions.TryGetValue(read.Person, out var known) && known >= read.At) continue;
            positions[read.Person] = read.At;
            moved = true;
        }
        return moved;
    }

    public IReadOnlyDictionary<string, long>? For(string chat) => chats.TryGetValue(chat, out var positions) ? positions : null;

    public void Clear() => chats.Clear();

    /// <summary>
    /// Brings the remembered messages of one conversation up to date with its positions, and names those whose readers
    /// changed. Only those are worth redrawing; the rest already say what they should.
    /// </summary>
    public static List<string> Redraw(Dictionary<string, ChatMessage> remembered, string chat, IReadOnlyDictionary<string, long>? positions,
        InstagramIdentity identity)
    {
        var changed = new List<string>();
        foreach (var message in remembered.Values.Where(message => message.ChatId == chat).ToArray())
        {
            var updated = identity.WithReaders(message, positions);
            if (ReferenceEquals(updated, message)) continue;
            remembered[message.Id] = updated;
            changed.Add(message.Id);
        }
        return changed;
    }
}

/// <summary>The newest message the poll has already considered in each conversation: its id and time.</summary>
internal readonly record struct InstagramNewest(string Id, long At);

/// <summary>
/// What arrived since the poll last looked, from the newest message each conversation in the list carries. No
/// conversation is read for it, so telling somebody a message came costs Instagram nothing.
/// </summary>
/// <remarks>
/// Owned by the poll alone. The list's own reads replace the conversations the poll compares against, so a message that
/// arrived between polls was absorbed by whichever read happened to land first, and the poll never saw it move.
/// </remarks>
internal static class InstagramArrivals
{
    /// <summary>
    /// Every conversation whose newest message is someone else's, is unread, is shown, and is newer than the one last
    /// considered there — or whose conversation was not known at all. The time as well as the id: a newest message
    /// taken back leaves an older one newest again, which is not an arrival.
    /// </summary>
    public static (InstagramMessage[] Arrived, Dictionary<string, InstagramNewest> Newest) Find(
        IReadOnlyDictionary<string, InstagramNewest> considered, IEnumerable<InstagramChat> fresh)
    {
        var newest = new Dictionary<string, InstagramNewest>(considered, StringComparer.Ordinal);
        var arrived = new List<InstagramMessage>();
        foreach (var chat in fresh)
        {
            if (chat.Last is not { Id.Length: > 0 } last) continue;
            var known = considered.TryGetValue(chat.Id, out var before);
            if (known && (before.Id == last.Id || before.At >= last.At)) continue;
            newest[chat.Id] = new(last.Id, last.At);
            if (!last.Outgoing && chat.Unread && InstagramIdentity.IsShown(chat)) arrived.Add(last with { ChatId = chat.Id });
        }
        return ([.. arrived], newest);
    }

    /// <summary>Where a new connection starts from: everything it can already see counts as considered.</summary>
    public static Dictionary<string, InstagramNewest> Seed(IEnumerable<InstagramChat> chats) =>
        Find(new Dictionary<string, InstagramNewest>(StringComparer.Ordinal), chats).Newest;

    /// <summary>
    /// Whether the list's copy of a message is the whole of it. The list carries no pictures and no share, so only text
    /// is: any other kind, put in front of a conversation, would draw a photograph as the word "Photo".
    /// </summary>
    public static bool Complete(InstagramMessage message) => message.Kind == "text";

    /// <summary>The list as it is kept on disk: without anybody's newest message, so the cache gains no message bodies.</summary>
    public static InstagramChat[] ForStorage(IEnumerable<InstagramChat> chats) => [.. chats.Select(chat => chat with { Last = null })];
}

/// <summary>
/// The list's copy of each arrival it could not give whole — a photograph, a video, a share — so that telling somebody
/// it came reads nothing of Instagram. The notifier and the unarchiver each ask for every arrival, and before this each
/// such question was a conversation read on the real account.
/// </summary>
/// <remarks>
/// Answered only for a conversation the app has never asked for messages, which nothing can be showing: whoever asks
/// then is saying the message came, and the list's words for it ("sent a photo") are all that takes. A conversation
/// that has been read may be open, and an open one would draw the copy as a photograph with no picture, so there the
/// conversation is read as for any message. Once read, the conversation's own copy answers first.
/// </remarks>
internal sealed class InstagramGlimpses
{
    private const int Limit = 200;
    private readonly object gate = new();
    private readonly Dictionary<string, ChatMessage> copies = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private readonly HashSet<string> opened = new(StringComparer.Ordinal);

    /// <summary>Keeps the list's copy of an arrival, under the message's own id. Bounded, oldest first out.</summary>
    public void Keep(ChatMessage message)
    {
        lock (gate)
        {
            if (!copies.ContainsKey(message.Id)) order.Enqueue(message.Id);
            copies[message.Id] = message;
            while (order.Count > Limit) copies.Remove(order.Dequeue());
        }
    }

    /// <summary>Something asked for this conversation's messages, so from now on it may be on screen.</summary>
    public void Opened(string chatId) { lock (gate) opened.Add(chatId); }

    /// <summary>
    /// The list's copy when it may answer, and which way the question goes: "list" when it did, "conversation" when the
    /// conversation has been read and must be again, null when the list never glimpsed this message.
    /// </summary>
    public (ChatMessage? Copy, string? From) Answer(string chatId, string messageId)
    {
        lock (gate)
        {
            if (!copies.TryGetValue(messageId, out var copy)) return (null, null);
            return opened.Contains(chatId) ? (null, "conversation") : (copy, "list");
        }
    }

    /// <summary>
    /// The kept copy whatever <see cref="Answer"/> would say, for a one-line mention of the arrival rather than a
    /// conversation view. Reorders nothing, evicts nothing and does not count as opening the conversation.
    /// </summary>
    public ChatMessage? Peek(string messageId)
    {
        lock (gate) return copies.GetValueOrDefault(messageId);
    }

    public void Clear()
    {
        lock (gate) { copies.Clear(); order.Clear(); opened.Clear(); }
    }
}

/// <summary>
/// Each conversation's newest page as this connection last read it, so an opening can draw what came after its saved
/// copy at once (ReadHeldAsync) rather than a second later, when Instagram answers the opening's own read (2026-10-02: a
/// reel that came while the app was closed slid in 1.0 s after its conversation opened, moving it 683 px).
/// </summary>
/// <remarks>
/// A page stands for its conversation only while the list shows nothing newer than it: the list carries a conversation's
/// newest activity, and anything later means the page no longer holds everything, so it answers nothing until the
/// conversation is read again. A page another helper read answers nothing either. Bounded, least recently read out first.
/// </remarks>
internal sealed class InstagramHeldPages
{
    private const int Limit = 64;
    private sealed record Page(object Connection, ChatMessage[] Messages, bool HasMore, long Newest, string? NewestId, long Read);
    private readonly object gate = new();
    private readonly Dictionary<string, Page> pages = new(StringComparer.Ordinal);
    private long reads;

    public void Record(object connection, string chatId, IReadOnlyList<ChatMessage> newest, bool hasMore)
    {
        var messages = newest.OrderBy(message => message.Timestamp).ThenBy(message => message.SortKey, StringComparer.Ordinal)
            .ThenBy(message => message.Id, StringComparer.Ordinal).ToArray();
        var last = messages.LastOrDefault();
        lock (gate)
        {
            pages[chatId] = new(connection, messages, hasMore, last?.Timestamp.ToUnixTimeMilliseconds() ?? 0, last?.Id, ++reads);
            if (pages.Count > Limit) pages.Remove(pages.MinBy(pair => pair.Value.Read).Key);
        }
    }

    /// <summary>
    /// Whether this connection holds the conversation's page as the list shows it now: listed no later than the page's
    /// newest message, or with that message as its newest (the list's time is the conversation's last activity).
    /// </summary>
    public bool Current(object connection, string chatId, long listedAt, string? listedNewestId) =>
        Held(connection, chatId, listedAt, listedNewestId) is not null;

    /// <summary>
    /// The page's messages after <paramref name="after"/>, oldest first, at most <paramref name="limit"/> of them; null
    /// when no current page is held. Complete only when the page reaches back to that moment, or holds the whole
    /// conversation, and every message since it fits.
    /// </summary>
    public NativeBeeper.Core.Services.HeldHistory? Answer(object connection, string chatId, long listedAt, string? listedNewestId,
        DateTimeOffset after, int limit)
    {
        if (Held(connection, chatId, listedAt, listedNewestId) is not { } page) return null;
        var newer = page.Messages.Where(message => message.Timestamp > after).ToArray();
        var reaches = !page.HasMore || page.Messages.Length > 0 && page.Messages[0].Timestamp <= after;
        var taken = newer.TakeLast(Math.Max(0, limit)).ToArray();
        return new(taken, reaches && taken.Length == newer.Length);
    }

    private Page? Held(object connection, string chatId, long listedAt, string? listedNewestId)
    {
        lock (gate)
        {
            if (!pages.TryGetValue(chatId, out var page) || !ReferenceEquals(page.Connection, connection) || page.NewestId is null) return null;
            return listedAt <= page.Newest || listedNewestId == page.NewestId ? page : null;
        }
    }

    public void Clear()
    {
        lock (gate) pages.Clear();
    }
}

/// <summary>
/// The conversations a new connection reads at once: those whose newest message came after the list the app last held
/// (saved at the end of the last run, or held when a helper stopped) — what came while nothing was listening. The unread
/// first, then the newest; the inbox only, since a conversation filed as spam is not one to spend a request on. None
/// without a list to compare against: a first sign-in has nothing to catch up on.
/// </summary>
internal static class InstagramCatchUp
{
    public static (string Chat, long Since)[] Candidates(IReadOnlyList<InstagramChat> previous, IReadOnlyList<InstagramChat> fresh, int limit)
    {
        if (previous.Count == 0 || limit <= 0) return [];
        var before = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var chat in previous) before[chat.Id] = Math.Max(before.GetValueOrDefault(chat.Id), chat.At);
        return [.. fresh.Where(chat => chat.Folder == "inbox")
            .Where(chat => chat.Last is { Id.Length: > 0 } last && (!before.TryGetValue(chat.Id, out var at) || last.At > at))
            .OrderByDescending(chat => chat.Unread).ThenByDescending(chat => chat.Last!.At)
            .Take(limit)
            .Select(chat => (chat.Id, before.GetValueOrDefault(chat.Id)))];
    }
}

/// <summary>
/// How many conversation reads the app makes of Instagram on its own in any hour: catching up after connecting, and
/// reading a photo, video or share that arrived so it is whole before anyone asks for it. Each is a request on the real
/// account, which nobody asked for by opening anything, so they share one ceiling whatever starts them.
/// </summary>
internal sealed class InstagramReadBudget(int perHour)
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);
    private readonly object gate = new();
    private readonly Queue<DateTimeOffset> taken = new();

    private bool stopped;

    /// <summary>Called after a read was taken, with the times still counting, so the count survives a restart (<see cref="InstagramGuardFile"/>).</summary>
    public Action<IReadOnlyList<DateTimeOffset>>? Taken { get; set; }

    public bool TryTake(DateTimeOffset now)
    {
        IReadOnlyList<DateTimeOffset> counting;
        lock (gate)
        {
            // A budget that could not be kept is spent: reads nobody asked for are the first thing to stop when the limit cannot be made to hold.
            if (stopped) return false;
            while (taken.Count > 0 && now - taken.Peek() >= Window) taken.Dequeue();
            if (taken.Count >= perHour) return false;
            taken.Enqueue(now);
            counting = [.. taken];
        }
        Taken?.Invoke(counting);
        return true;
    }

    /// <summary>The reads of the last hour that a previous run made. Times in the future are taken as now: a clock that was set back must not erase the count.</summary>
    public void Restore(IEnumerable<DateTimeOffset> times, DateTimeOffset now)
    {
        lock (gate)
        {
            // What was read is the budget from now on: a stop for a file that could not be written was this run's, and the next write says again whether it can be.
            stopped = false;
            taken.Clear();
            foreach (var time in times.Select(time => time > now ? now : time).Order().TakeLast(perHour)) taken.Enqueue(time);
        }
    }

    /// <summary>The whole hour's budget gone, as of <paramref name="now"/>: the state a budget that cannot be read starts from.</summary>
    public void Exhaust(DateTimeOffset now)
    {
        lock (gate)
        {
            taken.Clear();
            for (var i = 0; i < perHour; i++) taken.Enqueue(now);
        }
    }

    /// <summary>The reads that still count at <paramref name="now"/>, oldest first.</summary>
    public IReadOnlyList<DateTimeOffset> Counting(DateTimeOffset now)
    {
        lock (gate) return [.. taken.Where(time => now - time < Window)];
    }

    /// <summary>No more reads at all until the next start: the budget's file could not be written.</summary>
    public void Stop()
    {
        lock (gate) stopped = true;
    }
}

/// <summary>
/// How many requests the helper has made of Instagram, by kind, as its "snapshot" answer reports them. Counts only.
/// Cancelled is the reads the app stopped waiting for, answered without finishing: not requests Instagram saw.
/// </summary>
internal sealed record InstagramRequestCounts(ulong Index, ulong Mailbox, ulong MailboxPages, ulong Threads,
    ulong MessagePages, ulong Mutations, ulong Directory, ulong Media, ulong Failures, ulong Cancelled = 0)
{
    /// <summary>
    /// What was asked between an earlier answer and this one. A helper that started again counts from zero, so an
    /// answer smaller than the one before is a new process, and all of it is new.
    /// </summary>
    public InstagramRequestCounts Since(InstagramRequestCounts? earlier) =>
        earlier is null || Index < earlier.Index || Mailbox < earlier.Mailbox || MailboxPages < earlier.MailboxPages
            || Threads < earlier.Threads || MessagePages < earlier.MessagePages || Mutations < earlier.Mutations
            || Directory < earlier.Directory || Media < earlier.Media || Failures < earlier.Failures || Cancelled < earlier.Cancelled
            ? this
            : new(Index - earlier.Index, Mailbox - earlier.Mailbox, MailboxPages - earlier.MailboxPages, Threads - earlier.Threads,
                MessagePages - earlier.MessagePages, Mutations - earlier.Mutations, Directory - earlier.Directory,
                Media - earlier.Media, Failures - earlier.Failures, Cancelled - earlier.Cancelled);

    /// <summary>
    /// The Instagram log's line (InstagramLog): numbers and fixed words only. Beside the requests, what the poll did —
    /// how many polls, how many of them whole reads (sweeps) and how many made while nobody could be looking — and
    /// pagesPerPoll, the mailbox pages paged per poll, which was ten before the poll read the first page alone. Then the
    /// realtime deltas counted over the same stretch, against the arrivals the poll announced: whether the one could
    /// stand in for the other is what they are there to show.
    /// </summary>
    public string Describe(InstagramPollTally poll, InstagramDeltaCounts? deltas = null) =>
        $"instagram requests polls={poll.Polls} sweeps={poll.Sweeps} away={poll.Away} " +
        $"pagesPerPoll={(poll.Polls == 0 ? 0 : (double)MailboxPages / poll.Polls).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} " +
        $"index={Index} mailbox={Mailbox} pages={MailboxPages} threads={Threads} " +
        $"messages={MessagePages} mutations={Mutations} directory={Directory} media={Media} failures={Failures} cancelled={Cancelled}" +
        (deltas is null ? "" : $" deltas={deltas.Total} deltaMessages={deltas.Messages} deltaIncoming={deltas.Incoming} " +
            $"deltaReads={deltas.Reads} deltaReactions={deltas.Reactions} deltaEdits={deltas.Edits} deltaOther={deltas.Other}") +
        $" arrivals={poll.Arrivals} threadsReused={poll.ThreadsReused}";
}

/// <summary>
/// What arrived on the realtime connection, by kind, as the helper counted it under "realtime" in its "snapshot"
/// answer. Counted, never acted on: the poll still keeps the list, and these say whether the deltas match what it finds.
/// </summary>
internal sealed record InstagramDeltaCounts(ulong Total, ulong Messages, ulong Incoming, ulong Reads, ulong Reactions, ulong Edits, ulong Other)
{
    public InstagramDeltaCounts Since(InstagramDeltaCounts? earlier) =>
        earlier is null || Total < earlier.Total || Messages < earlier.Messages || Incoming < earlier.Incoming || Reads < earlier.Reads
            || Reactions < earlier.Reactions || Edits < earlier.Edits || Other < earlier.Other
            ? this
            : new(Total - earlier.Total, Messages - earlier.Messages, Incoming - earlier.Incoming, Reads - earlier.Reads,
                Reactions - earlier.Reactions, Edits - earlier.Edits, Other - earlier.Other);
}

/// <summary>
/// Typing as the helper counted it, under "typing" in its "snapshot" answer: reports shown and dropped, starts and stops
/// sent, failed and not attempted, how often the realtime connection typing arrives on was reopened, and how often
/// upstream reopened the typing stream by itself, which the helper's flap guard stops past thirty an hour. None of it is
/// a GraphQL request, which is the point of counting it apart. Ttl is the last time-to-live Instagram gave a report, a
/// bare number whose unit only live data will tell.
/// </summary>
internal sealed record InstagramTypingCounts(ulong In, ulong Out, ulong Unplaced, ulong Failed, ulong Skipped, ulong Reopened, long Ttl,
    ulong Reconnects = 0)
{
    public InstagramTypingCounts Since(InstagramTypingCounts? earlier) =>
        earlier is null || In < earlier.In || Out < earlier.Out || Unplaced < earlier.Unplaced || Failed < earlier.Failed
            || Skipped < earlier.Skipped || Reopened < earlier.Reopened || Reconnects < earlier.Reconnects
            ? this
            : new(In - earlier.In, Out - earlier.Out, Unplaced - earlier.Unplaced, Failed - earlier.Failed,
                Skipped - earlier.Skipped, Reopened - earlier.Reopened, Ttl, Reconnects - earlier.Reconnects);

    /// <summary>The Instagram log's line beside the request ledger's: numbers only.</summary>
    public string Describe() =>
        $"instagram typing in={In} out={Out} unplaced={Unplaced} failed={Failed} skipped={Skipped} reopened={Reopened} reconnects={Reconnects} ttl={Ttl}";
}

/// <summary>
/// One typing report from the helper, in the app's ids. The helper has already turned Instagram's thread and person ids
/// into the conversation and messaging ids the list uses, so here they are only qualified; anything missing or of the
/// wrong kind is not a report.
/// </summary>
internal sealed record InstagramTypingEvent(string ChatId, string PersonId, bool Active)
{
    public static InstagramTypingEvent? Read(JsonElement data, InstagramIdentity known) =>
        data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("chatId", out var chat) && chat.ValueKind == JsonValueKind.String && chat.GetString() is { Length: > 0 } chatId
            && data.TryGetProperty("sender", out var sender) && sender.ValueKind == JsonValueKind.String && sender.GetString() is { Length: > 0 } person
            && data.TryGetProperty("typing", out var typing) && typing.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? new(known.Chat(chatId), known.Person(person), typing.ValueKind == JsonValueKind.True)
            : null;
}

/// <summary>What the poll does when a read of the list fails.</summary>
internal static class InstagramPollFailure
{
    // Often enough that the phone and this app agree within a reasonable time, rarely enough that a small inbox is not
    // being asked for constantly.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Longest = TimeSpan.FromMinutes(5);

    /// <summary>The helper's fixed code, or one fixed word for a failure that carried none.</summary>
    public static string Code(Exception failure) =>
        failure is InvalidOperationException && NativeBeeper.GoogleMessages.Spike.HelperErrorCodes.IsKnown(failure.Message)
            ? failure.Message : "helper_failed";

    /// <summary>
    /// Instagram has flagged the session, or it has ended. Asking again cannot change that answer and only keeps asking
    /// a flagged account — every twenty seconds, indefinitely, before this existed. Signing in again is the user's.
    /// </summary>
    public static bool StopsPolling(string code) =>
        code is "session_expired" or "checkpoint_required" or "challenge_required" or "consent_required" or "account_suspended";

    /// <summary>How long before the next read after this many failures in a row: doubling from the usual interval.</summary>
    public static TimeSpan Delay(int consecutive) =>
        consecutive <= 0 ? Interval : TimeSpan.FromTicks(Math.Min(Longest.Ticks, Interval.Ticks << Math.Min(consecutive, 5)));
}

/// <summary>What marking an Instagram conversation read may do, and with which time.</summary>
internal static class InstagramMarkRead
{
    /// <summary>
    /// Only the inbox. Instagram's spam folder is its hidden message requests, whose senders its own apps do not tell
    /// they were seen until one is accepted. A conversation not in the list is left to the helper, which knows its folder.
    /// </summary>
    public static bool Allowed(InstagramChat? chat) => chat is null || chat.Folder == "inbox";

    /// <summary>
    /// The read message's own time, as upstream sends it, and the conversation's latest activity only where the message
    /// is not remembered. The two differ whenever something other than that message moved the conversation.
    /// </summary>
    public static (long At, string Source) Time(ChatMessage? message, InstagramChat? chat) =>
        message is not null && message.Timestamp.ToUnixTimeMilliseconds() is > 0 and var at ? (at, "message")
        : (chat?.At ?? 0, "list");
}

/// <summary>
/// The conversations whose last read Instagram did not take, and when. The next one for the same conversation waits
/// <see cref="RetryAfterFailure"/> (owner, 2026-10-22): a read that failed is tried again only on the next look at the
/// conversation, and a refusing or throttled Instagram is never asked faster than this.
/// </summary>
internal sealed class InstagramReadFailures
{
    public static TimeSpan RetryAfterFailure { get; set; } = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, DateTimeOffset> failedAt = new(StringComparer.Ordinal);

    public bool HoldsBack(string chatId, DateTimeOffset now)
    {
        lock (failedAt) return failedAt.TryGetValue(chatId, out var at) && now - at < RetryAfterFailure;
    }

    public void Failed(string chatId, DateTimeOffset now)
    {
        lock (failedAt)
        {
            if (failedAt.Count > 500) failedAt.Clear();
            failedAt[chatId] = now;
        }
    }

    public void Succeeded(string chatId)
    {
        lock (failedAt) failedAt.Remove(chatId);
    }
}
