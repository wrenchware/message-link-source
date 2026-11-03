using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed record GoogleContact(string Id, string Name, string Phone);

internal static class GoogleDirectory
{
    public const int MaximumConversationCount = 2000;
    // Google's Spam & blocked folder, newest first: one read a whole list, enough to catch a mistake, and never a page of its own.
    public const int SpamConversationCount = 100;

    // The helper's whole-list answer: the contacts, and each request it made for them as counts, logged so the log says
    // which way of asking the phone honours (contacts_list.go). "1/350/50:50+50" is the request's three numbers, the rows
    // that came back and how many were new; "x" a refused request; "?3:24" a response field the schema does not name.
    public static (GoogleContact[] Contacts, string Tries) ReadContacts(System.Text.Json.JsonElement result, System.Text.Json.JsonSerializerOptions json)
    {
        if (result.ValueKind == System.Text.Json.JsonValueKind.Array) return (result.Deserialize<GoogleContact[]>(json) ?? [], "");
        var contacts = result.TryGetProperty("contacts", out var list) ? list.Deserialize<GoogleContact[]>(json) ?? [] : [];
        var tries = new List<string>();
        if (result.TryGetProperty("tries", out var asked) && asked.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var attempt in asked.EnumerateArray())
            {
                int Number(string name) => attempt.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : 0;
                var line = $"{Number("i1")}/{Number("i2")}/{Number("i3")}:";
                line += attempt.TryGetProperty("failed", out var failed) && failed.ValueKind == System.Text.Json.JsonValueKind.True
                    ? "x" : $"{Number("rows")}+{Number("fresh")}";
                if (attempt.TryGetProperty("unknown", out var unknown) && unknown.GetString() is { Length: > 0 } fields) line += "?" + fields;
                tries.Add(line);
            }
        return (contacts, string.Join(',', tries));
    }

    // The helper exposes expanding prefixes rather than an opaque cursor. Either folder filling the requested prefix
    // means there may be another page; requiring the inbox to fill it made an exhausted inbox hide older archived
    // conversations. `advanced` stops a provider that repeats the same prefix from producing an endless cursor.
    public static int? NextConversationCount(int requested, int inboxCount, int archiveCount, bool advanced)
    {
        if (!advanced || requested >= MaximumConversationCount || inboxCount < requested && archiveCount < requested) return null;
        return Math.Min(MaximumConversationCount, requested * 2);
    }

    // Folder reads can overlap while the phone is applying an archive change. The explicit archive result is
    // authoritative in that case; keeping the earlier inbox copy would put the conversation back in the main list
    // on the next app start. Hidden state wins over either folder so deleted/spam rows cannot leak into the inbox.
    // A whole list read moments ago on the same connection, for the same range, answers again. A resync any service's
    // connection starts reads every service, and launch read Google's two folders three times in six seconds (2026-10-04:
    // about 2.3 s of phone round trips each). The phone is asked again after ListReuse, on a new connection, or for more.
    public static readonly TimeSpan ListReuse = TimeSpan.FromSeconds(15);
    public static bool CanReuseList(bool olderPage, int listedConnection, int connection, int listedCount, int count, TimeSpan sinceListed) =>
        !olderPage && listedConnection == connection && listedCount == count && sinceListed >= TimeSpan.Zero && sinceListed < ListReuse;

    // Past ListReuse and within ListHeldAnswer the held list still answers at once, kept current by the connection's events, while the phone is read
    // again behind it (GoogleMessagesBackend.RefreshListBehind). Longer than that, or on another connection, the phone is read first as before.
    public static readonly TimeSpan ListHeldAnswer = TimeSpan.FromMinutes(30);
    public static bool CanAnswerHeld(bool olderPage, int listedConnection, int connection, int listedCount, int count, TimeSpan sinceListed) =>
        !olderPage && listedConnection == connection && listedCount == count && sinceListed >= TimeSpan.Zero && sinceListed < ListHeldAnswer;

    // That list as the realtime events since have left it: each conversation's current copy (events replace it, and remove
    // one deleted), and any conversation an event brought in meanwhile.
    public static GoogleChat[] HeldList(IReadOnlyCollection<string> listed, IReadOnlyDictionary<string, GoogleChat> current, ISet<string> knownBefore) =>
        [.. listed.Select(id => current.TryGetValue(id, out var chat) ? chat : null).OfType<GoogleChat>()
            .Concat(current.Values.Where(chat => !knownBefore.Contains(chat.Id)))
            .Where(chat => !chat.Hidden).DistinctBy(chat => chat.Id)];

    // 2026-10-26: Google's list leaves out a new group until the owner sends in it (an RCS group for 3 minutes and two
    // incoming messages, an MMS group for 14), though its events came, so it appeared and then left at the next refresh.
    // A conversation held here that no list has carried, no older than the list's oldest row, stays in it; one deleted,
    // spammed or blocked leaves by its own event.
    // 2026-10-26: someone's first message in an SMS group arrived in a conversation of its own while it downloaded (with a
    // "one-on-one SMS conversation created" marker) and moved to the group a second later, leaving that conversation empty;
    // shown as a thread with only the sender, and kept by KeepUnlisted. One no list has carried, with only markers left in
    // it, is that placeholder.
    public static bool IsLeftPlaceholder(string chatId, bool everListed, IEnumerable<GoogleMessage> held) =>
        !everListed && !held.Any(message => message.ChatId == chatId && !message.Status.Contains("TOMBSTONE", StringComparison.Ordinal));

    // What joined and left the phone's own list between two whole lists, as one-way tags and nothing said in them: for each
    // that joined, its place from the newest (0), whether it is a group, how many people, and what this server holds of it
    // (none, markers only, or a count of messages). At most five each way; the rest as a count. Empty when nothing changed.
    public static string ListChanges(IReadOnlyCollection<string> before, GoogleChat[] after, IEnumerable<GoogleMessage> held)
    {
        const int Shown = 5;
        var was = before.ToHashSet(StringComparer.Ordinal);
        var now = after.Select(chat => chat.Id).ToHashSet(StringComparer.Ordinal);
        var joined = after.Select((chat, place) => (chat, place)).Where(row => !was.Contains(row.chat.Id)).ToArray();
        var left = before.Where(id => !now.Contains(id)).ToArray();
        if (joined.Length == 0 && left.Length == 0) return "";
        var heldBy = held.GroupBy(message => message.ChatId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        string Held(string id) => !heldBy.TryGetValue(id, out var list) ? "none"
            : list.All(message => message.Status.Contains("TOMBSTONE", StringComparison.Ordinal)) ? "markers"
            : list.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string More(int count) => count > Shown ? $",+{count - Shown}" : "";
        var joinedWords = string.Join(",", joined.Take(Shown).Select(row =>
            $"{GoogleMessagesLog.Tag(row.chat.Id)}(place={row.place} group={(row.chat.Group ? "yes" : "no")} people={row.chat.Participants?.Length ?? 0} held={Held(row.chat.Id)})")) + More(joined.Length);
        var leftWords = string.Join(",", left.Take(Shown).Select(id => GoogleMessagesLog.Tag(id))) + More(left.Length);
        return $"joined={(joined.Length == 0 ? "none" : joinedWords)} left={(left.Length == 0 ? "none" : leftWords)}";
    }

    public static GoogleChat[] KeepUnlisted(GoogleChat[] list, IEnumerable<GoogleChat> held, IReadOnlySet<string> everListed)
    {
        if (list.Length == 0) return list;
        var oldest = list.Min(chat => chat.At);
        var listed = list.Select(chat => chat.Id).ToHashSet(StringComparer.Ordinal);
        var kept = held.Where(chat => !chat.Hidden && chat.At >= oldest && !listed.Contains(chat.Id) && !everListed.Contains(chat.Id)).ToArray();
        return kept.Length == 0 ? list : [.. kept.OrderByDescending(chat => chat.At), .. list];
    }

    // A conversation written on its own after a whole list was asked for (its read, an event, an archive, a delete) keeps that copy, or stays gone:
    // the list's is older for it. 2026-10-26: a list read behind a phone's opening, asked 1 s before that phone read a conversation and answered
    // 4 s after, put the unread copy back over the read one, and announced nothing, as it compared itself with a copy older still. 30 s later the
    // phone and the server's drift check both found it unread, the phone showed it bold, and the read went to Google a second time.
    public static GoogleChat[] KeepNewer(GoogleChat[] list, long asked, IReadOnlyDictionary<string, long> written, IReadOnlyDictionary<string, GoogleChat> current) =>
        [.. list.Select(chat => !written.TryGetValue(chat.Id, out var at) || at <= asked ? chat : current.GetValueOrDefault(chat.Id)).OfType<GoogleChat>()];

    public static GoogleChat[] MergeConversations(IEnumerable<GoogleChat> inbox, IEnumerable<GoogleChat> archived, IEnumerable<GoogleChat>? spam = null)
    {
        spam ??= [];
        var hidden = inbox.Concat(archived).Concat(spam).Where(chat => chat.Hidden).Select(chat => chat.Id).ToHashSet(StringComparer.Ordinal);
        var merged = new Dictionary<string, GoogleChat>(StringComparer.Ordinal);
        foreach (var chat in inbox.Where(chat => !chat.Hidden)) merged[chat.Id] = chat;
        foreach (var chat in archived.Where(chat => !chat.Hidden)) merged[chat.Id] = chat with { Archived = true };
        foreach (var chat in spam.Where(chat => !chat.Hidden)) merged[chat.Id] = chat with { Archived = false, Spam = true };
        return [.. merged.Values.Where(chat => !hidden.Contains(chat.Id))];
    }

    public static string? Phone(string? input)
    {
        var value = new StringBuilder();
        foreach (var ch in (input ?? "").Trim())
        {
            if (ch is >= '0' and <= '9' || ch == '+' && value.Length == 0) value.Append(ch);
            else if (ch is not (' ' or '-' or '(' or ')' or '.')) return null;
        }
        var phone = value.ToString();
        return phone.TrimStart('+').Length is >= 3 and <= 15 ? phone : null;
    }

    // Last ten digits: matches the same number written with or without a country code.
    public static string PhoneKey(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length > 10 ? digits[^10..] : digits;
    }

    // The newest writable one-to-one conversation whose other participant has this number (same last ten digits),
    // so New chat can open an existing thread without asking the phone.
    public static GoogleChat? FindDirect(IEnumerable<GoogleChat?> chats, string? phone)
    {
        if (Phone(phone) is not { } number) return null;
        var key = PhoneKey(number);
        return chats.Where(c => c is { Group: false, ReadOnly: false, Hidden: false } && c.Participants.Count(p => !p.Self) == 1 &&
                PhoneKey(c.Participants.First(p => !p.Self).Id ?? "") == key)
            .OrderByDescending(c => c!.At).FirstOrDefault();
    }

    // recent maps PhoneKey to the latest conversation activity; people you text often rank first.
    public static IReadOnlyList<ContactPerson> Find(IEnumerable<GoogleContact> source, string query, IReadOnlyDictionary<string, DateTimeOffset>? recent = null)
    {
        var phone = Phone(query);
        var terms = query.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var digits = new string(query.Where(char.IsDigit).ToArray());
        var result = source.Where(c => terms.All(term => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            digits.Length > 0 && query.All(ch=>char.IsDigit(ch) || " +()- .".Contains(ch)) && new string(c.Phone.Where(char.IsDigit).ToArray()).Contains(digits,StringComparison.Ordinal))
            .DistinctBy(c => c.Phone)
            .OrderByDescending(c => recent is not null && recent.TryGetValue(PhoneKey(c.Phone), out var at) ? at : DateTimeOffset.MinValue)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Take(50)
            .Select(c => new ContactPerson(GoogleModels.Account, c.Phone, c.Name, null, c.Phone, null, false)).ToList();
        // This is an explicitly entered number, not a claim that it is a saved contact or RCS-capable.
        if (phone is not null && !result.Any(c => c.Phone == phone))
            result.Insert(0, new(GoogleModels.Account, phone, phone, null, phone, null, false));
        return result;
    }

    /// <summary>The most people New chat offers as Frequent. The helper asks the phone for eight, as upstream does.</summary>
    public const int FrequentCount = 8;

    // The phone's own ranking of who this account writes to most (the helper's ui.topContacts), as people New chat can
    // offer. The phone's order is kept, because it is the ranking; a number is offered once however it was written, and
    // a row without a usable number is dropped, since a Google Messages chat is started by number.
    public static IReadOnlyList<ContactPerson> Frequent(IEnumerable<GoogleContact> ranked)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ContactPerson>();
        foreach (var contact in ranked)
        {
            if (Phone(contact.Phone) is not { } phone || !seen.Add(PhoneKey(phone))) continue;
            var name = string.IsNullOrWhiteSpace(contact.Name) ? phone : contact.Name;
            result.Add(new(GoogleModels.Account, phone, name, null, phone, null, false));
            if (result.Count == FrequentCount) break;
        }
        return result;
    }

    public static IReadOnlyList<SearchHit> Search(IEnumerable<GoogleChat> chats, IEnumerable<GoogleMessage> messages, SearchRequest request)
    {
        var lookup = chats.Where(c => !c.Hidden && MessageSearch.MatchesChat(GoogleModels.Map(c), request)).ToDictionary(c => c.Id);
        var query = request.Query.Trim();
        bool Matches(string? text) => MessageSearch.WordsMatch(text, query);
        if (request.Kind != SearchKind.Messages)
            return lookup.Values.Where(c => (request.Kind != SearchKind.Participants && Matches(c.Title + " Google Messages")) || c.Participants.Any(p => !p.Self && (Matches(p.Name) || Matches(p.Id))))
                .OrderByDescending(c => c.At).ThenBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => new SearchHit(GoogleModels.Map(c), null)).ToArray();
        return messages.Where(m => lookup.ContainsKey(m.ChatId))
            .Select(m => new SearchHit(GoogleModels.Map(lookup[m.ChatId]), GoogleModels.Map(m, lookup[m.ChatId])))
            .Where(h => MessageSearch.Matches(h.Chat, h.Message!, request))
            .OrderByDescending(h => h.Message!.Timestamp).ThenBy(h => h.Message!.Id, StringComparer.Ordinal).ToArray();
    }
}
