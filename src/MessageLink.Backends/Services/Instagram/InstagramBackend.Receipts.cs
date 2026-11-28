using MessageLink.Core.Messaging;
using MessageLink.Core.Services;

namespace MessageLink.App.Services.Instagram;

/// <summary>
/// Read receipts. Every thread Instagram sends carries how far each person has read it, so the list the poll reads every
/// twenty seconds and the newest page of a conversation already hold everything needed: nothing extra is asked of
/// Instagram for them.
/// </summary>
internal sealed partial class InstagramBackend
{
    private readonly InstagramReadPositions readPositions = new();
    // Guards readPositions and the remembered messages, which the poll now changes while a conversation is reading.
    private readonly object receiptGate = new();

    /// <summary>
    /// Takes the read positions from answers already in hand. Where one moved, the messages this session remembers in
    /// that conversation are brought up to date, and — when <paramref name="announce"/> — redrawn through a change that
    /// is not a new message, so the chat list neither re-reads the row nor treats it as an arrival.
    /// </summary>
    private void AdoptReads(IEnumerable<(string Chat, InstagramRead[]? Reads)> answers, bool announce)
    {
        if (identity is not { } known) return;
        List<RealtimeEvent>? changes = null;
        int moved = 0, redrawn = 0;
        lock (receiptGate)
        {
            foreach (var (chat, reads) in answers)
            {
                if (!readPositions.Advance(chat, reads)) continue;
                moved++;
                var ids = InstagramReadPositions.Redraw(seen, known.Chat(chat), readPositions.For(chat), known);
                redrawn += ids.Count;
                if (announce && ids.Count > 0) (changes ??= []).Add(new(RealtimeKind.MessageChanged, known.Chat(chat), ids, false, DateTimeOffset.UtcNow, ReceiptOnly: true));
            }
        }
        if (moved == 0) return;
        InstagramLog.Write($"instagram receipts moved={moved} redrawn={redrawn} announced={changes?.Count ?? 0}");
        foreach (var change in changes ?? []) realtime.Writer.TryWrite(change);
    }

    private void AdoptReads(IEnumerable<InstagramChat> answers, bool announce) =>
        AdoptReads(answers.Select(chat => (chat.Id, chat.Reads)), announce);

    // A copy, so a page can be mapped outside the lock while the poll moves a position.
    private Dictionary<string, long>? ReadsOf(string chat)
    {
        lock (receiptGate) return readPositions.For(chat) is { } positions ? new(positions, StringComparer.Ordinal) : null;
    }
}
