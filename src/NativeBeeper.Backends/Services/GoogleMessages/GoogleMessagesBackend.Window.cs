using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// A window of a conversation's history around a moment, read from the protected archive: the whole message is kept there,
// so it maps to the same rows the phone's pages do. Only what the crawl has walked is read this way, since only that is
// known to have no gaps: all of a conversation it finished, and for one still being crawled, from where the crawl has got
// to (its cursor) up to now. A result further back than that pages back from the phone as before. A row the archive
// holds can lack what fetching its photo needs (the helper learns that only from a page the phone sends), so a window with
// such photos is drawn at once and then asked of the phone, as scrolling back to it would ask, and the phone's copies
// replace their rows when they come.
internal sealed partial class GoogleMessagesBackend : IHistoryWindowReader
{
    private const int WindowPhonePage = 100, WindowPhonePages = 4;

    public bool CanReadWindow(string chatId) =>
        chatId.StartsWith("google-direct:", StringComparison.Ordinal) && archive.IsLoaded && archive.Profile is not null &&
        CrawledFrom(archive.CrawlState(Models.Native(chatId))) is not null;

    // How far back a conversation's archive is known to be whole: everything once its crawl finished, otherwise from the
    // oldest message the crawl has reached (its cursor names it). Null when it has not begun.
    private static DateTimeOffset? CrawledFrom(GoogleCrawlState? state)
    {
        if (state is null) return null;
        if (state.Done) return DateTimeOffset.MinValue;
        if (state.Cursor is not { Length: > 0 } cursor) return null;
        try
        {
            using var parsed = JsonDocument.Parse(cursor);
            return parsed.RootElement.TryGetProperty("lastItemTimestamp", out var at) && at.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default)
    {
        var native = Models.Native(chatId);
        var account = archive.Profile ?? throw new BackendException(BackendFailure.Unavailable, "Google Messages history is still loading.");
        var chat = chats.GetValueOrDefault(native) ?? archive.Chat(native)
            ?? throw new BackendException(BackendFailure.Unavailable, "This conversation is not available.");
        var from = CrawledFrom(archive.CrawlState(native)) ?? DateTimeOffset.MaxValue;
        var timeline = archive.Timeline(native).Where(entry => entry.At >= from).ToArray();
        var split = 0;
        while (split < timeline.Length && timeline[split].At < at) split++;
        var start = Math.Max(0, split - older);
        while (start > 0 && start < split && timeline[start - 1].At == timeline[start].At) start--;
        var end = Math.Min(timeline.Length, split + newer);
        while (end > split && end < timeline.Length && timeline[end].At == timeline[end - 1].At) end++;
        var rows = new List<GoogleMessage>(end - start);
        var unfetchable = false;
        for (var index = start; index < end; index++)
        {
            var id = timeline[index].Id;
            if (messages.TryGetValue(id, out var held)) { rows.Add(held); continue; }
            if (await archive.ReadMessageAsync(account, id, cancellationToken) is not { } kept) continue;
            rows.Add(kept);
            unfetchable |= kept.Media is { Length: > 0 };
        }
        GoogleMessagesLog.Write($"history window chat={GoogleMessagesLog.Tag(chatId)} older={older} newer={newer} returned={rows.Count} " +
            $"more_older={start > 0} more_newer={end < timeline.Length} photos_from_phone={unfetchable}");
        var window = new HistoryWindow([.. rows.Select(message => Models.Map(message, chat))], start > 0, end < timeline.Length);
        if (!unfetchable || rows.Count == 0) return window;
        // The phone's page that ends at the window's newest message starts just past it: at the next message the archive
        // knows of, or at the newest page when there is none.
        var after = end < timeline.Length ? timeline[end] : default;
        return window with { Refreshed = RefreshWindowAsync(chatId, after.Id is null ? null : after, rows[0].At, cancellationToken) };
    }

    // Where each conversation stood (its newest message's time) in the first list this connection read. Anything newer came
    // live and was archived as it came; anything up to it is in the archive once a catch-up has reached it. Cleared on every
    // connection, since what arrived while the last one was down came as neither.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> headsAtConnect = new(StringComparer.Ordinal);
    private int connectionNumber;
    private static readonly System.Text.RegularExpressions.Regex MaybeLink =
        new(@"://|www\.|[\p{L}\p{N}-]\.[\p{L}]{2,}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // An opening conversation's newest messages from the archive, drawn while the phone is asked (2026-10-02: texts that
    // came while the app was closed appeared a second after opening, behind the older saved copy). Only for a conversation
    // whose crawl finished and whose last catch-up reached at least where it stood when this connection began, so the
    // archive holds every message to now. A row the phone's page would draw differently is not drawn early: the helper
    // attaches a photo's means of fetching, and a link's card, only to a message it has seen in a page or live this
    // session, and the crawl's pages register neither; a crawled photo would show as gone, and a link would grow a card.
    // Soon after a launch the archive has not loaded yet (it pauses while the app is in use); a conversation the
    // connection's own catch-up has filled (PriorityCatchUp) is answered from memory meanwhile.
    public async Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default)
    {
        if (!chatId.StartsWith("google-direct:", StringComparison.Ordinal)) return null;
        var native = Models.Native(chatId);
        var tag = GoogleMessagesLog.Tag(chatId);
        var refused = GoogleHeldRules.Refusal(connected, archive.CrawlState(native), headsAtConnect.TryGetValue(native, out var head) ? head : null);
        if (refused is not null) { GoogleMessagesLog.Write($"history held chat={tag} outcome={refused}"); return null; }
        if ((chats.GetValueOrDefault(native) ?? archive.Chat(native)) is not { } chat) return null;
        DateTimeOffset? from = freshFrom.TryGetValue(native, out var since) ? since : null;
        var inMemory = from is { } start
            ? messages.Values.Where(message => message.ChatId == native && message.At >= start)
                .OrderBy(message => message.At).ThenBy(message => message.Id, StringComparer.Ordinal).ToArray()
            : [];
        switch (GoogleHeldRules.Source(after, from, inMemory.Length, limit, archive.IsLoaded && archive.Profile is not null))
        {
            case GoogleHeldSource.Memory:
                var held = inMemory.Where(message => message.At > after).ToArray();
                var newest = held.TakeLast(limit).ToArray();
                GoogleMessagesLog.Write($"history held chat={tag} outcome=ok source=memory newer={held.Length} returned={newest.Length}");
                return new([.. newest.Select(message => Models.Map(message, chat))], after >= from && newest.Length == held.Length);
            case GoogleHeldSource.None:
                GoogleMessagesLog.Write($"history held chat={tag} outcome=archive_not_loaded fresh={from is not null}");
                return null;
        }
        var account = archive.Profile!;
        var newer = archive.Timeline(native).Where(entry => entry.At > after).ToArray();
        var taken = newer.TakeLast(limit).ToArray();
        var rows = new List<ChatMessage>(taken.Length);
        var fromMemory = 0;
        foreach (var (id, _) in taken)
        {
            if (messages.TryGetValue(id, out var held)) { rows.Add(Models.Map(held, chat)); fromMemory++; continue; }
            var kept = await archive.ReadMessageAsync(account, id, cancellationToken);
            refused = kept is null ? "missing" : kept.Media is { Length: > 0 } || MaybeLink.IsMatch(kept.Text ?? "") ? "phone_only" : null;
            if (refused is not null)
            {
                GoogleMessagesLog.Write($"history held chat={tag} outcome={refused} newer={newer.Length}");
                return null;
            }
            rows.Add(Models.Map(kept!, chat));
        }
        GoogleMessagesLog.Write($"history held chat={tag} outcome=ok source=archive newer={newer.Length} returned={rows.Count} from_memory={fromMemory}");
        return new(rows, taken.Length == newer.Length);
    }

    // The phone's own pages over the window, newest first, until they reach its oldest message.
    private async Task<IReadOnlyList<ChatMessage>> RefreshWindowAsync(string chatId, (string Id, DateTimeOffset At)? after, DateTimeOffset oldest,
        CancellationToken cancellationToken)
    {
        var cursor = after is { } next
            ? JsonSerializer.Serialize(new { lastItemID = next.Id, lastItemTimestamp = next.At.ToUnixTimeMilliseconds() }) : null;
        var fresh = new List<ChatMessage>();
        for (var page = 0; page < WindowPhonePages; page++)
        {
            var read = await FetchMessagePageAsync(chatId, cursor, WindowPhonePage, cancellationToken);
            fresh.AddRange(read.Items);
            if (!read.HasMore || read.Items.Count == 0 || read.Items[0].Timestamp <= oldest) break;
            cursor = read.OldestCursor;
        }
        GoogleMessagesLog.Write($"history window chat={GoogleMessagesLog.Tag(chatId)} refreshed={fresh.Count}");
        return fresh;
    }
}
