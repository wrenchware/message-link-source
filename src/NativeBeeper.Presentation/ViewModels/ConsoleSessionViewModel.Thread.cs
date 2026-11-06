using System.Globalization;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

// /thread: a conversation inspected from what this PC holds — the list's summary and the history kept in memory or on
// disk. Nothing is read from a service, and nothing is marked read.
public sealed partial class ConsoleSessionViewModel
{
    public const int MediaShown = 30;
    private static readonly string[] ThreadViews = ["info", "members", "media", "stats", "raw", "route"];

    private async Task ThreadAsync(string rest)
    {
        var words = rest.Trim();
        var view = "info";
        var space = words.IndexOf(' ');
        var first = space < 0 ? words : words[..space];
        if (ThreadViews.Contains(first, StringComparer.OrdinalIgnoreCase)) { view = first.ToLowerInvariant(); words = space < 0 ? "" : words[(space + 1)..].Trim(); }
        ChatTarget? target;
        if (words.Length > 0) { target = Named(words); if (target is null) return; }
        else
        {
            target = Target is { } to ? to.Target ?? TargetOf(to.SourceChatId) : null;
            target ??= OpenTarget() ?? Implied().Target;
            if (target is null) { Fail("Which conversation? /thread name, /thread N, or /thread . for the one open in Messages."); return; }
        }
        if (view == "route") { Trace(target, null); return; }
        if (view == "members") { Members(target); return; }
        if (view == "raw") { await RawAsync(target); return; }
        var held = await HeldAsync(target);
        switch (view)
        {
            case "media": Media(target, held); return;
            case "stats": Stats(target, held); return;
            default: Info(target, held); return;
        }
    }

    // Every member conversation's kept messages, oldest first, without repeats.
    private async Task<IReadOnlyList<ChatMessage>> HeldAsync(ChatTarget target)
    {
        var all = new Dictionary<(string, string), ChatMessage>();
        foreach (var member in target.Members)
        {
            HistorySnapshot? snapshot;
            try { snapshot = await host.SavedHistoryAsync(member.Id); }
            catch (Exception) { snapshot = null; }
            foreach (var message in snapshot?.Messages ?? []) all.TryAdd((message.ChatId, message.Id), message);
        }
        log($"console thread members={target.Members.Count.ToString(CultureInfo.InvariantCulture)} held={all.Count.ToString(CultureInfo.InvariantCulture)}");
        return [.. all.Values.Where(m => !m.IsHidden).OrderBy(m => m.Timestamp)];
    }

    private static ConsoleSpan InfoKey(string text) => new(text.PadRight(11), ConsoleTone.Dim);

    private void Info(ChatTarget target, IReadOnlyList<ChatMessage> held)
    {
        var now = clock.GetUtcNow();
        var family = target.Networks.Count == 1 ? BubbleColours.TagFamily(target.Networks[0], target.Members.FirstOrDefault()?.ExpectedTransport) : (BubbleFamily?)null;
        Out(ConsoleSpan.For(target.Title, family), new ConsoleSpan(target.Label != target.Title ? "  " + target.Label : "", ConsoleTone.Dim));
        Out(InfoKey("services"), new ConsoleSpan(string.Join(", ", target.Networks.Where(n => n.Length > 0).Distinct())),
            new ConsoleSpan(target.Members.Count > 1 ? $" · linked, {target.Members.Count.ToString(CultureInfo.CurrentCulture)} conversations" : "", ConsoleTone.Dim));
        var people = target.Members.Max(m => Math.Max(m.ParticipantCount, m.Participants.Count));
        Out(InfoKey("kind"), new ConsoleSpan((target.IsGroup ? "group" : "one-to-one") + (people > 0 ? $" · {people.ToString(CultureInfo.CurrentCulture)} people" : "")));
        var flags = new List<ConsoleSpan>();
        void Flag(string text, ConsoleTone tone = ConsoleTone.Default) { if (flags.Count > 0) flags.Add(new ConsoleSpan(" · ", ConsoleTone.Dim)); flags.Add(new ConsoleSpan(text, tone)); }
        var unread = target.Members.Sum(m => m.UnreadCount);
        if (unread > 0) Flag($"{unread.ToString(CultureInfo.CurrentCulture)} unread", ConsoleTone.Strong);
        if (target.Members.Any(m => m.IsMarkedUnread)) Flag("marked unread", ConsoleTone.Strong);
        if (target.Members.Any(m => m.IsPinned)) Flag("pinned");
        if (((IConsoleAppHost)this).IsMuted(target))
            Flag(target.Members.Select(m => m.MutedUntil).FirstOrDefault(u => u is not null) is { } until
                ? "muted until " + TimeZoneInfo.ConvertTime(until, clock.LocalTimeZone).ToString("t", CultureInfo.CurrentCulture) : "muted", ConsoleTone.Dim);
        if (target.Members.Any(m => m.IsArchived)) Flag("archived", ConsoleTone.Dim);
        if (target.Members.Select(m => m.SnoozedUntil).FirstOrDefault(u => u is not null && u > now) is { } snoozed)
            Flag("snoozed until " + TimeZoneInfo.ConvertTime(snoozed, clock.LocalTimeZone).ToString("t", CultureInfo.CurrentCulture), ConsoleTone.Dim);
        if (target.Members.Any(m => m.IsEncrypted == true)) Flag("end-to-end encrypted", ConsoleTone.Ok);
        if (target.Members.All(m => m.IsReadOnly)) Flag("read only", ConsoleTone.Warn);
        if (flags.Count == 0) Flag("nothing unread", ConsoleTone.Dim);
        Out([InfoKey("flags"), .. flags]);
        var readUpTo = target.Members.Select(m => m.ReadUpTo).Where(r => r is not null).Max();
        Out(InfoKey("activity"), new ConsoleSpan("last " + ConsoleCells.Age(target.LastActivity, now) + (target.LastActivity is null ? "" : " ago")),
            new ConsoleSpan(readUpTo is { } r ? " · read up to " + formatter.Time(r, formatter.LocalDate(r) != formatter.LocalDate(now)) : "", ConsoleTone.Dim));
        Out(InfoKey("held here"), new ConsoleSpan(held.Count == 0 ? "nothing kept on this PC yet" :
            $"{held.Count.ToString("N0", CultureInfo.CurrentCulture)} messages since {formatter.ShortDate(held[0].Timestamp)}" +
            $" · {held.Count(m => m.Attachments.Count > 0).ToString("N0", CultureInfo.CurrentCulture)} with media"), new ConsoleSpan(held.Count == 0 ? "" : " · /thread stats", ConsoleTone.Dim));
        ConsoleRoute route;
        lock (gate) route = sender.Route(target, null, NewestByDisplay);
        Out(InfoKey("sends by"), route is ConsoleRoute.To { Recipient: var to } ? ConsoleSpan.For(to.Service, BubbleColours.TagFamily(to.Network, null))
            : new ConsoleSpan("nowhere from here", ConsoleTone.Warn), new ConsoleSpan(" · /thread route explains", ConsoleTone.Dim));
        var caps = target.Members.Where(m => !m.IsCallHistory).Select(m => m.Capabilities).FirstOrDefault() ?? ChatCapabilities.Unknown;
        static ConsoleSpan Can(string what, bool? can) => new(what + (can switch { true => " ✓  ", false => " ✗  ", _ => " ?  " }),
            can switch { true => ConsoleTone.Ok, false => ConsoleTone.Dim, _ => ConsoleTone.Dim });
        Out(InfoKey("can"), Can("reply", caps.Reply > 0), Can("edit", caps.Edit > 0), Can("react", caps.Reaction > 0), Can("unsend", caps.Delete > 0),
            Can("receipts", caps.ReadReceipts), Can("typing", caps.TypingNotifications),
            new ConsoleSpan(caps.MaxTextLength is { } max ? "up to " + max.ToString("N0", CultureInfo.CurrentCulture) + " chars" : "", ConsoleTone.Dim));
        if (target.Members.Count > 1)
            for (var i = 0; i < target.Members.Count; i++)
            {
                var member = target.Members[i];
                Out(new ConsoleSpan(i == target.Members.Count - 1 ? "  └ " : "  ├ ", ConsoleTone.Dim),
                    ConsoleSpan.For(ConsoleCells.Pad(ConsoleLineFormatter.Tag(member.Network), 10), BubbleColours.TagFamily(member.Network, member.ExpectedTransport)),
                    new ConsoleSpan(ConsoleCells.Pad(Tag(member.Id), 20)), new ConsoleSpan((member.UnreadCount > 0 ? $"unread {member.UnreadCount}  " : "") +
                        "last " + ConsoleCells.Age(member.LastActivity, now) + (member.ExpectedTransport is { Length: > 0 } t ? "  " + t.ToUpperInvariant() : ""), ConsoleTone.Dim));
            }
    }

    private void Members(ChatTarget target)
    {
        var people = target.Members.SelectMany(m => m.Participants.Select(p => (Member: m, Person: p)))
            .GroupBy(x => x.Person.DisplayName + "\n" + x.Member.Network).Select(g => g.First()).ToArray();
        if (people.Length == 0) { Out("This PC doesn't know who is in it.", ConsoleTone.Dim); return; }
        Out(new ConsoleSpan(target.Title, ConsoleTone.Strong), new ConsoleSpan($" · {people.Length.ToString(CultureInfo.CurrentCulture)} people", ConsoleTone.Dim));
        foreach (var (member, person) in people.OrderByDescending(x => x.Person.IsSelf == true).ThenBy(x => x.Person.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            Out(new ConsoleSpan("  "), ConsoleSpan.For(ConsoleCells.Pad(ConsoleLineFormatter.Tag(member.Network), 10), BubbleColours.TagFamily(member.Network, null)),
                new ConsoleSpan(string.IsNullOrWhiteSpace(person.DisplayName) ? "Someone" : person.DisplayName, person.IsSelf == true ? ConsoleTone.Accent : ConsoleTone.Default),
                new ConsoleSpan((person.Username is { Length: > 0 } handle ? "  @" + handle : "") + (person.IsSelf == true ? "  (you)" : ""), ConsoleTone.Dim));
    }

    private static string KindOf(MessageAttachment attachment) =>
        attachment.IsVoiceNote ? "voice" : attachment.IsSticker ? "sticker" : attachment.IsGif ? "gif" : attachment.Link is not null && attachment.Play ? "reel"
        : attachment.Type.ToLowerInvariant() switch { "img" or "image" or "photo" => "photo", "video" => "video", "audio" => "audio", "link" => "link", var t => t.Length == 0 ? "file" : t };

    private void Media(ChatTarget target, IReadOnlyList<ChatMessage> held)
    {
        var items = held.SelectMany(m => m.Attachments.Select(a => (Message: m, Attachment: a))).Reverse().ToArray();
        if (items.Length == 0) { Out($"No photos, files or voice notes kept here for {target.Title}.", ConsoleTone.Dim); return; }
        var shown = Capturing ? items : items.Take(MediaShown).ToArray();
        var table = new ConsoleTable(new("#", 3, Right: true), new("DATE", 0), new("FROM", 0), new("KIND", 7), new("SIZE", 6, Right: true), new("NAME", 0, Flex: true));
        for (var i = 0; i < shown.Length; i++)
        {
            var (message, attachment) = shown[i];
            table.Add(new((i + 1).ToString(CultureInfo.InvariantCulture), ConsoleTone.Accent), new(formatter.ShortDate(message.Timestamp), ConsoleTone.Dim),
                new(message.IsOutgoing ? "You" : message.SenderName ?? target.Title), new(KindOf(attachment), ConsoleTone.Strong),
                new(attachment.Size is { } size ? ConsoleCells.Bytes(size) : "—", ConsoleTone.Dim),
                new(attachment.Name ?? attachment.Caption ?? attachment.Link ?? attachment.MimeType ?? "", ConsoleTone.Dim));
        }
        Rows(table.Render(Columns));
        var kinds = items.GroupBy(x => KindOf(x.Attachment)).OrderByDescending(g => g.Count()).Select(g => $"{g.Count().ToString(CultureInfo.CurrentCulture)} {g.Key}");
        Out($"{items.Length.ToString(CultureInfo.CurrentCulture)} kept here: {string.Join(", ", kinds)}" +
            (shown.Length < items.Length ? $" · newest {shown.Length.ToString(CultureInfo.CurrentCulture)} shown (| less shows all)" : ""), ConsoleTone.Dim);
    }

    private void Stats(ChatTarget target, IReadOnlyList<ChatMessage> held)
    {
        if (held.Count == 0) { Out($"Nothing kept here for {target.Title} yet. Opening it in Messages keeps its history.", ConsoleTone.Dim); return; }
        var span = held[^1].Timestamp - held[0].Timestamp;
        Out(new ConsoleSpan(target.Title, ConsoleTone.Strong), new ConsoleSpan($" · {held.Count.ToString("N0", CultureInfo.CurrentCulture)} messages over {ConsoleCells.Duration(span)} " +
            $"({formatter.ShortDate(held[0].Timestamp)} – {formatter.ShortDate(held[^1].Timestamp)})", ConsoleTone.Dim));
        Blank();
        // Who writes: a bar each, longest first.
        var senders = held.GroupBy(m => m.IsOutgoing ? "You" : target.IsGroup ? (m.SenderName ?? "Someone") : target.Title)
            .Select(g => (Name: g.Key, Count: g.Count())).OrderByDescending(s => s.Count).Take(8).ToArray();
        var nameWidth = Math.Min(18, senders.Max(s => ConsoleCells.Width(s.Name)));
        var barWidth = Math.Clamp(Columns - nameWidth - 18, 10, 40);
        Out("WHO WRITES", ConsoleTone.Accent);
        foreach (var (name, count) in senders)
        {
            var (filled, empty) = ConsoleCells.Bar(count / (double)held.Count, barWidth);
            Out(new ConsoleSpan("  " + ConsoleCells.Pad(name, nameWidth) + "  ", name == "You" ? ConsoleTone.Accent : ConsoleTone.Default),
                new ConsoleSpan(filled, name == "You" ? ConsoleTone.Accent : ConsoleTone.Ok), new ConsoleSpan(empty, ConsoleTone.Dim),
                new ConsoleSpan($" {count.ToString("N0", CultureInfo.CurrentCulture),6} {count * 100 / held.Count,3}%", ConsoleTone.Dim));
        }
        Blank();
        // When: by hour of the day and by day of the week, in local time.
        var hours = new double[24];
        var days = new double[7];
        foreach (var message in held)
        {
            var local = TimeZoneInfo.ConvertTime(message.Timestamp, clock.LocalTimeZone);
            hours[local.Hour]++;
            days[((int)local.DayOfWeek + 6) % 7]++;
        }
        Out("WHEN", ConsoleTone.Accent);
        Out(new ConsoleSpan("  hour  ", ConsoleTone.Dim), new ConsoleSpan(string.Concat(ConsoleCells.Spark(hours).Select(c => c + " ")), ConsoleTone.Ok),
            new ConsoleSpan($" busiest {Array.IndexOf(hours, hours.Max()):00}:00", ConsoleTone.Dim));
        Out(new ConsoleSpan("        ", ConsoleTone.Dim), new ConsoleSpan("0     3     6     9     12    15    18    21", ConsoleTone.Dim));
        var dayNames = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        var dayBar = Math.Clamp(Columns - 24, 10, 30);
        for (var d = 0; d < 7; d++)
        {
            var (filled, empty) = ConsoleCells.Bar(days.Max() <= 0 ? 0 : days[d] / days.Max(), dayBar);
            Out(new ConsoleSpan("  " + ConsoleCells.Pad(dayNames[(d + 1) % 7], 5) + " ", ConsoleTone.Dim), new ConsoleSpan(filled, ConsoleTone.Ok), new ConsoleSpan(empty, ConsoleTone.Dim),
                new ConsoleSpan($" {days[d].ToString(CultureInfo.CurrentCulture),5}", ConsoleTone.Dim));
        }
        Blank();
        // How quickly each side answers: the median wait from the other side's last message to the first reply, within a day.
        var yours = new List<TimeSpan>();
        var theirs = new List<TimeSpan>();
        for (var i = 1; i < held.Count; i++)
        {
            if (held[i].IsOutgoing == held[i - 1].IsOutgoing) continue;
            var wait = held[i].Timestamp - held[i - 1].Timestamp;
            if (wait <= TimeSpan.Zero || wait > TimeSpan.FromDays(1)) continue;
            (held[i].IsOutgoing ? yours : theirs).Add(wait);
        }
        static string Median(List<TimeSpan> waits) => waits.Count == 0 ? "—" : ConsoleCells.Age(waits.Order().ElementAt(waits.Count / 2));
        Out("REPLIES", ConsoleTone.Accent);
        Out(new ConsoleSpan("  you answer in ", ConsoleTone.Dim), new ConsoleSpan(Median(yours), ConsoleTone.Strong),
            new ConsoleSpan(" · they answer in ", ConsoleTone.Dim), new ConsoleSpan(Median(theirs), ConsoleTone.Strong), new ConsoleSpan(" (medians, within a day)", ConsoleTone.Dim));
        var media = held.Count(m => m.Attachments.Count > 0);
        var reactions = held.Sum(m => m.Reactions.Count);
        var edits = held.Count(m => m.EditedAt is not null);
        Out(new ConsoleSpan("  ", ConsoleTone.Dim), new ConsoleSpan($"{media.ToString("N0", CultureInfo.CurrentCulture)} with media · {reactions.ToString("N0", CultureInfo.CurrentCulture)} reactions · " +
            $"{edits.ToString("N0", CultureInfo.CurrentCulture)} edited · {held.Count(m => m.ReplyToId is not null).ToString("N0", CultureInfo.CurrentCulture)} replies to a message", ConsoleTone.Dim));
        var busiest = held.GroupBy(m => formatter.LocalDate(m.Timestamp)).OrderByDescending(g => g.Count()).First();
        Out(new ConsoleSpan("  busiest day ", ConsoleTone.Dim), new ConsoleSpan(busiest.Key.ToString("d MMM yyyy", CultureInfo.CurrentCulture), ConsoleTone.Strong),
            new ConsoleSpan($" with {busiest.Count().ToString(CultureInfo.CurrentCulture)}", ConsoleTone.Dim));
    }

    private async Task RawAsync(ChatTarget target)
    {
        void Field(string name, string value) => Out(new ConsoleSpan(ConsoleCells.Pad(name, 18), ConsoleTone.Dim), new ConsoleSpan(value));
        Field("display_id", target.DisplayId);
        Field("title", target.Title);
        Field("label", target.Label);
        Field("is_group", target.IsGroup ? "true" : "false");
        Field("can_write", target.CanWrite ? "true" : "false");
        Field("networks", string.Join(", ", target.Networks));
        foreach (var member in target.Members)
        {
            Blank();
            Out(new ConsoleSpan("[" + Tag(member.Id) + "]", ConsoleTone.Accent));
            Field("id", member.Id);
            Field("account_id", member.AccountId.Length == 0 ? "—" : member.AccountId);
            Field("network", member.Network);
            Field("type", member.Type);
            Field("transport", member.ExpectedTransport ?? "—");
            Field("unread", member.UnreadCount.ToString(CultureInfo.InvariantCulture) + (member.IsMarkedUnread ? " (marked)" : ""));
            Field("flags", string.Join(" ", new[] { member.IsPinned ? "pinned" : "", member.IsMuted ? "muted" : "", member.IsArchived ? "archived" : "",
                member.IsReadOnly ? "read-only" : "", member.IsSpam ? "spam" : "", member.IsCallHistory ? "call-history" : "" }.Where(f => f.Length > 0)) is { Length: > 0 } f ? f : "—");
            Field("encrypted", member.IsEncrypted switch { true => "true", false => "false", _ => "unknown" });
            Field("participants", member.ParticipantCount.ToString(CultureInfo.InvariantCulture));
            Field("last_activity", member.LastActivity?.ToString("u", CultureInfo.InvariantCulture) ?? "—");
            Field("read_up_to", member.ReadUpTo?.ToString("u", CultureInfo.InvariantCulture) ?? "—");
            var caps = member.Capabilities;
            Field("capabilities", $"reply={N(caps.Reply)} edit={N(caps.Edit)} delete={N(caps.Delete)} reaction={N(caps.Reaction)} receipts={B(caps.ReadReceipts)} typing={B(caps.TypingNotifications)} max_text={N(caps.MaxTextLength)}");
            HistorySnapshot? snapshot = null;
            try { snapshot = await host.SavedHistoryAsync(member.Id); } catch (Exception) { }
            Field("held", snapshot is null ? "nothing" : $"{snapshot.Messages.Count} messages · has_more={B(snapshot.HasMore)} at_limit={B(snapshot.AtLimit)} needs_read={B(snapshot.NeedsRead)}");
            if (snapshot?.Messages.LastOrDefault() is { } newest) Field("newest", $"{newest.Id} · sort={newest.SortKey} · {newest.Timestamp.ToString("u", CultureInfo.InvariantCulture)}");
        }
        static string N(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "?";
        static string B(bool? v) => v switch { true => "true", false => "false", _ => "?" };
    }
}
