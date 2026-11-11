using NativeBeeper.Core.Messaging;

namespace NativeBeeper.App.Services.GoogleVoice;

// What the Google Voice helper returns, as it names it.
internal sealed record VoiceChat(string Id, string Title, VoiceParticipant[]? Participants, bool Unread, long LastActivity,
    VoiceMessage? Preview, bool Group, bool Archived, bool Spam);
internal sealed record VoiceParticipant(string Number, string? Name, bool Blocked, string? Avatar = null);
internal sealed record VoiceMessage(string Id, string ChatId, string? Sender, long Timestamp, bool Outgoing, string Kind,
    string? Text, VoiceAttachment[]? Attachments, string? CallKind = null);
internal sealed record VoiceAttachment(string Id, string? Mime, int Width, int Height, bool Unsupported, bool Voicemail = false, int Duration = 0);
internal sealed record VoicePage(VoiceMessage[]? Messages, string? Next);
internal sealed record VoiceChatPage(VoiceChat[]? Chats, string? Next);

// Identities in the shared inbox. Every id carries its account, so several Voice accounts never collide and every call
// reaches the helper that owns the conversation: gvoice:<account key>:<Voice id>, and gvoice-media:<account key>:<id>.
internal static class GoogleVoiceModels
{
    // The account-id half (and the text-route decision) lives in Core, where every client reads it; see GoogleVoiceIds.
    public const string Network = GoogleVoiceIds.Network;
    private const string MediaPrefix = "gvoice-media:";
    public static readonly ChatCapabilities Capabilities = GoogleVoiceIds.Capabilities;

    public static string Account(string key) => GoogleVoiceIds.Account(key);
    public static string Qualify(string key, string id) => GoogleVoiceIds.Qualify(key, id);
    public static string QualifyMedia(string key, string id) => MediaPrefix + key + ":" + id;
    public static bool Owns(string? id) => id?.StartsWith(GoogleVoiceIds.Prefix, StringComparison.Ordinal) == true;
    public static bool OwnsMedia(MessageAttachment attachment) => attachment.Id?.StartsWith(MediaPrefix, StringComparison.Ordinal) == true;

    public static (string Key, string Id)? Parse(string? value) => GoogleVoiceIds.Parse(value);
    public static (string Key, string Id)? ParseMedia(string? value) => GoogleVoiceIds.Parse(value, MediaPrefix);
    public static string? AccountKey(string? accountId) => GoogleVoiceIds.AccountKey(accountId);
    public static string? Number(string? input) => GoogleVoiceIds.Number(input);

    // Someone with no name is shown by number as every other service shows one, (860) 555-0147, not as Voice writes it (+18605550147).
    public static string Shown(string number) => NativeBeeper.Presentation.Services.ConversationHeaderText.Phone(number) ?? number;
    public static ChatSummary? TextRouteForCall(ChatSummary call) => GoogleVoiceIds.TextRouteForCall(call);

    // The Voice helper derives this opaque value from the provider's contact-photo URL and keeps the URL inside its
    // process. Accept only its fixed `avatar:` + SHA-256 shape before routing it through the encrypted media cache.
    internal static string? AvatarForContact(string key, string? avatar)
    {
        if (avatar is not { Length: 71 } || !avatar.StartsWith("avatar:", StringComparison.Ordinal)) return null;
        foreach (var c in avatar.AsSpan("avatar:".Length)) if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)) return null;
        return QualifyMedia(key, avatar);
    }

    // Voice text threads are named t.<number>. A one-person non-text thread is the separate call/voicemail timeline.
    public static bool CallHistoryOnly(ChatSummary? chat) => chat is { Network: Network, Participants.Count: 1 }
        && Parse(chat.Id) is { } parsed && !parsed.Id.StartsWith("t.", StringComparison.Ordinal);

    public static string? ParticipantNumber(ChatSummary? chat)
    {
        if (chat?.Participants.FirstOrDefault() is not { } participant || Parse(participant.Id) is not { } parsed
            || !parsed.Id.StartsWith("person:", StringComparison.Ordinal)) return null;
        return Number(parsed.Id["person:".Length..]);
    }

    // The account's own number identifies it among the participants: Voice sends a thread's people without saying
    // which one is this account, and everything that asks who a conversation is with needs to be able to leave it out.
    public static ChatSummary Map(string key, VoiceChat chat, string? accountNumber = null)
    {
        var self = Number(accountNumber);
        var participants = (chat.Participants ?? []).Select(person =>
            new ChatParticipant(Qualify(key, "person:" + person.Number), string.IsNullOrWhiteSpace(person.Name) ? Shown(person.Number) : person.Name!,
                AvatarForContact(key, person.Avatar), self is not null && Number(person.Number) == self, person.Number)).ToArray();
        // Who the conversation is actually with. A thread that lists this account as well is still a conversation with
        // one person, and its own picture is that person's: counting the account in made it a group of two, which drew
        // the account's own circle beside theirs and left the conversation with no picture of its own.
        var others = participants.Where(person => person.IsSelf != true).ToArray();
        var group = others.Length > 1;
        var avatar = others.Length == 1 ? others[0].AvatarUrl : null;
        var names = participants.ToDictionary(person => person.Id, person => person.DisplayName);
        var preview = chat.Preview is null ? null : Map(key, chat.Preview, names);
        var at = chat.LastActivity > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(chat.LastActivity) : (DateTimeOffset?)null;
        // The helper names a thread after everyone in it. Where that included this account, the name is the people it
        // is with, so a conversation with one person is not titled after two.
        var title = others.Length > 0 && others.Length != participants.Length
            ? string.Join(", ", others.Select(person => person.DisplayName)) : Number(chat.Title) is not null ? Shown(chat.Title) : chat.Title;
        return new(Qualify(key, chat.Id), Account(key), string.IsNullOrWhiteSpace(title) ? "Google Voice conversation" : title,
            Network, group ? "group" : "single", at, chat.Unread && chat.Preview?.Outgoing != true ? 1 : 0,
            false, false, chat.Archived, false, avatar, preview, [], null, null, Capabilities, participants, participants.Length,
            IsSpam: chat.Spam, IsCallHistory: !chat.Id.StartsWith("t.", StringComparison.Ordinal) && others.Length == 1);
    }

    // Deleting a conversation (2026-10-04): what the confirmation says, and what each helper answer means. Voice removes it
    // from this account only, so the people in it keep theirs. Only these answers mean it certainly was not deleted; any
    // other may have reached Google, and is reported as unknown and never asked again.
    internal static string DeleteConfirmation(string title) =>
        $"“{title}” will be deleted from your Google Voice account. The people in it keep their copy. This can't be undone.";

    internal static bool DeleteOutcomeUnknown(string code) => code is not ("delete_rejected" or "auth_required" or "invalid_input"
        or "login_required" or "not_connected" or "request_cancelled" or "unknown_method");

    internal static string ExplainDelete(string code) => code switch
    {
        "delete_rejected" => "Google Voice didn't delete this conversation.",
        "auth_required" => "Google Voice needs you to sign in again in Settings. The conversation wasn't deleted.",
        "login_required" or "not_connected" => "Google Voice isn't connected. The conversation wasn't deleted.",
        "invalid_input" => "This conversation can't be deleted with Google Voice.",
        "request_cancelled" => "The delete was canceled before it reached Google Voice. Nothing was deleted.",
        "unknown_method" => "This Google Voice connection can't delete conversations. Nothing was deleted.",
        _ => "Google Voice didn't confirm the delete. Check Google Voice before trying again.",
    };

    // A saved voicemail is named for who left it and when, in local time, so a folder of them sorts and reads well:
    // "Jesse Bevel voicemail 2026-09-11 17.34.mp3". Characters a file name cannot hold are replaced when it is saved.
    internal static string VoicemailFileName(string? caller, long timestamp, string mime)
    {
        var at = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).ToLocalTime();
        var extension = mime switch { "audio/mp4" => ".m4a", "audio/ogg" => ".ogg", "audio/wav" => ".wav", "audio/amr" => ".amr", _ => ".mp3" };
        var who = string.IsNullOrWhiteSpace(caller) ? "Unknown caller" : caller.Trim();
        return $"{who} voicemail {at:yyyy-MM-dd HH.mm}{extension}";
    }

    internal static string? UnsupportedNote(int count) => count switch
    {
        <= 0 => null,
        1 => "This message had an attachment Google Voice doesn't support, so it can't be shown.",
        _ => $"This message had {count} attachments Google Voice doesn't support, so they can't be shown.",
    };

    public static ChatMessage Map(string key, VoiceMessage message, IReadOnlyDictionary<string, string>? names = null)
    {
        var sender = message.Outgoing ? Qualify(key, "self") : Qualify(key, "person:" + (message.Sender ?? ""));
        var senderName = message.Outgoing ? "You" : names is not null && names.TryGetValue(sender, out var name) ? name : message.Sender;
        var attachments = (message.Attachments ?? []).Where(item => !item.Unsupported).Select(item => item.Voicemail
            // Voicemail audio plays in place; the helper fetches it from Google only when it is played. Voice serves MP3.
            ? new MessageAttachment(VoicemailFileName(senderName, message.Timestamp, item.Mime ?? "audio/mpeg"), "audio", item.Mime ?? "audio/mpeg", null, true, false, false, QualifyMedia(key, item.Id),
                DurationSeconds: item.Duration > 0 ? item.Duration : null)
            : new MessageAttachment(
                null, item.Mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? "img"
                    : item.Mime?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ? "video"
                    // Audio Google does hand over plays in place, as received audio does on every other service, rather
                    // than drawing as a file card.
                    : item.Mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ? "audio" : "file",
                item.Mime, null, false, item.Mime == "image/gif", false, QualifyMedia(key, item.Id), null,
                item.Width > 0 ? item.Width : null, item.Height > 0 ? item.Height : null)).ToArray();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp);
        // Calls sit among the texts as notes in words, as Google Voice lists them. A voicemail is a message from its
        // caller: its transcript, with the recording to play underneath.
        var type = message.Kind is "call" ? "system" : "text";
        var text = message.Kind == "unsupported" && attachments.Length == 0 ? "This Google Voice item can't be shown here." : message.Text;
        // Google marks a picture message's part it will not carry (a voice message texted to a Voice number, for one) as
        // not supported, and offers nothing to fetch. Said in the message rather than dropped, which left an empty bubble.
        if (message.Kind != "unsupported" && UnsupportedNote((message.Attachments ?? []).Count(item => item.Unsupported)) is { } note)
            text = string.IsNullOrWhiteSpace(text) ? note : text + "\n\n" + note;
        return new(Qualify(key, message.Id), Qualify(key, message.ChatId), sender, senderName, at, at.UtcTicks.ToString("D19"),
            text, type, message.Outgoing, false, false, null, null, attachments, [],
            new(message.Outgoing ? "SUCCESS" : null, null, null, null, new Dictionary<string, bool?>()), CallKind: message.CallKind);
    }
}
