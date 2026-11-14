using NativeBeeper.Core.Messaging;

namespace NativeBeeper.App.Services.GoogleMessages;

// First is appended: this record is built positionally. It is how Google names a group's readers.
internal sealed record GooglePerson(string Id, string Name, bool Self, string NativeId, string? Phone = null, string? First = null);
internal sealed record GoogleChat(string Id, string Title, GooglePerson[] Participants, string Preview, DateTimeOffset At, bool Unread, bool Group, bool ReadOnly = true, bool PreviewOutgoing = false, string? Transport = null, string? OutgoingId = null, string? ExpectedTransport = null, bool Archived = false, bool Hidden = false, string? AvatarColor = null, string? PreviewSender = null,
    // In Google's Spam & blocked folder. Appended.
    bool Spam = false);
// FullSize: the attachment has its full-size media ID; false means only a thumbnail or inline preview so far.
internal sealed record GoogleMedia(string Id, string Name, string Mime, long Size, double Width, double Height, bool Thumbnail = false, bool FullSize = true,
    // Where a generated link preview leads, and whether it stands for something that plays. Appended: this record is
    // built positionally, and inserting a field here rebinds every one after it.
    string? Link = null, bool Play = false, string? Description = null, string? Caption = null);
internal sealed record GoogleReaction(string Key,string ParticipantId);
// ReadBy is appended: the names Google gives a group message's readers ("Read by Ann, Bob"), matched in Readers.
internal sealed record GoogleMessage(string Id, string ChatId, string Sender, string Text, DateTimeOffset At, string Status, string? TmpId = null, GoogleMedia[]? Media = null, GoogleReaction[]? Reactions = null, string? ReplyTo = null, string? Transport = null, string[]? ReadBy = null,
    // Google reports this message went end-to-end encrypted (the helper's encryption.go). Appended.
    bool Encrypted = false);
internal static class GoogleModels
{
    // Whose typing a message that just arrived ends. Nothing says the phone sends a stop when a message lands, and the
    // dots stayed up to the rest of their fifteen seconds under the message they announced. Only a message received:
    // not this account's own, not a conversation note (TOMBSTONE statuses) or a deletion. In a direct conversation it
    // ends everyone's there (person null): only the other person types in one, and the number typing names them by need
    // not be the participant row the message names (one person can be listed twice, a hidden RCS identity beside the
    // visible one). In a group, its sender's, by the number typing goes by, and nobody's when the sender is not listed.
    // Nothing older than a couple of minutes (a replay, or the phone's clock well behind) ends typing going on now.
    public static bool EndsTyping(GoogleMessage m, GoogleChat? chat, DateTimeOffset now, out string? person)
    {
        person = null;
        if (chat is null || m.At < now - TimeSpan.FromMinutes(2) || !m.Status.StartsWith("INCOMING_", StringComparison.Ordinal) ||
            m.Status == "INCOMING_DELETED") return false;
        var sender = chat.Participants.FirstOrDefault(p => p.NativeId == m.Sender);
        if (sender is { Self: true }) return false;
        if (!chat.Group) return true;
        if (sender is not { Id.Length: > 0 }) return false;
        person = sender.Id;
        return true;
    }
    // UI availability from synchronized state. The helper still checks fresh SIM,
    // conversation and message state before executing any mutation.
    public static NativeBeeper.Core.Services.MessageActionOptions Actions(GoogleChat chat, GoogleMessage message)
    {
        if (chat.Id != message.ChatId || chat.ReadOnly || chat.Transport is not ("SMS" or "RCS") || string.IsNullOrEmpty(chat.OutgoingId) ||
            message.Status.Contains("DELETED",StringComparison.Ordinal) || message.Status.Contains("DRAFT",StringComparison.Ordinal) ||
            message.Status.Contains("TOMBSTONE",StringComparison.Ordinal)) return new(false,[],[]);
        var self=chat.Participants.Where(p=>p.Self).Select(p=>p.NativeId).Append(chat.OutgoingId).ToHashSet();
        var remove=(message.Reactions ?? []).Where(r=>self.Contains(r.ParticipantId)).Select(r=>r.Key).Distinct().ToArray();
        var reply=chat.Transport=="RCS" && message.Status is "INCOMING_COMPLETE" or "OUTGOING_COMPLETE" or "OUTGOING_DELIVERED" or "OUTGOING_DISPLAYED";
        // Google Messages can take a message out of this account's own conversation, and that is all it can do: the
        // protocol has no scope on the request and nothing reaches the other person's phone. There is no edit at all.
        return new(reply,["👍","❤️","😂","😮","😢","👎"],remove,CanEdit:false,CanDeleteForMe:true,CanDeleteForEveryone:false);
    }
    // The transaction can be omitted from later history/status snapshots. Retain
    // an established exact-ID correlation; never infer identity from message text.
    public static GoogleMessage MergeUpdate(GoogleMessage previous, GoogleMessage update)
    {
        if (previous.Id != update.Id || previous.ChatId != update.ChatId) return update;
        var status = Regresses(previous.Status, update.Status) ? previous.Status : update.Status;
        return update with { Status = status, TmpId = string.IsNullOrEmpty(update.TmpId) ? previous.TmpId : update.TmpId,
            ReplyTo = string.IsNullOrEmpty(update.ReplyTo) ? previous.ReplyTo : update.ReplyTo,
            Transport = string.IsNullOrEmpty(update.Transport) ? previous.Transport : update.Transport,
            // Reading is not undone. A later update of a still-displayed message that names fewer readers, or
            // none, keeps those already named, as upstream's receipts do.
            ReadBy = status == "OUTGOING_DISPLAYED" ? Union(previous.ReadBy, update.ReadBy) : update.ReadBy };
    }
    // A sent message only moves forward: sending, sent, delivered, read. A copy further back along that line is older
    // than the one held, not news. Observed 2026-10-18: a send's status-page read (ResolvePendingAsync) was answered 20 ms
    // after the phone's update event for that message and its copy replaced the event's; the message said Sent while
    // Messages for Web said Delivered, and no further update came to correct it. Failures, retries, deletion and
    // anything else are not on the line and always apply.
    public static bool Regresses(string held, string arriving) => Progress(arriving) is > 0 and var later && Progress(held) > later;
    private static int Progress(string status) => status switch
    {
        "OUTGOING_YET_TO_SEND" or "OUTGOING_VALIDATING" or "OUTGOING_SEND_AFTER_PROCESSING" or "OUTGOING_SENDING" => 1,
        "OUTGOING_COMPLETE" => 2,
        "OUTGOING_DELIVERED" => 3,
        "OUTGOING_DISPLAYED" => 4,
        _ => 0
    };
    private static string[]? Union(string[]? before, string[]? after) =>
        before is not { Length: > 0 } ? after : after is not { Length: > 0 } ? before : [.. before.Union(after, StringComparer.Ordinal)];
    // What a send's receipt names our message by until its copy is found: the transaction id (GoogleMessagesBackend.Sending).
    public const string PendingPrefix = "google-direct:pending:";
    public static GoogleMessage? FindEcho(IEnumerable<GoogleMessage> messages, string nativeChat, string transaction)
        => messages.FirstOrDefault(m => m.ChatId == nativeChat && m.TmpId == transaction && m.Status.StartsWith("OUTGOING_", StringComparison.Ordinal));
    // Incoming media the phone has not finished downloading (upstream connector downloadPendingStatusMessage).
    public static string? MediaAvailability(string status) => status switch
    {
        "INCOMING_AUTO_DOWNLOADING" or "INCOMING_MANUAL_DOWNLOADING" or "INCOMING_RETRYING_AUTO_DOWNLOAD" or "INCOMING_RETRYING_MANUAL_DOWNLOAD" => "downloading",
        "INCOMING_YET_TO_MANUAL_DOWNLOAD" => "manual",
        "INCOMING_DOWNLOAD_FAILED" or "INCOMING_DOWNLOAD_FAILED_TOO_LARGE" or "INCOMING_DOWNLOAD_FAILED_SIM_HAS_NO_DATA" or "INCOMING_DOWNLOAD_CANCELED" => "failed",
        _ => null
    };
    public const string Account = "google-direct:paired";
    public static string Qualify(string id) => "google-direct:" + id;
    public static string Native(string id) => id.StartsWith("google-direct:", StringComparison.Ordinal) ? id[14..] : throw new ArgumentException("Not a direct Google identity.");
    public static bool Owns(string id) => id.StartsWith("google-direct:", StringComparison.Ordinal);
    public static ChatSummary Map(GoogleChat c)
    {
        // A group's line names who wrote it, as Google does beside its words: the phone's list put the name there only once the conversation's
        // messages were read, and the line changed each time the phone read them or let them go (owner, 2026-10-27).
        var sender = c.Group && !c.PreviewOutgoing && !string.IsNullOrWhiteSpace(c.PreviewSender) ? c.PreviewSender : null;
        // The writer as the conversation's own messages name them, and the way the conversation travels, so the phone draws the newest message from
        // the line as it draws it once read: a group's line stood for a coloured initial, and the owner's own line for a bubble in the "unknown" blue,
        // until the messages came and each changed (swap audit, 2026-10-27). Google gives the writer by name only: one person by that name, or none.
        var writer = sender is null ? null : PreviewWriter(c, sender);
        var preview = string.IsNullOrWhiteSpace(c.Preview) ? null : new ChatMessage(Qualify("preview:" + c.Id), Qualify(c.Id), writer?.Id ?? "", writer?.Name ?? sender, c.At, "", c.Preview, "text", c.PreviewOutgoing, false, false, null, null, [], [], new(null,null,null,null,new Dictionary<string,bool?>()),
            c.Transport is "RCS" or "SMS" ? c.Transport : null);
        // An RCS conversation has read receipts. Anything else is unknown rather than absent: SMS reports no reading, but
        // a conversation can move between the two and its messages say which they travelled on.
        return new(Qualify(c.Id), Account, c.Title, "Google Messages", c.Group ? "group" : "single", c.At, c.Unread && !c.PreviewOutgoing ? 1 : 0, false, false, c.Archived,
            c.ReadOnly, "google-avatar:"+c.Id, preview, [], null, null, new(0,0,0,0,c.Transport=="RCS" ? true : null,true), c.Participants.Select(p => new ChatParticipant(p.Id, p.Name,
                // Group members carry their own photo so the chat can show a composite; a direct chat already has one.
                c.Group && !p.Self && !string.IsNullOrEmpty(p.NativeId) ? "google-avatar:" + c.Id + "|" + p.NativeId : null, p.Self, p.Phone,
                // A saved contact as GoogleIncomingCards reads one; a short code or a named sender never counts as one.
                IsContact: !p.Self && GoogleIncomingCards.SavedContact(p) && GoogleIncomingCards.IsPersonNumber(p.Id))).ToArray(),c.Participants.Length,c.ExpectedTransport,c.AvatarColor,
            IsEncrypted: SendsAsText(c) ? false : null, IsSpam: c.Spam);
    }
    // The one participant (not this account) a line's writer name belongs to, by full or first name; none when the name is shared or unknown.
    private static GooglePerson? PreviewWriter(GoogleChat c, string name)
    {
        var named = c.Participants.Where(p => !p.Self && (string.Equals(p.Name, name, StringComparison.Ordinal) || string.Equals(p.First, name, StringComparison.Ordinal))).Take(2).ToArray();
        return named.Length == 1 ? named[0] : null;
    }
    // A conversation Google keeps as SMS, or one set to send by SMS or MMS, is not encrypting what it sends now, whatever
    // an older switch line says. That much is known, so it is said; anything else is left to the switch lines.
    public static bool SendsAsText(GoogleChat c) => c.Transport == "SMS" || c.ExpectedTransport is "SMS" or "SMS/MMS";
    public static ChatMessage Map(GoogleMessage m, GoogleChat c)
    {
        var person = c.Participants.FirstOrDefault(p => p.NativeId == m.Sender);
        // Phone-generated conversation events (SMS/RCS and encryption switches, group changes) are centered notes, as in
        // Google Messages for web, not messages from anyone. Without text there is nothing to show.
        var system = m.Status.Contains("TOMBSTONE", StringComparison.Ordinal);
        var outgoing = !system && (m.Status.StartsWith("OUTGOING_", StringComparison.Ordinal) || person?.Self == true);
        var deleted = m.Status is "OUTGOING_DELETED" or "INCOMING_DELETED";
        var hidden = m.Status is "OUTGOING_DRAFT" or "OUTGOING_SCHEDULED" || system && string.IsNullOrWhiteSpace(m.Text) || IsPixelPlaceholder(m);
        var sent = m.Status is "OUTGOING_COMPLETE" or "OUTGOING_DELIVERED" or "OUTGOING_DISPLAYED" ? "SUCCESS"
            : m.Status.StartsWith("OUTGOING_FAILED", StringComparison.Ordinal) ? "FAIL_PERMANENT"
            : outgoing ? "PENDING" : null;
        // A group's delivered is the phone's own word for it: Google Messages on the phone says Delivered for this status
        // in an RCS group (owner, 2026-10-28), where this said Sent. Google gives no count, and the status turned
        // Delivered at the first member's update (a second followed under a second later in a group of two others,
        // 2026-10-27), so it is counted as one, the most it is known to say. Upstream skips it in groups for Matrix's
        // sake, not Google's.
        var delivered = m.Status is "OUTGOING_DELIVERED" or "OUTGOING_DISPLAYED" ? (int?)1 : null;
        // A recording is named as one. Google sends an audio message with an audio media type like any other
        // attachment, and everything that was not a picture or a clip was being called a file — so it drew as a card
        // with a filename on it rather than as something that plays. The player the voicemails use takes any
        // attachment and is what Google Messages shows on a phone.
        var attachments=(m.Media ?? []).Select(a=>new MessageAttachment(a.Name,a.Mime.StartsWith("image/")?"img":a.Mime.StartsWith("video/")?"video":a.Mime.StartsWith("audio/")?"audio":"file",a.Mime,a.Size,false,a.Mime=="image/gif",false,"google-media:"+a.Id,a.Thumbnail ? "google-preview:"+a.Id : null,a.Width,a.Height,MediaAvailability(m.Status) ?? (!a.FullSize && a.Mime.StartsWith("image/") ? "preview" : null),
            // A generated link preview: the picture leads somewhere, and says whether what it leads to plays.
            Link: a.Link, Play: a.Play, Description: string.IsNullOrWhiteSpace(a.Description) ? null : a.Description, Caption: a.Caption)).ToArray();
        var reactions=(m.Reactions ?? []).Select(r=>new MessageReaction(r.Key,c.Participants.FirstOrDefault(p=>p.NativeId==r.ParticipantId)?.Id ?? r.ParticipantId)).Distinct().ToArray();
        // In a group, displayed says somebody has read it, not that everyone has: who has is the names below. In a direct
        // conversation it is the other person reading, which is all there is to say.
        return new(Qualify(m.Id), Qualify(m.ChatId), person?.Id ?? m.Sender, person?.Name, m.At,m.At.UtcTicks.ToString("D19"),m.Text,system ? "system" : "text",outgoing,deleted,hidden,null,string.IsNullOrEmpty(m.ReplyTo) ? null : Qualify(m.ReplyTo),attachments,reactions,new(sent,delivered,m.Status=="OUTGOING_DISPLAYED" ? true : null,null,Readers(m, c)),string.IsNullOrEmpty(m.Transport) ? null : m.Transport,NativeBeeper.Presentation.Services.ConversationEncryption.FromGoogleStatus(m.Status), IsEncrypted: m.Encrypted ? true : null,
            // The receipt's name for our own message, so a send still pending recognises its copy the moment it arrives.
            PendingId: outgoing && !string.IsNullOrEmpty(m.TmpId) ? PendingPrefix + m.TmpId : null);
    }

    // Observed 2026-10-01: a separate completed RCS record with this 112-byte, 1x1 PNG exists in the provider
    // history but is absent on both the phone and Messages for web. Grouping it with the next real photo enlarged
    // its one black pixel into a second photo. Its protocol meaning is unknown; hide only the observed shape,
    // retaining the raw record and attachment. Text, replies, reactions, previews and other media stay visible.
    private static bool IsPixelPlaceholder(GoogleMessage m) =>
        m.Status == "INCOMING_COMPLETE" && m.Transport == "RCS" && string.IsNullOrWhiteSpace(m.Text) &&
        string.IsNullOrEmpty(m.ReplyTo) && m.Reactions is not { Length: > 0 } &&
        m.Media is [{ Mime: "image/png", Size: 112, Width: 1, Height: 1, FullSize: true, Thumbnail: true,
            Link: null, Play: false, Caption: null }];

    /// <summary>
    /// A group message's readers, from the names Google gives them. A name counts only when it picks out one other
    /// participant, whether it is the name the conversation shows for them or their first name. A name that matches
    /// nobody, or more than one person either way, is dropped rather than guessed at.
    /// </summary>
    /// <remarks>
    /// Upstream takes the first participant it finds under a shared name; this does not, because a group says Read
    /// only once every member is named here, and naming the wrong person could claim it early. Upstream reads the names
    /// as first names, so a contact saved as just "Ann" does not take "Ann" from an "Ann Lee" in the same group: either
    /// could be meant. One person listed twice under one number is still one person.
    /// </remarks>
    public static IReadOnlyDictionary<string, bool?> Readers(GoogleMessage m, GoogleChat c)
    {
        var readers = new Dictionary<string, bool?>(StringComparer.Ordinal);
        if (!c.Group || !m.Status.StartsWith("OUTGOING_", StringComparison.Ordinal) || m.ReadBy is not { Length: > 0 } names) return readers;
        foreach (var raw in names)
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            if (Single(Match(c, person => person.Name == name || person.First == name)) is { } id) readers[id] = true;
        }
        return readers;
    }

    private static List<GooglePerson> Match(GoogleChat c, Func<GooglePerson, bool> named) =>
        [.. c.Participants.Where(person => !person.Self && named(person))];

    // One person, or nobody. Someone with no number cannot be told apart from anybody else, so they never count as one.
    private static string? Single(List<GooglePerson> matches) =>
        matches.Count > 0 && matches.All(person => person.Id.Length > 0) && matches.Select(person => person.Id).Distinct(StringComparer.Ordinal).Count() == 1
            ? matches[0].Id : null;
}
