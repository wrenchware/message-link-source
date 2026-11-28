using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MessageLink.Core.Messaging;

namespace MessageLink.App.Services;

/// <summary>
/// Watches for a read a service took and then did not keep, and tells it once more. Every read reaches a service through the
/// router, and every conversation the clients are shown passes back through it, so both halves are seen here without asking any
/// service anything new: a read accepted for a conversation's newest message, then that conversation reported unread again with
/// nothing newer, is drift. Messenger did exactly this for one conversation after the server's nightly restart (2026-10-22).
/// </summary>
/// <remarks>
/// <para><b>Bounded.</b> A drifted read is sent again once per conversation per message per run (owner, 2026-10-22, every service,
/// Instagram included under its own backend's rules), through the router so private reading still refuses it. A second drift on the
/// same message is only counted. Nothing else is ever sent, and nothing is read that was not already marked read for that message.</para>
/// <para><b>Grace.</b> A service's own unread can trail a read it took (Voice lists again at most every 10 s), so unread seen within
/// <see cref="Grace"/> of the read waits for the grace to end: the conversation is then asked again where asking is a memory read,
/// and Instagram's is judged on what it last said, since asking Instagram is a request to Instagram.</para>
/// <para><b>Logs.</b> Counts, the service's word and a one-way tag of the conversation; never a name, a message or an id.</para>
/// </remarks>
internal sealed class ReadDrift(Func<string, string, CancellationToken, Task<bool>> resend, Func<string, CancellationToken, Task<ChatSummary>>? reread,
    Action<string> log, TimeProvider? clock = null)
{
    public static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(30);
    private const int Remembered = 20_000;

    private sealed class Mark(string messageId, DateTimeOffset? activity, DateTimeOffset acceptedAt)
    {
        public string MessageId { get; } = messageId;
        public DateTimeOffset? Activity { get; } = activity;
        public DateTimeOffset AcceptedAt { get; } = acceptedAt;
        public ChatSummary? Last;
        public int Verifying;
        public int Reported;
        // Its grace has been waited out: a timer can end a moment before the clock agrees, and the answer asked for then must not count as within it.
        public volatile bool GraceOver;
    }

    private readonly ConcurrentDictionary<string, Mark> marks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset?> seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> healed = new(StringComparer.Ordinal);
    private int accepted, reverted, resent, resendFailed, stuck;

    private DateTimeOffset Now() => (clock ?? TimeProvider.System).GetUtcNow();

    /// <summary>A read the service took for this conversation through this message.</summary>
    public void Accepted(string chatId, string messageId)
    {
        Interlocked.Increment(ref accepted);
        if (marks.Count >= Remembered) marks.Clear();
        marks[chatId] = new Mark(messageId, seen.GetValueOrDefault(chatId), Now());
    }

    /// <summary>A conversation as its service describes it now, on its way to a client.</summary>
    public void Observe(ChatSummary chat)
    {
        if (seen.Count >= Remembered) seen.Clear();
        seen[chat.Id] = chat.LastActivity;
        if (!marks.TryGetValue(chat.Id, out var mark)) return;
        if (Newer(chat, mark)) { marks.TryRemove(new(chat.Id, mark)); return; }
        mark.Last = chat;
        if (chat.UnreadCount <= 0 || chat.IsSpam) return;
        var age = Now() - mark.AcceptedAt;
        if (age < Grace && !mark.GraceOver)
        {
            if (Interlocked.Exchange(ref mark.Verifying, 1) == 0) _ = VerifyLaterAsync(chat.Id, mark, Grace - age);
            return;
        }
        _ = RevertedAsync(chat.Id, mark);
    }

    // Something after the read: a newer activity, or (with no activity known at the read) a different newest message.
    private static bool Newer(ChatSummary chat, Mark mark) =>
        mark.Activity is { } then ? chat.LastActivity is { } now && now > then
        : chat.Preview?.Id is { } newest && newest != mark.MessageId;

    private async Task VerifyLaterAsync(string chatId, Mark mark, TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait).ConfigureAwait(false);
            if (!marks.TryGetValue(chatId, out var current) || !ReferenceEquals(current, mark)) return;
            mark.GraceOver = true;
            // Asked again where that is a memory read: its answer passes through Observe like any other, now past the grace.
            if (reread is not null && !Instagram(chatId)) { await reread(chatId, CancellationToken.None).ConfigureAwait(false); return; }
            if (mark.Last is { UnreadCount: > 0, IsSpam: false } last && !Newer(last, mark)) await RevertedAsync(chatId, mark).ConfigureAwait(false);
        }
        catch (Exception) { /* Not knowing is not drift. */ }
        finally { Interlocked.Exchange(ref mark.Verifying, 0); }
    }

    private async Task RevertedAsync(string chatId, Mark mark)
    {
        if (Interlocked.Exchange(ref mark.Reported, 1) != 0) return;
        Interlocked.Increment(ref reverted);
        var service = Service(chatId);
        var tag = Tag(chatId);
        if (!healed.TryAdd(chatId + "\n" + mark.MessageId, 0))
        {
            Interlocked.Increment(ref stuck);
            Write($"read drift reverted service={service} chat={tag} resend=already {Totals()}");
            return;
        }
        if (healed.Count >= Remembered) healed.Clear();
        string outcome;
        try { outcome = await resend(chatId, mark.MessageId, CancellationToken.None).ConfigureAwait(false) ? "accepted" : "withheld"; }
        catch (Exception) { outcome = "failed"; Interlocked.Increment(ref resendFailed); }
        if (outcome == "accepted") Interlocked.Increment(ref resent);
        Write($"read drift reverted service={service} chat={tag} resend={outcome} {Totals()}");
    }

    private string Totals() => $"accepted={Volatile.Read(ref accepted)} reverted={Volatile.Read(ref reverted)} resent={Volatile.Read(ref resent)} " +
        $"resend_failed={Volatile.Read(ref resendFailed)} stuck={Volatile.Read(ref stuck)}";

    private void Write(string line)
    {
        try { log(line); } catch (Exception) { /* a logger that fails must not fail a read */ }
    }

    private static bool Instagram(string chatId) => chatId.StartsWith("instagram:", StringComparison.Ordinal);

    // The service's own word from the id's prefix: fixed words, never the id itself.
    internal static string Service(string chatId) => chatId switch
    {
        _ when chatId.StartsWith("instagram:", StringComparison.Ordinal) => "instagram",
        _ when chatId.StartsWith("messenger:", StringComparison.Ordinal) => "messenger",
        _ when chatId.StartsWith("gvoice:", StringComparison.Ordinal) => "gvoice",
        _ when chatId.StartsWith("google-direct:", StringComparison.Ordinal) => "google",
        _ => "other",
    };

    // The same tag each service's own log gives the conversation, so a line can be followed into it; Voice's log tags accounts, so
    // its conversations get one of their own.
    internal static string Tag(string chatId) => Service(chatId) switch
    {
        "instagram" => MessageLink.App.Services.Instagram.InstagramLog.Tag(chatId),
        "messenger" => MessageLink.App.Services.Messenger.MessengerLog.Tag(chatId),
        "google" => MessageLink.App.Services.GoogleMessages.GoogleMessagesLog.Tag(chatId),
        _ => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("read-drift:" + chatId)).AsSpan(0, 3)).ToLowerInvariant(),
    };
}
