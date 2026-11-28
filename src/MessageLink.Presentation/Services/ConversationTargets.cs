using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

/// <summary>
/// A conversation somebody can be addressed by name through: one row of the conversation list, a merged person
/// being one row with every conversation it holds as a member.
/// </summary>
/// <param name="DisplayId">The row's own id; for a merged person the merged id, which is never a send target.</param>
/// <param name="Label">The title, or the title and its service where another entry shares the title.</param>
/// <param name="CanWrite">Whether a text can go anywhere from here. Read-only rows and Voice call records with no
/// text conversation cannot be written to, whatever they are called.</param>
public sealed record ChatTarget(string DisplayId, string Title, string Label, bool IsGroup, IReadOnlyList<ChatSummary> Members,
    IReadOnlyList<string> Networks, DateTimeOffset? LastActivity, bool CanWrite);

public abstract record TargetMatch
{
    public sealed record Unique(ChatTarget Target) : TargetMatch;
    /// <summary>The best candidates in match order, at most five.</summary>
    public sealed record Ambiguous(IReadOnlyList<ChatTarget> Top) : TargetMatch;
    public sealed record None : TargetMatch;
}

/// <summary>
/// The conversation a line starts with and the rest of the line. A record rather than a tuple because C# does not allow
/// a tuple element named Rest; it deconstructs the same way: <c>var (target, rest, ambiguous) = targets.Lead(line);</c>
/// </summary>
public sealed record TargetLead(ChatTarget? Target, string Rest, IReadOnlyList<ChatTarget> Ambiguous)
{
    /// <summary>The words taken as the name, as typed (without quotes or @service); empty when none were.</summary>
    public string Name { get; init; } = "";
    /// <summary>The service word written straight after the name ("Alex Smith @fb hi"), or null.</summary>
    public string? Service { get; init; }
}

public abstract record RouteResult
{
    /// <summary>The source conversation a text goes through: a provider chat id, never a merged one.</summary>
    public sealed record To(ChatSummary Source) : RouteResult;
    /// <summary>More than one service could carry it and nothing says which; each entry is a route label that can be
    /// passed back as the explicit service.</summary>
    public sealed record NeedsService(IReadOnlyList<string> Services) : RouteResult;
    public sealed record NoTextRoute : RouteResult;
}

/// <summary>
/// Turns a typed name into a conversation, from what this PC already holds and nothing else: the loaded conversation
/// list and the merges. It never asks a provider, a directory or a remote search, so resolving a name costs nothing on
/// any account however often it is done. Ranks the way New chat does (<see cref="NameMatch"/>) and always gives the
/// same order for the same input, so Tab cycles without reshuffling.
/// </summary>
public sealed class ConversationTargets : IDisposable
{
    public const int AmbiguousShown = 5;
    private const string LinkedLabel = "Linked";
    private const string Separator = " · ";

    private readonly Func<IReadOnlyList<ChatSummary>> loaded;
    private readonly MergedConversations merges;
    private readonly Func<ChatSummary, ChatSummary?>? voiceTextRoute;
    private readonly object gate = new();
    private int mergesVersion;
    private ChatSummary[]? builtFrom;
    private int builtVersion = -1;
    private Entry[] built = [];
    private bool disposed;

    // One entry and everything it can be matched by, worked out once per build rather than on every keystroke. The
    // first NameKeys keys are its title and label; the rest are the people in it.
    private sealed record Entry(ChatTarget Target, ChatSummary Row, (string Name, string? Phone)[] Keys, int NameKeys);

    public ConversationTargets(Func<IReadOnlyList<ChatSummary>> loaded, MergedConversations merges,
        Func<ChatSummary, ChatSummary?>? voiceTextRoute = null)
    {
        this.loaded = loaded;
        this.merges = merges;
        this.voiceTextRoute = voiceTextRoute;
        merges.Changed += Merges_Changed;
    }

    private void Merges_Changed(object? sender, EventArgs args) => Interlocked.Increment(ref mergesVersion);

    /// <summary>Stops following the merges, which outlive the console this was made for.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        merges.Changed -= Merges_Changed;
    }

    /// <summary>Every conversation that can be named, in list order. Rebuilt only when a row of the loaded list or the
    /// merges change.</summary>
    public IReadOnlyList<ChatTarget> Entries() => [.. Built().Select(entry => entry.Target)];

    /// <summary>The conversations in Spam, which <see cref="Entries"/> and every name leave out: for /ls spam and /notspam alone.</summary>
    public IReadOnlyList<ChatTarget> Spam() =>
        [.. loaded().Where(row => row.IsSpam && row.MergedIntoChatId is null && !MergedConversations.IsMerged(row.Id) && !string.IsNullOrWhiteSpace(row.Title))
            .Select(row => Target(row, [row], row.Title.Trim(), row.Title.Trim()))];

    private Entry[] Built()
    {
        var source = loaded();
        var version = Volatile.Read(ref mergesVersion);
        lock (gate)
        {
            // The list hands out a fresh copy each time it is asked and replaces a row with a new record whenever that
            // row changes, so the same rows by reference are the same list, whatever array holds them.
            if (version == builtVersion && Same(source, builtFrom)) return built;
            built = Build(source);
            builtFrom = [.. source]; builtVersion = version;
            return built;
        }
    }

    private static bool Same(IReadOnlyList<ChatSummary> source, ChatSummary[]? before)
    {
        if (before is null || before.Length != source.Count) return false;
        for (var index = 0; index < before.Length; index++)
            if (!ReferenceEquals(source[index], before[index])) return false;
        return true;
    }

    private Entry[] Build(IReadOnlyList<ChatSummary> source)
    {
        var rows = merges.Project(source);
        var drafts = new List<(ChatSummary Row, IReadOnlyList<ChatSummary> Members, string Title)>();
        foreach (var row in rows)
        {
            // Spam is kept out of every view but Spam; a merged-away row is its merged person; the new-chat draft is
            // not a conversation yet.
            if (row.IsSpam || row.MergedIntoChatId is not null || row.Id.StartsWith("draft:", StringComparison.Ordinal)) continue;
            IReadOnlyList<ChatSummary> members = MergedConversations.IsMerged(row.Id) ? merges.Members(row.Id) : [row];
            if (members.Count == 0) continue;
            var title = !string.IsNullOrWhiteSpace(row.Title) ? row.Title.Trim()
                : members.SelectMany(m => m.Participants).FirstOrDefault(p => p.IsSelf != true && !string.IsNullOrWhiteSpace(p.DisplayName))?.DisplayName.Trim();
            if (string.IsNullOrEmpty(title)) continue;
            drafts.Add((row, members, title));
        }
        var shared = drafts.GroupBy(d => d.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entries = new Entry[drafts.Count];
        for (var i = 0; i < drafts.Count; i++)
        {
            var (row, members, title) = drafts[i];
            var merged = MergedConversations.IsMerged(row.Id);
            var label = !shared.Contains(title) ? title
                : title + Separator + (merged ? LinkedLabel : merges.ServiceRouteLabel(row));
            var target = Target(row, members, title, label);
            var keys = new List<(string, string?)> { (title, null) };
            if (label != title) keys.Add((label, null));
            var nameKeys = keys.Count;
            foreach (var person in members.SelectMany(m => m.Participants).Where(p => p.IsSelf != true))
                keys.Add((person.DisplayName ?? "", person.Phone));
            // Distinct keeps first occurrences in order, so the title and label stay at the front.
            entries[i] = new(target, row, [.. keys.Distinct()], nameKeys);
        }
        return entries;
    }

    private ChatTarget Target(ChatSummary row, IReadOnlyList<ChatSummary> members, string title, string label) =>
        new(row.Id, title, label, row.Type == "group", members, [.. members.Select(m => m.Network).Distinct(StringComparer.Ordinal)],
            row.LastActivity, CanWrite(row, members));

    private bool CanWrite(ChatSummary row, IReadOnlyList<ChatSummary> members)
    {
        if (MergedConversations.IsMerged(row.Id)) return members.Any(Writable);
        if (row.IsReadOnly) return false;
        return !MergedConversations.IsCall(row) || voiceTextRoute?.Invoke(row) is not null;
    }

    // The same test every send route in the app uses: not a call record, not read-only.
    private static bool Writable(ChatSummary chat) => !MergedConversations.IsCall(chat) && !chat.IsReadOnly;

    /// <summary>
    /// The conversation a name means. <paramref name="toSend"/> is for a name a text is about to go to at once: then a
    /// single match is taken on its own only as a whole label or title, or as the whole name or number of the person a
    /// one-to-one conversation is with. One that only begins or contains a name, or a group matched through somebody
    /// in it, comes back as <see cref="TargetMatch.Ambiguous"/>, so it is offered rather than written to.
    /// </summary>
    public TargetMatch Resolve(string name, string? service, bool toSend = false)
    {
        var text = Unquote(name);
        if (text.Length == 0) return new TargetMatch.None();
        var pool = Pool(service);
        // A label is how the console prints an entry and how Tab completes it, so it is always taken back exactly.
        var labelled = pool.Where(e => string.Equals(e.Target.Label, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (labelled.Length == 1) return new TargetMatch.Unique(labelled[0].Target);
        var titled = pool.Where(e => string.Equals(e.Target.Title, text, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (titled.Length == 1) return new TargetMatch.Unique(titled[0].Target);
        var ranked = Ranked(pool, text);
        if (ranked.Count == 0) return new TargetMatch.None();
        if (ranked.Count == 1 && (!toSend || WholeName(ranked[0].Entry, text))) return new TargetMatch.Unique(ranked[0].Entry.Target);
        return new TargetMatch.Ambiguous([.. ranked.Take(AmbiguousShown).Select(r => r.Entry.Target)]);
    }

    // The text is the whole of the entry's title or label or, outside a group, the whole name or number of the person
    // it is with. A group is never picked through somebody in it.
    private static bool WholeName(Entry entry, string text)
    {
        for (var index = 0; index < entry.Keys.Length; index++)
        {
            if (index >= entry.NameKeys && entry.Target.IsGroup) break;
            var (name, phone) = entry.Keys[index];
            if (NameMatch.Rank(name, phone, text) == NewChatRank.ExactLocal) return true;
        }
        return false;
    }

    /// <summary>
    /// The conversation a line starts with, for "/w Alex Smith hey": the longest title or label that is a whole-word
    /// prefix of the line, a whole title winning over a label of the same length, or a quoted name. A service written
    /// straight after the name ("Alex @ig hi", "Alex Smith@fb hi") narrows it and is not part of the rest. Returns the
    /// rest of the line after them.
    /// With no title or label at the start, the first word is only a candidate: when text follows it, it is the target
    /// only as a whole name (<see cref="Resolve"/> with toSend); otherwise whoever it could mean comes back as
    /// <see cref="TargetLead.Ambiguous"/>, so a text never goes to a name that merely begins with what was typed.
    /// </summary>
    public TargetLead Lead(string input)
    {
        var line = input.TrimStart();
        if (line.Length == 0) return new TargetLead(null, "", []);
        if (line[0] == '"')
        {
            var close = line.IndexOf('"', 1);
            var quoted = (close < 0 ? line[1..] : line[1..close]).Trim();
            var after = close < 0 ? "" : line[(close + 1)..];
            var named = TakeService(ref after);
            return Led(Resolve(quoted, named, toSend: after.Length > 0), quoted, after, named, line);
        }
        var entries = Built();
        var bestLength = 0; var bestIsTitle = false;
        var best = new List<Entry>();
        foreach (var entry in entries)
        {
            foreach (var (text, isTitle) in new[] { (entry.Target.Title, true), (entry.Target.Label, false) })
            {
                if (!WordPrefix(line, text)) continue;
                if (text.Length > bestLength || text.Length == bestLength && isTitle && !bestIsTitle)
                { bestLength = text.Length; bestIsTitle = isTitle; best.Clear(); best.Add(entry); }
                else if (text.Length == bestLength && isTitle == bestIsTitle && !best.Contains(entry)) best.Add(entry);
            }
        }
        if (best.Count > 0)
        {
            var name = line[..bestLength];
            var rest = line[bestLength..];
            var service = TakeService(ref rest);
            IReadOnlyList<Entry> chosen = best;
            if (service is not null)
            {
                // The same entries narrowed to that service, a merged person to its member there.
                var ids = best.Select(e => e.Target.DisplayId).ToHashSet(StringComparer.Ordinal);
                chosen = [.. Pool(service).Where(e => ids.Contains(e.Target.DisplayId))];
                if (chosen.Count == 0) return new TargetLead(null, line, []) { Name = name, Service = service };
            }
            if (chosen.Count == 1) return new TargetLead(chosen[0].Target, rest, []) { Name = name, Service = service };
            return new TargetLead(null, rest, [.. Order(chosen.Select(e => (e, NewChatRank.ExactLocal))).Take(AmbiguousShown).Select(r => r.Entry.Target)])
            { Name = name, Service = service };
        }
        var stop = 0;
        while (stop < line.Length && !char.IsWhiteSpace(line[stop]) && !ServiceAt(line, stop)) stop++;
        var word = line[..stop];
        var tail = line[stop..];
        var wordService = TakeService(ref tail);
        return Led(Resolve(word, wordService, toSend: tail.Length > 0), word, tail, wordService, line);
    }

    private static TargetLead Led(TargetMatch match, string name, string rest, string? service, string line) => match switch
    {
        TargetMatch.Unique unique => new TargetLead(unique.Target, rest, []) { Name = name, Service = service },
        TargetMatch.Ambiguous ambiguous => new TargetLead(null, rest, ambiguous.Top) { Name = name, Service = service },
        _ => new TargetLead(null, line, []) { Name = name, Service = service }
    };

    // "@ig" (or "@voice:work") at this point of the line and ending at a space or the end: a service after a name.
    private static bool ServiceAt(string line, int index)
    {
        if (index >= line.Length || line[index] != '@') return false;
        var stop = index + 1;
        while (stop < line.Length && !char.IsWhiteSpace(line[stop])) stop++;
        return ServiceOf(line[(index + 1)..stop]) is not null;
    }

    // Takes a service word from the start of what follows a name ("@fb hi" or " @fb hi"); either way the rest loses
    // its leading space.
    private static string? TakeService(ref string rest)
    {
        var text = rest.TrimStart();
        if (!ServiceAt(text, 0)) { rest = text; return null; }
        var stop = 1;
        while (stop < text.Length && !char.IsWhiteSpace(text[stop])) stop++;
        rest = text[stop..].TrimStart();
        return text[1..stop];
    }

    /// <summary>Labels for Tab to cycle through, in match order; a label with a space is quoted so it reads back whole.</summary>
    public IReadOnlyList<string> Complete(string prefix, string? service)
    {
        var text = Unquote(prefix);
        var pool = Pool(service);
        var ordered = text.Length == 0 ? Order(pool.Select(e => (e, NewChatRank.ExactLocal))) : Ranked(pool, text);
        return [.. ordered.Select(r => Quote(r.Entry.Target.Label))];
    }

    /// <summary>
    /// Which source conversation a text to this target goes through. Reads the merge's saved sending choice and never
    /// changes it: a choice the console made on its own would outlive the line it was made for.
    /// </summary>
    public RouteResult Route(ChatTarget target, string? explicitService, Func<string, ChatMessage?> newestIncomingSeen)
    {
        var members = target.Members;
        if (!MergedConversations.IsMerged(target.DisplayId))
        {
            if (members.Count == 0) return new RouteResult.NoTextRoute();
            var chat = members[0];
            if (explicitService is not null && !OnService(chat, ParseService(explicitService)))
                return new RouteResult.NeedsService([merges.ServiceRouteLabel(chat)]);
            if (chat.IsReadOnly) return new RouteResult.NoTextRoute();
            if (!MergedConversations.IsCall(chat)) return new RouteResult.To(chat);
            return voiceTextRoute?.Invoke(chat) is { } text && !MergedConversations.IsMerged(text.Id)
                ? new RouteResult.To(text) : new RouteResult.NoTextRoute();
        }
        var writable = members.Where(m => Writable(m) && !MergedConversations.IsMerged(m.Id)).ToArray();
        if (writable.Length == 0) return new RouteResult.NoTextRoute();
        var services = ServiceLabels(writable);
        if (explicitService is not null)
        {
            var filter = ParseService(explicitService);
            var chosen = writable.Where(m => OnService(m, filter)).ToArray();
            if (chosen.Length == 0) return new RouteResult.NeedsService(services);
            if (chosen.Length == 1) return new RouteResult.To(chosen[0]);
            // Two accounts on the one service asked for: the rules below choose between them, or ask.
            writable = chosen;
            services = ServiceLabels(chosen);
        }
        // "Always send with" holds here as in the composer; a manual choice the next incoming message has not ended comes next.
        // A pin whose service has stopped and waits on the user is passed over, with the rest of that service, until it is
        // back (MergedConversations.NeedsSignIn); with nothing else to send from it stays the route.
        if (merges.Find(target.DisplayId)?.PinnedRoute is { } pin && writable.FirstOrDefault(m => m.Id == pin.ChatId) is { } pinned)
        {
            if (!merges.NeedsSignIn(pinned)) return new RouteResult.To(pinned);
            var others = writable.Where(m => !merges.NeedsSignIn(m)).ToArray();
            if (others.Length == 0) return new RouteResult.To(pinned);
            writable = others;
            services = ServiceLabels(writable);
        }
        if (merges.Find(target.DisplayId)?.SendingRoute is { } manual && writable.FirstOrDefault(m => m.Id == manual.ChatId) is { } picked)
            return new RouteResult.To(picked);
        if (newestIncomingSeen(target.DisplayId) is { IsOutgoing: false } seen && writable.FirstOrDefault(m => m.Id == seen.ChatId) is { } answered)
            return new RouteResult.To(answered);
        // A list preview settles it only when it is provably the person's newest incoming message: no other member has a
        // line of the user's own (or anything else) newer than it, behind which an incoming one could sit. Otherwise it
        // asks, rather than guess away from where the merged conversation's own composer would send.
        if (writable.Where(m => Incoming(m.Preview)).OrderByDescending(m => m.Preview!.Timestamp)
                .ThenBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault() is { } latest &&
            !writable.Any(m => !Incoming(m.Preview) && (m.Preview?.Timestamp ?? m.LastActivity ?? DateTimeOffset.MinValue) > latest.Preview!.Timestamp))
            return new RouteResult.To(latest);
        return writable.Length == 1 ? new RouteResult.To(writable[0]) : new RouteResult.NeedsService(services);
    }

    // What the merged composer counts as the person writing: not the user's own, a deletion, a hidden or a system line.
    private static bool Incoming(ChatMessage? message) =>
        message is { IsOutgoing: false, IsDeleted: false, IsHidden: false } && message.Type != "system";

    /// <summary>
    /// The service a word names: google, sms or rcs; messenger or fb; instagram or ig; voice; hermes; or voice:label for one
    /// Voice account. A leading @ is allowed. Returns the network, or for one Voice account its route label
    /// ("Google Voice · label"); null when the word names no service.
    /// </summary>
    public static string? ServiceOf(string word)
    {
        var value = word.Trim();
        if (value.StartsWith('@')) value = value[1..];
        if (value.StartsWith("voice:", StringComparison.OrdinalIgnoreCase))
        {
            var label = Unquote(value["voice:".Length..]);
            return label.Length == 0 ? null : "Google Voice" + Separator + label;
        }
        return value.ToLowerInvariant() switch
        {
            "google" or "sms" or "rcs" => "Google Messages",
            "messenger" or "fb" => "Messenger",
            "instagram" or "ig" => "Instagram",
            "voice" => "Google Voice",
            "hermes" => "Hermes",
            _ => null
        };
    }

    private static readonly string[] Networks = ["Google Messages", "Messenger", "Instagram", "Google Voice", "Hermes"];

    // A service as given: a service word, a network's own name, or a route label ("Google Voice · Work"), which is what
    // NeedsService offers and so what may come back. Anything else is a service nothing is on.
    private readonly record struct ServiceFilter(string? Network, string? Account);
    private static ServiceFilter ParseService(string service)
    {
        var value = ServiceOf(service) ?? service.Trim();
        foreach (var network in Networks)
        {
            if (string.Equals(value, network, StringComparison.OrdinalIgnoreCase)) return new(network, null);
            if (value.StartsWith(network + Separator, StringComparison.OrdinalIgnoreCase) && value.Length > network.Length + Separator.Length)
                return new(network, value[(network.Length + Separator.Length)..].Trim());
        }
        return new(null, null);
    }

    private bool OnService(ChatSummary chat, ServiceFilter filter)
    {
        if (filter.Network is null || !string.Equals(chat.Network, filter.Network, StringComparison.Ordinal)) return false;
        if (filter.Account is not { } wanted) return true;
        var route = merges.ServiceRouteLabel(chat);
        var account = route.Length > chat.Network.Length + Separator.Length ? route[(chat.Network.Length + Separator.Length)..] : "";
        var name = merges.AccountName(chat.AccountId) ?? "";
        if (string.Equals(account, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) return true;
        if (account.Length > 0 && account.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) return true;
        // A Voice account is often named by its number; its last four digits are how people say which one.
        var digits = NameMatch.Digits(wanted);
        return digits.Length >= 4 && wanted.All(c => char.IsDigit(c) || c is ' ' or '(' or ')' or '-' or '+' or '.') &&
            (NameMatch.Digits(account).EndsWith(digits, StringComparison.Ordinal) || NameMatch.Digits(name).EndsWith(digits, StringComparison.Ordinal));
    }

    private string[] ServiceLabels(IEnumerable<ChatSummary> members) =>
        [.. members.Select(merges.ServiceRouteLabel).Distinct(StringComparer.Ordinal)];

    // The entries a service leaves: those with a member on it, a merged person narrowed to that member. No service
    // leaves them all.
    private IReadOnlyList<Entry> Pool(string? service)
    {
        var entries = Built();
        if (string.IsNullOrWhiteSpace(service)) return entries;
        var filter = ParseService(service);
        if (filter.Network is null) return [];
        var pool = new List<Entry>();
        foreach (var entry in entries)
        {
            var members = entry.Target.Members.Where(m => OnService(m, filter)).ToArray();
            if (members.Length == 0) continue;
            if (members.Length == entry.Target.Members.Count) { pool.Add(entry); continue; }
            var target = entry.Target with
            {
                Members = members, Networks = [.. members.Select(m => m.Network).Distinct(StringComparer.Ordinal)],
                CanWrite = members.Any(Writable)
            };
            pool.Add(entry with { Target = target });
        }
        return pool;
    }

    private static List<(Entry Entry, NewChatRank Rank)> Ranked(IEnumerable<Entry> pool, string text)
    {
        var rows = new List<(Entry, NewChatRank)>();
        foreach (var entry in pool)
        {
            NewChatRank? best = null;
            foreach (var (name, phone) in entry.Keys)
                if (NameMatch.Rank(name, phone, text) is { } rank && (best is null || rank < best)) best = rank;
            if (best is { } found) rows.Add((entry, found));
        }
        return Order(rows);
    }

    // Best match first; an archived conversation after an unarchived one that matches as well; then newest activity,
    // then id, so the same input always gives the same order.
    private static List<(Entry Entry, NewChatRank Rank)> Order(IEnumerable<(Entry Entry, NewChatRank Rank)> rows) =>
        [.. rows.OrderBy(r => r.Rank)
            .ThenBy(r => r.Entry.Row.IsArchived)
            .ThenByDescending(r => r.Entry.Target.LastActivity ?? DateTimeOffset.MinValue)
            .ThenBy(r => r.Entry.Target.DisplayId, StringComparer.Ordinal)];

    // The text begins the line and ends at a space, the end, or a service written straight after it ("Alex@ig").
    private static bool WordPrefix(string line, string text) =>
        text.Length > 0 && line.StartsWith(text, StringComparison.OrdinalIgnoreCase) &&
        (line.Length == text.Length || char.IsWhiteSpace(line[text.Length]) || ServiceAt(line, text.Length));

    private static string Unquote(string value)
    {
        var text = value.Trim();
        if (text.StartsWith('"')) text = text[1..];
        if (text.EndsWith('"')) text = text[..^1];
        return text.Trim();
    }

    private static string Quote(string label) => label.Contains(' ') ? "\"" + label + "\"" : label;
}
