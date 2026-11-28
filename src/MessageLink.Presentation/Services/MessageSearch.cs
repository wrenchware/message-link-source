using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.Presentation.Services;

// Shared local matching for the protected Google archive, loaded history and merged history.
// Text is indexed in memory; this never creates a plaintext copy on disk or fetches a URL.
public static class MessageSearch
{
    public static string SearchableText(ChatMessage message) => string.Join(' ',
        new[] { MessageText.PlainText(message) }.Concat(message.Attachments.SelectMany(a =>
            new[] { a.Name, a.Caption, a.Description, a.Link }.OfType<string>())));

    public static bool WordsMatch(string? text, string query)
    {
        var terms = TextSearchIndex.Words(query).Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Length == 0) return string.IsNullOrWhiteSpace(query);
        var words = TextSearchIndex.Words(text).Distinct(StringComparer.Ordinal).ToArray();
        return terms.All(term => words.Any(word => word.StartsWith(term, StringComparison.Ordinal)));
    }

    public static bool MatchesChat(ChatSummary chat, SearchRequest request)
    {
        if (chat.IsSpam || request.ChatId is not null && request.ChatId != chat.Id) return false;
        if (request.Filters?.Service is { Length: > 0 } service && !string.Equals(chat.Network, service, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.Filters?.Person is { Length: > 0 } person && request.Filters.LinkedChatIds?.Contains(chat.Id, StringComparer.Ordinal) != true && !WordsMatch(chat.Title, person) &&
            !chat.Participants.Any(p => p.IsSelf != true && WordsMatch(p.DisplayName, person))) return false;
        return true;
    }

    public static bool Matches(ChatSummary chat, ChatMessage message, SearchRequest request, bool matchWords = true) =>
        !message.IsDeleted && !message.IsHidden && MatchesChat(chat, request) &&
        MatchesDate(message.Timestamp, request.Filters) &&
        (request.Filters?.Content is not { } content || content == SearchContent.All || HasContent(message, content)) &&
        (!matchWords || string.IsNullOrWhiteSpace(request.Query) || WordsMatch(SearchableText(message), request.Query));

    public static bool MatchesDate(DateTimeOffset timestamp, SearchFilters? filters)
    {
        var date = DateOnly.FromDateTime(timestamp.LocalDateTime);
        return (filters?.From is not { } from || date >= from) && (filters?.Through is not { } through || date <= through);
    }

    public static bool HasContent(ChatMessage message, SearchContent content) => content switch
    {
        SearchContent.All => true,
        SearchContent.Photos => message.Attachments.Any(a => a.Link is null && !a.IsSticker && (a.Type == "img" || a.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)),
        SearchContent.Videos => message.Attachments.Any(a => a.Play || a.Type == "video" || a.MimeType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true),
        SearchContent.Links => message.Attachments.Any(a => MessageText.WebLink(a.Link ?? "") is not null) || MessageText.Parse(MessageText.Markup(message)).Any(run => run.Link is { } link && MessageText.IsWeb(link)),
        SearchContent.Files => message.Attachments.Any(a => a.Link is null && a.Type == "file"),
        SearchContent.Audio => message.Attachments.Any(a => a.Type == "audio" || a.IsVoiceNote || a.MimeType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true),
        SearchContent.Calls => message.CallKind is not null && !IsVoicemail(message),
        SearchContent.Voicemail => IsVoicemail(message),
        _ => false
    };

    private static bool IsVoicemail(ChatMessage message) => message.CallKind?.Contains("voicemail", StringComparison.OrdinalIgnoreCase) == true;
}
