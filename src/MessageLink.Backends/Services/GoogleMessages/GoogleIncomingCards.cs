using MessageLink.Core.Services;

namespace MessageLink.App.Services.GoogleMessages;

// Whether a link that came in a Google Messages conversation may have its card made as it arrives, before anyone opens
// the conversation. Making a card fetches the linked page (the helper does it anonymously, ui.linkPreviews), and the
// conversation on screen has always done that for what it shows; asked ahead of time it is a fetch nobody chose, so it is
// asked only where opening the conversation would plainly have led to it:
// - never with link previews turned off (Settings), which is the user saying no page is to be fetched for them;
// - never in a conversation the list does not show this session or files as spam, nor one Google keeps hidden;
// - never from a short code or a named sender (an alphanumeric id): bulk senders' links are what a person least wants
//   fetched on their behalf, and the fetch itself tells the sender's site the link was looked at;
// - only from someone the user knows: in a one-to-one conversation a contact saved on the phone (Google gives a first name
//   only for those), or anywhere the user has written themselves (an outgoing message in the archive this PC keeps).
// The signal chosen is the archive's own record of the user's messages, with the phone's saved contact as a second: both
// are local, neither asks Google anything, and a conversation the user has never written in and whose sender the phone
// does not know gets no card until it is opened.
//
// listedAsSpam (RefusalAsync): how the conversation list shows the conversation this session — null when it does not show it at all.
// The list is the client's, so the client says (IIncomingCards).
internal sealed class GoogleIncomingCards(GoogleArchive archive, IAccessTokenStore tokens)
{
    // How many of a conversation's newest archived messages are looked through for one of the user's own. Read once per
    // conversation and remembered: yes for the session, no for a few minutes (the user may write there meanwhile).
    private const int OwnLookback = 40;
    private static readonly TimeSpan NoAgain = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (bool Wrote, DateTimeOffset At)> wrote = new(StringComparer.Ordinal);
    private readonly object gate = new();

    /// <summary>Why no card may be asked for, as a fixed word for the log, or null when it may.</summary>
    public async Task<string?> RefusalAsync(string chatId, bool previewsOn, bool? listedAsSpam, CancellationToken token)
    {
        if (!previewsOn) return "off";
        if (!GoogleModels.Owns(chatId)) return "unknown_chat";
        if (listedAsSpam is not { } spam) return "unknown_chat";
        if (spam) return "spam";
        var chat = archive.Chat(GoogleModels.Native(chatId));
        var early = Refusal(chat, previewsOn, wroteThere: null);
        if (early is not "unknown_sender") return early;
        return Refusal(chat, previewsOn, await WroteThereAsync(GoogleModels.Native(chatId), token));
    }

    /// <summary>The rules above for a conversation as Google lists it. <paramref name="wroteThere"/> null means not
    /// looked up yet, which refuses as an unknown sender would when nothing else allows it.</summary>
    public static string? Refusal(GoogleChat? chat, bool previewsOn, bool? wroteThere)
    {
        if (!previewsOn) return "off";
        if (chat is null) return "unknown_chat";
        if (chat.Hidden) return "hidden";
        // A participant row with no number at all is a duplicate the phone never shows, not a short code: counted, it
        // refused ordinary conversations as "short_code".
        var others = chat.Participants.Where(person => !person.Self && !string.IsNullOrWhiteSpace(person.Id)).ToArray();
        if (others.Length == 0) return "unknown_chat";
        if (others.Any(person => !IsPersonNumber(person.Id))) return "short_code";
        if (wroteThere == true) return null;
        if (!chat.Group && others.All(SavedContact)) return null;
        return "unknown_sender";
    }

    // A person's number: at least eight digits, and nothing but what numbers are written with. A short code ("72975") or a
    // named sender ("GOOGLE", an RCS agent's id) is not.
    public static bool IsPersonNumber(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var digits = 0;
        foreach (var c in id)
        {
            if (char.IsAsciiDigit(c)) digits++;
            else if (c is not ('+' or ' ' or '-' or '(' or ')' or '.')) return false;
        }
        return digits >= 8;
    }

    // Google names a participant's first name only from the phone's contacts; an unsaved number has none, or a number.
    public static bool SavedContact(GooglePerson person) =>
        !string.IsNullOrWhiteSpace(person.First) && person.First.Any(char.IsLetter);

    // An outgoing message Google sent or tried to; a draft or one still scheduled is not the user writing to anyone yet.
    public static bool Written(string? status) =>
        status is { } value && value.StartsWith("OUTGOING_", StringComparison.Ordinal) &&
        value is not ("OUTGOING_DRAFT" or "OUTGOING_SCHEDULED");

    private async Task<bool> WroteThereAsync(string native, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
            if (wrote.TryGetValue(native, out var known) && (known.Wrote || now - known.At < NoAgain)) return known.Wrote;
        var found = false;
        if (await MessageLink.Presentation.Services.NotificationIdentity.ProfileAsync(tokens, token) is { } profile)
        {
            var timeline = archive.Timeline(native);
            for (var index = timeline.Length - 1; index >= 0 && index >= timeline.Length - OwnLookback && !found; index--)
                found = Written((await archive.ReadMessageAsync(profile, timeline[index].Id, token))?.Status);
        }
        lock (gate)
        {
            if (wrote.Count >= 512) wrote.Clear();
            wrote[native] = (found, now);
        }
        return found;
    }
}
