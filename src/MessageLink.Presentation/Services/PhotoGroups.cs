using MessageLink.Core.Messaging;
using MessageLink.Presentation.ViewModels;

namespace MessageLink.Presentation.Services;

// Presentation only: original rows/messages remain available to history, receipts, search and per-photo actions.
public static class PhotoGroups
{
    private static bool Eligible(MessageItemViewModel row) =>
        !row.Message.IsDeleted && !row.Message.IsHidden && !row.IsSystem && !row.IsEdited &&
        // Reactions and read positions do not split a set: the set row shows them together.
        string.IsNullOrWhiteSpace(row.Message.Text) && !row.HasReply &&
        row.Message.Delivery.SendState is null or "SUCCESS" && row.Message.Delivery.Local is null &&
        row.Message.Attachments.Count == 1 && row.Message.Attachments.All(a =>
            PhotoPresentation.IsPhoto(a) && !a.IsSticker && !a.IsGif && !a.Play && a.Link is null && string.IsNullOrWhiteSpace(a.Caption));

    // Google Messages draws each photo as its own message, on the phone and on the web, and sends a set one photo at a time.
    // A stack of them here was never what the other person saw, and our own set could only fold into it once every photo
    // was confirmed (2026-10-01). Its photos stay separate.
    private static bool Stacks(string? network) => !string.Equals(network, "Google Messages", StringComparison.OrdinalIgnoreCase);

    public static void Apply(IReadOnlyList<MessageItemViewModel> rows, string? network = null,
        IReadOnlyDictionary<string, string>? exactBatches = null)
    {
        for (var start = 0; start < rows.Count;)
        {
            var end = start;
            var batch = exactBatches?.GetValueOrDefault(MessageIdentity.Key(rows[start].Message));
            if (Eligible(rows[start]) && Stacks(network ?? rows[start].SourceNetwork))
                while (end + 1 < rows.Count && end - start < (batch is null ? 5 : AttachmentBatchRules.ItemLimit - 1) && Eligible(rows[end + 1]) &&
                    rows[end + 1].Message.ChatId == rows[start].Message.ChatId &&
                    rows[end + 1].Message.SenderId == rows[start].Message.SenderId &&
                    rows[end + 1].IsOutgoing == rows[start].IsOutgoing &&
                    // Exact accepted-batch membership keeps a slow upload/send sequence
                    // together without widening the heuristic for unrelated photos.
                    exactBatches?.GetValueOrDefault(MessageIdentity.Key(rows[end + 1].Message)) == batch &&
                    (batch is not null || !rows[end + 1].ShowDate &&
                    // Messenger's native encrypted multi-select is a rapid sequence of individual image messages.
                    // Keep the window tight so ordinary photos sent separately remain separate.
                    (!string.Equals(network ?? rows[start].SourceNetwork, "Messenger", StringComparison.OrdinalIgnoreCase) ||
                        rows[end + 1].Message.Timestamp - rows[start].Message.Timestamp <= TimeSpan.FromSeconds(2)) &&
                    rows[end + 1].Message.Timestamp - rows[start].Message.Timestamp <= TimeSpan.FromMinutes(5))) end++;
            if (end > start)
            {
                var messages = rows.Skip(start).Take(end - start + 1).Select(row => row.Message).ToArray();
                for (var index = start; index < end; index++) rows[index].SetPhotoGroup([], true, rows[start]);
                rows[end].SetPhotoGroup(messages, false, rows[start], rows.Skip(start).Take(end - start + 1).ToArray());
            }
            start = end + 1;
        }
    }
}
