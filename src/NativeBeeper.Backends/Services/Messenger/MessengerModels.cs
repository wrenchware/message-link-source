using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;


namespace NativeBeeper.App.Services.Messenger;

internal sealed record MessengerPerson(string Id, string Name, bool Self, string NativeId, string? AvatarUrl = null);
internal sealed record MessengerChat(string Id, string Title, MessengerPerson[] Participants, string Preview, DateTimeOffset At,
    bool Unread, bool Group, bool ReadOnly = false, bool Archived = false, string? Transport = null, string? OutgoingId = null,
    string? AvatarUrl = null, bool Encrypted = false, bool PreviewOutgoing = false,
    // Facebook's own count of a group's members, which the participants can fall short of while their rows arrive.
    int Members = 0,
    // This account's read watermark (Unix ms), zero when Messenger has not said.
    long ReadAt = 0,
    // How this account muted the conversation in Messenger, perhaps on the phone: -1 until turned off, otherwise when
    // it ends (Unix ms); zero for none. Received only. Appended (2026-10-04).
    long MutedUntil = 0,
    // The group changes the helper says Messenger takes from this account here: an ordinary group it is in, with
    // removing for its admins. Appended (2026-10-04).
    bool CanRename = false, bool CanAddMembers = false, bool CanRemoveMembers = false,
    // Who wrote the preview's words in a group, as its messages name them; none for this account and one-to-ones. Appended (2026-10-28).
    string? PreviewSender = null, string? PreviewName = null,
    // A Marketplace buyer and seller's chat, by Facebook's own thread kind. Appended (2026-11-12).
    bool Marketplace = false);
internal sealed record MessengerMedia(string Id, string? Name, string? Mime, long Size, double Width, double Height, bool Video,
    string? Preview = null, bool Animated = false, string? Link = null, bool Play = false, string? Description = null, string? Caption = null,
    string? ActionTitle = null);
internal sealed record MessengerMessage(string Id, string ChatId, string Sender, string? Name, string Text, DateTimeOffset At,
    bool Outgoing, bool Deleted, string? ReplyTo = null, long Status = 0, MessengerMedia[]? Media = null,
    MessengerReaction[]? Reactions = null,
    // Contact ids of everyone who has read this far, and of those for whom this is the newest message they have read,
    // and when the words were last changed. Appended, never inserted: these records are built positionally.
    string[]? SeenBy = null, string[]? ReadMark = null, long EditedAt = 0,
    // How many of the other people a message this account sent has reached, reading counted as reaching; and when it
    // was read, in milliseconds, only where Facebook said when and the helper saw that reading happen. Appended.
    int Delivered = 0, long SeenAt = 0,
    // A line Facebook writes into the conversation rather than a person ("Jay started this chat."), and a row Facebook's
    // own phone app does not show (Marketplace's "Message sent" card). Appended.
    bool Notice = false, bool Hidden = false);
internal sealed record MessengerMessagePage(MessengerMessage[] Messages, bool Refreshing);
internal sealed record MessengerMessageWindow(MessengerMessage[] Messages, bool MoreOlder, bool MoreNewer);
internal sealed record MessengerReaction(string Key, string Sender);

internal static class MessengerModels
{
    // The pinned connector disables captions for every encrypted media kind: Messenger Web hides them even
    // when the send is accepted. Unknown threads must use the separate-text path until their kind is known.
    public static bool SupportsAttachmentCaption(MessengerChat? chat) => chat is { Encrypted: false, ReadOnly: false };

    public const string Account = "messenger:me";
    private const string Prefix = "messenger:";
    public static bool Owns(string id) => id.StartsWith(Prefix, StringComparison.Ordinal);
    public static string Qualify(string id) => Prefix + id;
    public static string Native(string id) => Owns(id) ? id[Prefix.Length..] : throw new ArgumentException("Not a Messenger identity.");

    // A Facebook account id is all digits, which the conversation header reads as a phone number and prints under the
    // title. Marking them as what they are keeps them out of anywhere a number belongs; senders and participants use
    // the same form, so a message still matches the person who sent it.
    public static string Person(string id) => string.IsNullOrEmpty(id) ? "" : "fb:" + id;

    // Photos, files and profile pictures are all fetched through the helper, which holds the Facebook urls.
    private const string MediaPrefix = "messenger-media:";
    public static string QualifyMedia(string id) => string.IsNullOrEmpty(id) ? "" : MediaPrefix + id;
    public static bool OwnsMedia(MessageAttachment attachment) =>
        attachment.Id?.StartsWith(MediaPrefix, StringComparison.Ordinal) == true ||
        attachment.SourceUrl?.StartsWith(MediaPrefix, StringComparison.Ordinal) == true;
    public static string NativeMedia(MessageAttachment attachment)
    {
        var id = attachment.Id?.StartsWith(MediaPrefix, StringComparison.Ordinal) == true ? attachment.Id
            : attachment.SourceUrl?.StartsWith(MediaPrefix, StringComparison.Ordinal) == true ? attachment.SourceUrl
            : throw new ArgumentException("Not Messenger media.");
        return id[MediaPrefix.Length..];
    }

    // Facebook reports how far each person has read every conversation, encrypted or not, so Messenger has read
    // receipts. Everything else it can do is decided elsewhere, and unknown stays unknown.
    private static readonly ChatCapabilities Capabilities = ChatCapabilities.Unknown with { ReadReceipts = true };

    public static ChatSummary Map(MessengerChat chat)
    {
        // A group's line names who wrote it, as its messages do: the phone's line gained the name when the conversation was read and lost it when the
        // phone let it go (its diary, 2026-10-28), as a Google group's did before GoogleModels named the writer.
        var writer = chat.Group && !chat.PreviewOutgoing && !string.IsNullOrEmpty(chat.PreviewSender) ? chat.PreviewSender : null;
        var preview = string.IsNullOrWhiteSpace(chat.Preview) ? null
            : new ChatMessage(Qualify("preview:" + chat.Id), Qualify(chat.Id), writer is null ? "" : Person(writer), writer is null ? null : chat.PreviewName, chat.At, "", chat.Preview, "text",
                chat.PreviewOutgoing, false, false, null, null, [], [], new(null, null, null, null, new Dictionary<string, bool?>()));
        return new(Qualify(chat.Id), Account, chat.Title, "Messenger", chat.Group ? "group" : "single", chat.At,
            chat.Unread && !chat.PreviewOutgoing ? 1 : 0, false, false, chat.Archived,
            // Facebook says which conversations cannot be replied to; the rest take text. An encrypted conversation is
            // one of those only while its own connection is off, which the helper decides.
            chat.ReadOnly, QualifyMedia(chat.AvatarUrl ?? ""), preview, [], null, null, CapabilitiesOf(chat),
            chat.Participants.Select(person => new ChatParticipant(Person(person.Id), person.Name, QualifyMedia(person.AvatarUrl ?? ""), person.Self)).ToArray(),
            // The larger of the two, so a group whose listed members have all read is not called Read while some of
            // its members are not listed yet.
            Math.Max(chat.Participants.Length, chat.Members), IsEncrypted: chat.Encrypted,
            ReadUpTo: chat.ReadAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(chat.ReadAt) : null,
            ServiceMute: ServiceMuteOf(chat.MutedUntil), IsMarketplace: chat.Marketplace);
    }

    // A mute set in Messenger, on this account's phone or anywhere else. Whether it still holds is decided when the
    // conversation is read (LocalPreferenceChatReader); the helper already leaves out one that has ended.
    public static ServiceMute? ServiceMuteOf(long mutedUntil) => mutedUntil switch
    {
        < 0 => new(null),
        0 => null,
        // Past what a moment can be is not a mute this app can show; reading it as none is the direction that is noticed.
        > 253402300799999 => null,
        _ => new(DateTimeOffset.FromUnixTimeMilliseconds(mutedUntil)),
    };

    // Only true or unknown: a change the helper did not offer is not one this app claims Messenger refuses.
    private static ChatCapabilities CapabilitiesOf(MessengerChat chat) => !chat.Group || !(chat.CanRename || chat.CanAddMembers || chat.CanRemoveMembers)
        ? Capabilities
        : Capabilities with
        {
            RenameGroup = chat.CanRename ? true : null,
            AddMembers = chat.CanAddMembers ? true : null,
            RemoveMembers = chat.CanRemoveMembers ? true : null,
        };

    // The message half of a helper "changed" event. Receipts name messages whose only change is who has received or
    // read them: they redraw those rows like any change but are not new messages, and announcing them as new made
    // every receipt look like an arrival to the chat list.
    public static IEnumerable<RealtimeEvent> MessageEvents(JsonElement data, DateTimeOffset received)
    {
        foreach (var (property, change, isNew) in new (string, RealtimeKind, bool?)[] { ("messages", RealtimeKind.MessageChanged, true),
            ("receipts", RealtimeKind.MessageChanged, false), ("deleted", RealtimeKind.MessageDeleted, null) })
        {
            if (!data.TryGetProperty(property, out var messages) || messages.ValueKind != JsonValueKind.Object) continue;
            foreach (var chat in messages.EnumerateObject())
            {
                if (chat.Value.ValueKind != JsonValueKind.Array) continue;
                var ids = chat.Value.EnumerateArray().Select(id => Qualify(id.GetString() ?? "")).ToArray();
                if (ids.Length == 0) continue;
                var historical = data.TryGetProperty("history", out var history) && history.ValueKind == JsonValueKind.Object &&
                    history.TryGetProperty(chat.Name, out var listed) && listed.ValueKind == JsonValueKind.Array
                    ? listed.EnumerateArray().Select(id => Qualify(id.GetString() ?? "")).ToHashSet(StringComparer.Ordinal) : [];
                // A single helper table can contain a fetched page and unrelated live reactions. Split by message id,
                // never by the whole conversation or by the age of the message a reaction belongs to.
                var live = ids.Where(id => !historical.Contains(id)).ToArray();
                if (live.Length > 0)
                    yield return new(change, Qualify(chat.Name), live, isNew, received, ReceiptOnly: property == "receipts");
                var snapshots = ids.Where(historical.Contains).ToArray();
                if (snapshots.Length > 0)
                    yield return new(change, Qualify(chat.Name), snapshots, false, received, ReceiptOnly: property == "receipts", IsHistory: true);
            }
        }
    }

    public static ChatMessage Map(MessengerMessage message)
    {
        var attachments = (message.Media ?? []).Select(item => new MessageAttachment(
            item.Name,
            // Audio (a voice message, or a recording sent as a file) plays in place, as it does from the other services.
            item.Video ? "video" : item.Mime?.StartsWith("image/", StringComparison.Ordinal) == true ? "img"
                : item.Mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ? "audio" : "file",
            item.Mime, item.Size == 0 ? null : item.Size, false, item.Animated || item.Mime == "image/gif", false,
            // SourceUrl carries a video's still, as the Google side does, so the conversation shows an image inline.
            QualifyMedia(item.Id), string.IsNullOrEmpty(item.Preview) ? null : QualifyMedia(item.Preview),
            item.Width == 0 ? null : item.Width, item.Height == 0 ? null : item.Height,
            // A shared reel or post: a picture of it, and where it leads when opened.
            Link: string.IsNullOrEmpty(item.Link) ? null : item.Link, Play: item.Play,
            Description: string.IsNullOrWhiteSpace(item.Description) ? null : item.Description, Caption: item.Caption,
            ActionTitle: string.IsNullOrWhiteSpace(item.ActionTitle) ? null : item.ActionTitle)).ToArray();
        return new(Qualify(message.Id), Qualify(message.ChatId), Person(message.Sender), message.Name, message.At,
            // Facebook's own lines ("Jay is waiting for your response.") run across the conversation, as Messenger
            // draws them, rather than in the sender's bubble.
            message.At.UtcTicks.ToString("D19"), message.Text, message.Notice ? "system" : "text", message.Outgoing, message.Deleted,
            // Marketplace's "Message sent" card (2026-11-12), which the Messenger phone app does not show either; the
            // helper names it by its shape. Only that: anything else with nothing to draw still notifies and shows.
            message.Hidden && !message.Deleted,
            // Messenger reports a correction separately from the message, so "edited" is something the helper tells us
            // rather than something the message carries.
            message.EditedAt == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(message.EditedAt),
            string.IsNullOrEmpty(message.ReplyTo) ? null : Qualify(message.ReplyTo), attachments,
            (message.Reactions ?? []).Select(item => new MessageReaction(item.Key, Person(item.Sender))).ToArray(),
            // A message Facebook has a row for has been sent, whether from here or from another of this account's apps.
            // Facebook's own send status is not mapped: its numbers are undocumented upstream, and a guess at which
            // ones mean failure could call a delivered message failed. Who has read it: Messenger reports how far each
            // person has got, so the conversation can draw a face beside the newest message each of them has seen.
            new(message.Outgoing ? "SUCCESS" : null, message.Outgoing && message.Delivered > 0 ? message.Delivered : null, null,
                message.Outgoing && message.SeenAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(message.SeenAt) : null,
                (message.SeenBy ?? []).ToDictionary(Person, _ => (bool?)true),
                (message.ReadMark ?? []).Select(Person).ToArray()));
    }
}
