using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

/// <summary>
/// The chat console: one scroll of everything the activity stream records across every service, and a command line.
/// It is fed only by <see cref="ActivityStream"/>, which costs no provider request; seeing a line never marks anything
/// read (only /read and /open do), and it sends only when the user presses Enter on a line for the target the prompt
/// shows. Lines and input history live in memory only; the log gets fixed words and counts.
/// </summary>
/// <remarks>
/// Made and used on the interface thread. Events from elsewhere (the stream's worker in the checks, a timer) are brought
/// back to the thread the session was made on, and every change is also taken under one lock, so a session made where
/// there is no interface thread (the checks) stays consistent.
/// </remarks>
public sealed partial class ConsoleSessionViewModel : ObservableObject, IDisposable
{
    public const int BufferLimit = 2000;
    public const int TrimAfter = 2250;
    public const int HistoryLimit = 500;
    public const int SearchShown = 10;
    public static readonly TimeSpan BackfillWindow = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan EchoHold = TimeSpan.FromSeconds(10);
    public const string Banner = "New messages from every service appear below as they arrive. /help lists commands; while no one is selected, they work without the /.";
    public const string ResetText = "Your account changed, so the console started again.";
    public const string NoPrompt = "messages>";

    private static readonly string[] CompletionPrefixes = ["/w ", "/msg ", "/query ", "/open ", "/read ", "/mute ", "/unmute ", "/filter person ", "/to ", "/route ", "/watch ", "/thread info ", "/thread members ", "/thread media ", "/thread stats ", "/thread raw ", "/thread route ", "/thread ",
        "/archive ", "/unarchive ", "/pin ", "/unpin ", "/unread ", "/delete ", "/msgs ", "/messages ", "/snooze ", "/label ", "/unlabel "];

    private readonly ActivityStream stream;
    private readonly ConversationTargets targets;
    private readonly ExplicitRead reads;
    private readonly ConsoleSender sender;
    private readonly ISearchBackend search;
    private readonly ConsoleHistory? earlier;
    private readonly MergedConversations merges;
    private readonly IConsoleHost host;
    private readonly TimeProvider clock;
    private readonly Action<string> log;
    private readonly ConsoleLineFormatter formatter;
    private readonly SynchronizationContext? ui = SynchronizationContext.Current;
    private readonly object gate = new();

    // Everything the console holds, oldest first; Lines is the part the filter and verbosity let through, in order.
    private readonly List<Held> buffer = [];
    private readonly Dictionary<string, Held> byLocalId = new(StringComparer.Ordinal);
    private readonly HashSet<long> fromSnapshot = [];
    private long seq;
    private DateOnly? sectionDate;
    private Held? sectionDay;
    private bool attached, disposed;
    private int generation;

    // The newest incoming message the console has seen, by source conversation and by where it is shown.
    private readonly Dictionary<string, ChatMessage> newestByChat = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChatMessage> newestByDisplay = new(StringComparer.Ordinal);

    // Backfill gathered per conversation, and echoes held while a console send may still learn their id.
    private readonly Dictionary<string, List<ActivityEntry>> gathering = new(StringComparer.Ordinal);
    private readonly List<(ActivityEntry Entry, DateTimeOffset Since)> parked = [];
    // Updates for a send whose line is still being appended.
    private int appending;
    private readonly Dictionary<string, List<ConsoleSendUpdate>> stashed = new(StringComparer.Ordinal);

    // Targets.
    private ConsoleRecipient? query;
    private ConsoleRecipient? replyLock;
    private int replyHold;
    private string currentInput = "";
    private string? newerHint;

    // View filter.
    private ConsoleVerbosity verbosity;
    private readonly List<(string Network, string? Account, string Label)> serviceFilter = [];
    private (string DisplayId, HashSet<string> Members, string Title)? personFilter;
    private bool showMuted;
    private readonly Dictionary<string, bool> mutedHere = new(StringComparer.Ordinal);

    // History and completion.
    private readonly List<string> history;
    private int historyIndex = -1;
    private string historyDraft = "";
    private Completion? completion;
    private sealed record Completion(string Output, int Caret, string Head, string Tail, IReadOnlyList<string> Choices, int Index);

    // The last numbered list (/ls, /search): what /open N, /to N and /thread N mean.
    private IReadOnlyList<ConsoleRef> refs = [];
    private sealed record ConsoleRef(ChatTarget? Target, SearchHit? Hit);

    public ObservableCollection<ConsoleLineViewModel> Lines { get; } = [];

    /// <summary>"&gt;" with no target, or "[to Alex · Messenger] &gt;". Set once per change, never an in-between value.</summary>
    [ObservableProperty] private string prompt = NoPrompt;
    /// <summary>What the prompt says to a screen reader: "Sending to Alex on Messenger" or "No one selected".</summary>
    [ObservableProperty] private string promptName = "No one selected";
    /// <summary>The status strip's hint: a newer writer while a reply is fixed, else the filter in force; null for none.</summary>
    [ObservableProperty] private string? hint;

    /// <summary>The typing strip; the window feeds it typing updates and a one-second tick.</summary>
    public ConsoleTyping Typing { get; }

    /// <summary>How many lines the console holds, shown or not.</summary>
    public int BufferCount { get { lock (gate) return buffer.Count; } }

    /// <summary>Where plain text goes: the reply fixed by /r while one is being typed, else the query target.</summary>
    public ConsoleRecipient? Target { get { lock (gate) return replyLock ?? query; } }

    public ConsoleVerbosity Verbosity { get { lock (gate) return verbosity; } }

    public ConsoleSessionViewModel(ActivityStream stream, ConversationTargets targets, ExplicitRead reads, ConsoleSender sender,
        ISearchBackend search, MergedConversations merges, IConsoleHost host, TimeProvider? clock, Action<string> log,
        ConsoleHistory? earlier = null)
    {
        this.stream = stream;
        this.targets = targets;
        this.reads = reads;
        this.sender = sender;
        this.search = search;
        this.earlier = earlier;
        this.merges = merges;
        this.host = host;
        this.clock = clock ?? TimeProvider.System;
        this.log = log;
        formatter = new ConsoleLineFormatter(merges, this.clock.LocalTimeZone);
        verbosity = host.Verbosity;
        Typing = new ConsoleTyping(this.clock, ChatShown);
        history = host.KeptInputs ?? [];
        try { Theme = ConsoleThemes.Find(host.Theme).Name; } catch (Exception) { }
    }

    // ---- The feed ----

    /// <summary>
    /// Opens as a login shell does: the last login, /neofetch run as if typed, the banner, then what the stream already
    /// holds, then follows it. Once. Nothing earlier is read from this PC; /history does that when asked.
    /// </summary>
    public void Attach()
    {
        lock (gate)
        {
            if (attached || disposed) return;
            attached = true;
            // Subscribed before the snapshot is read, so nothing falls between; an entry in both is taken once.
            stream.Appended += Stream_Appended;
            stream.Reset += Stream_Reset;
            sender.Updated += Sender_Updated;
            var held = stream.Snapshot();
            Motd();
            var previous = running.Value;
            running.Value = new RunState();
            try { Echo(Prompt, "/neofetch"); Neofetch(); }
            finally { running.Value = previous; }
            Notice(ConsoleLineKind.Info, Banner);
            TakeHeld(held);
            log($"console attach lines={buffer.Count.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    private void TakeHeld(IReadOnlyList<ActivityEntry> held)
    {
        foreach (var entry in held)
        {
            fromSnapshot.Add(entry.Sequence);
            Take(entry);
        }
    }

    // ---- History ----

    // /history is asked for and waited on, and a week reads far more than a day, so it may take this long.
    public static readonly TimeSpan CommandHistoryBudget = TimeSpan.FromSeconds(10);

    private static string Hours(TimeSpan span) => span == ConsoleHistory.DefaultSpan ? "day"
        : $"{((int)span.TotalHours).ToString(CultureInfo.CurrentCulture)} hours";

    // The span ends where the stream's own lines begin, so the two meet: at its oldest entry, or now when it holds none.
    // The stream keeps a bounded number, so after a long run that is later than the launch, and history fills the gap.
    private async Task<(IReadOnlyList<ConsoleHistoryItem> Items, bool TimedOut)> ReadEarlierAsync(TimeSpan span, string reason, TimeSpan budgetTime)
    {
        if (earlier is null) return ([], false);
        var now = clock.GetUtcNow();
        var held = stream.Snapshot();
        var until = held.Count > 0 ? held.Min(entry => entry.At) : now;
        if (until > now) until = now;
        var started = clock.GetTimestamp();
        using var budget = new CancellationTokenSource(budgetTime, clock);
        IReadOnlyList<ConsoleHistoryItem> items = [];
        string outcome;
        try { items = await earlier.ReadAsync(now - span, until, budget.Token); outcome = "ok"; }
        catch (OperationCanceledException) { outcome = "timeout"; }
        catch (Exception) { outcome = "failed"; }
        log($"console history reason={reason} span_h={((int)span.TotalHours).ToString(CultureInfo.InvariantCulture)} " +
            $"lines={items.Count.ToString(CultureInfo.InvariantCulture)} ms={((long)clock.GetElapsedTime(started).TotalMilliseconds).ToString(CultureInfo.InvariantCulture)} outcome={outcome}");
        return (items, outcome == "timeout");
    }

    // Oldest first, above the banner. Each line is the entry its arrival would have made, so it reads, filters and
    // answers /r like a live one; a message the stream also holds is left to the stream. Returns how many were printed.
    private int PrintEarlier(IReadOnlyList<ConsoleHistoryItem> items, IReadOnlyList<ActivityEntry> held)
    {
        var live = new HashSet<(string, string)>(held.Where(entry => entry.Message is not null).Select(entry => (entry.Message!.ChatId, entry.Message.Id)));
        var printed = 0;
        foreach (var item in items)
        {
            if (live.Contains((item.Message.ChatId, item.Message.Id)) || merges.Source(item.Message.ChatId)?.IsSpam == true) continue;
            if (stream.Earlier(item.Chat, item.Message) is not { } entry) continue;
            if (entry is { Kind: ActivityKind.Message, IsOutgoing: false, Message: { } seen, ChatId: { } from })
            {
                Newest(newestByChat, from, seen);
                Newest(newestByDisplay, entry.DisplayChatId ?? from, seen);
            }
            if (entry.IsArchived) continue;
            Print(entry);
            printed++;
        }
        return printed;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            generation++;
            stream.Appended -= Stream_Appended;
            stream.Reset -= Stream_Reset;
            sender.Updated -= Sender_Updated;
        }
    }

    private void Stream_Appended(object? source, ActivityEntry entry) => OnUi(() =>
    {
        if (disposed || fromSnapshot.Remove(entry.Sequence)) return;
        Take(entry);
    });

    private void Stream_Reset(object? source, EventArgs e) => OnUi(() =>
    {
        if (disposed) return;
        ClearAll();
        query = null; replyLock = null; newerHint = null;
        history.Clear(); historyIndex = -1; completion = null; refs = []; messageRefs = []; confirming = null; held = null;
        newestByChat.Clear(); newestByDisplay.Clear(); mutedHere.Clear();
        sender.Forget();
        Typing.Clear();
        UpdatePrompt(); UpdateHint();
        Notice(ConsoleLineKind.Info, ResetText);
        log("console reset");
    });

    // One stream entry: remembered for routing, then printed, gathered, held as a possible echo, or attached to the
    // console's own line for the send it echoes.
    private void Take(ActivityEntry entry)
    {
        if (entry.ChatId is { } chatId && merges.Source(chatId)?.IsSpam == true) return;
        if (entry is { Kind: ActivityKind.Message, IsOutgoing: false, Message: { } seen, ChatId: { } from })
        {
            Newest(newestByChat, from, seen);
            Newest(newestByDisplay, entry.DisplayChatId ?? from, seen);
        }
        // Archived conversations are not shown; spam never reaches the stream.
        if (entry.IsArchived) return;
        if (entry is { Kind: ActivityKind.Message, IsOutgoing: true, Message: { } mine })
        {
            if (sender.ClaimEcho(mine) is { } localId) { Attach(localId); return; }
            if (sender.MayClaimLater(mine.ChatId))
            {
                parked.Add((entry, clock.GetUtcNow()));
                var version = generation;
                _ = LaterAsync(EchoHold, version, () => ReleaseParked(force: true));
                return;
            }
        }
        if (entry is { IsBackfill: true, IsOutgoing: false } && entry.Kind is ActivityKind.Message or ActivityKind.Call or ActivityKind.Voicemail)
        {
            var key = entry.DisplayChatId ?? entry.ChatId ?? "";
            if (gathering.TryGetValue(key, out var gathered)) { gathered.Add(entry); return; }
            gathering[key] = [entry];
            var version = generation;
            _ = LaterAsync(BackfillWindow, version, () => FlushBackfill(key));
            return;
        }
        Print(entry);
    }

    private static void Newest(Dictionary<string, ChatMessage> table, string key, ChatMessage message)
    {
        if (!table.TryGetValue(key, out var known) || message.Timestamp >= known.Timestamp) table[key] = message;
    }

    private void Print(ActivityEntry entry)
    {
        var level = ConsoleLineFormatter.Level(entry.Kind);
        Add(entry.At, (n, withDate) => formatter.Line(entry, n, withDate), level, entry);
    }

    private void FlushBackfill(string key)
    {
        if (!gathering.Remove(key, out var entries) || entries.Count == 0) return;
        var newest = entries.OrderBy(e => e.At).ThenBy(e => e.Sequence).Last();
        Add(newest.At, (n, withDate) => formatter.CatchUp(entries, n, withDate), ConsoleVerbosity.Normal, newest);
    }

    // An outgoing entry held while a console send in its conversation might still turn out to be what it echoes.
    private void ReleaseParked(bool force)
    {
        if (parked.Count == 0) return;
        var now = clock.GetUtcNow();
        foreach (var item in parked.ToArray())
        {
            var message = item.Entry.Message!;
            if (sender.ClaimEcho(message) is { } localId) { parked.Remove(item); Attach(localId); continue; }
            if (sender.MayClaimLater(message.ChatId) && !(force && now - item.Since >= EchoHold)) continue;
            parked.Remove(item);
            Print(item.Entry);
        }
    }

    // The provider's copy of a console send: the line is already there, and its echo says it went.
    private void Attach(string localId)
    {
        if (byLocalId.TryGetValue(localId, out var held) &&
            held.Vm.SendState is ConsoleSendState.Sending or ConsoleSendState.Unconfirmed or ConsoleSendState.Unknown)
            held.Vm.SendState = ConsoleSendState.Sent;
    }

    // ---- Lines ----

    private sealed class Held(ConsoleLineViewModel vm)
    {
        public ConsoleLineViewModel Vm { get; } = vm;
        public ConsoleLine Line => Vm.Line;
        public ConsoleVerbosity Level { get; init; }
        public string? Title { get; init; }
        public string? RouteLabel { get; init; }
        public string? AccountId { get; init; }
        public Held? Day { get; set; }
        public bool Visible { get; set; }
        public bool Removed { get; set; }
    }

    private Held? Add(DateTimeOffset at, Func<long, bool, ConsoleLine?> make, ConsoleVerbosity level, ActivityEntry? entry,
        ConsoleRecipient? to = null, ConsoleSendState state = ConsoleSendState.None)
    {
        var date = formatter.LocalDate(at);
        var withDate = false;
        var newDay = sectionDate is { } current && date > current;
        if (sectionDate is { } shown && date < shown) withDate = true;
        // The day line takes the number before its first line's.
        var line = make(seq + (newDay ? 2 : 1), withDate);
        if (line is null) return null;
        var day = newDay ? new Held(new ConsoleLineViewModel(formatter.Day(at, line.Seq - 1))) { Level = ConsoleVerbosity.Quiet } : null;
        sectionDate ??= date;
        seq = line.Seq;
        if (day is not null)
        {
            // A new day starts only with a line to go under it.
            buffer.Add(day);
            sectionDay = day;
            sectionDate = date;
        }
        var held = new Held(new ConsoleLineViewModel(line) { SendState = state })
        {
            Level = level,
            Title = entry is not null ? ConsoleLineFormatter.TitleOf(entry) : to?.Title,
            RouteLabel = entry is not null ? (entry.Network.Length == 0 ? null : formatter.RouteLabel(entry.ChatId, entry.Network, entry.AccountId)) : to?.Service,
            AccountId = entry?.AccountId ?? to?.AccountId,
            Day = sectionDay,
        };
        buffer.Add(held);
        if (line.LocalId is { } localId) byLocalId[localId] = held;
        if (Shows(held))
        {
            if (held.Day is { Visible: false, Removed: false } head) { Lines.Add(head.Vm); head.Visible = true; }
            Lines.Add(held.Vm);
            held.Visible = true;
            Noticed(held);
        }
        Trim();
        return held;
    }

    private void Notice(ConsoleLineKind kind, string text) =>
        Add(clock.GetUtcNow(), (n, withDate) => formatter.Notice(kind, text, clock.GetUtcNow(), n, withDate), ConsoleVerbosity.Quiet, null);

    private void Trim()
    {
        if (buffer.Count <= TrimAfter) return;
        var remove = buffer.Count - BufferLimit;
        for (var index = 0; index < remove; index++)
        {
            var held = buffer[index];
            held.Removed = true;
            if (held.Visible)
            {
                if (Lines.Count > 0 && ReferenceEquals(Lines[0], held.Vm)) Lines.RemoveAt(0); else Lines.Remove(held.Vm);
                held.Visible = false;
            }
            if (held.Line.LocalId is { } localId) byLocalId.Remove(localId);
        }
        buffer.RemoveRange(0, remove);
        log($"console trimmed lines={remove.ToString(CultureInfo.InvariantCulture)}");
    }

    private void ClearAll()
    {
        generation++;
        buffer.Clear(); Lines.Clear(); byLocalId.Clear(); fromSnapshot.Clear();
        gathering.Clear(); parked.Clear(); stashed.Clear();
        sectionDate = null; sectionDay = null;
    }

    // Whether a line is let through: archived never; notices always; then verbosity, mute, services and person.
    private bool Shows(Held held)
    {
        var line = held.Line;
        if (line.Kind == ConsoleLineKind.Day) return false;
        if (line.Archived) return false;
        if (line.Kind is ConsoleLineKind.Info or ConsoleLineKind.Error) return true;
        if (held.Level > verbosity) return false;
        var display = line.DisplayChatId ?? line.SourceChatId;
        var muted = display is not null && mutedHere.TryGetValue(display, out var here) ? here : line.Muted;
        if (muted && !showMuted) return false;
        if (serviceFilter.Count > 0 && line.Network is { } network && !serviceFilter.Any(f => ServiceMatches(f, network, held.RouteLabel))) return false;
        if (personFilter is { } person && line.SourceChatId is { } source &&
            person.DisplayId != display && !person.Members.Contains(source)) return false;
        return true;
    }

    private static bool ServiceMatches((string Network, string? Account, string Label) filter, string network, string? routeLabel)
    {
        if (filter.Network != network) return false;
        if (filter.Account is not { } account) return true;
        var label = routeLabel ?? network;
        var own = label.Length > network.Length + 3 ? label[(network.Length + 3)..] : "";
        return own.StartsWith(account, StringComparison.OrdinalIgnoreCase);
    }

    // Brings Lines in line with the filter by removing and inserting only what changed, so what stays keeps its place.
    private void Rebuild()
    {
        var shows = new bool[buffer.Count];
        var sectionHasLine = false;
        for (var index = buffer.Count - 1; index >= 0; index--)
        {
            var held = buffer[index];
            if (held.Line.Kind == ConsoleLineKind.Day) { shows[index] = sectionHasLine; sectionHasLine = false; continue; }
            shows[index] = Shows(held);
            sectionHasLine |= shows[index];
        }
        var at = 0;
        for (var index = 0; index < buffer.Count; index++)
        {
            var held = buffer[index];
            if (held.Visible && !shows[index]) { Lines.RemoveAt(at); held.Visible = false; }
            else if (!held.Visible && shows[index]) { Lines.Insert(at, held.Vm); held.Visible = true; at++; }
            else if (held.Visible) at++;
        }
        Typing.Refresh();
    }

    // A newer writer while a reply is fixed: said, never followed.
    private void Noticed(Held held)
    {
        if (replyLock is not { } locked || held.Line.Kind != ConsoleLineKind.Incoming) return;
        var display = held.Line.DisplayChatId ?? held.Line.SourceChatId;
        if (display == locked.DisplayChatId) return;
        var name = held.Title ?? "Someone";
        newerHint = $"Newer: {name} · {held.RouteLabel ?? held.Line.Network ?? ""} — Esc, then /r, to answer {name}";
        UpdateHint();
    }

    private Held? NewestIncoming()
    {
        for (var index = buffer.Count - 1; index >= 0; index--)
            if (buffer[index] is { Visible: true, Line.Kind: ConsoleLineKind.Incoming } held) return held;
        return null;
    }

    // ---- Prompt and targets ----

    private void UpdatePrompt()
    {
        var target = replyLock ?? query;
        var next = target is null ? NoPrompt : PromptFor(target.Title);
        var name = target is null ? "No one selected" : $"Sending to {target.Title} on {target.Service}";
        if (Prompt != next) Prompt = next;
        if (PromptName != name) PromptName = name;
    }

    private void UpdateHint()
    {
        string? filter = null;
        var parts = new List<string>();
        parts.AddRange(serviceFilter.Select(f => f.Label));
        if (personFilter is { } person) parts.Add(person.Title);
        if (showMuted) parts.Add("muted conversations");
        if (parts.Count > 0) filter = "Showing " + string.Join(", ", parts) + " · /filter all to reset";
        var next = (replyLock is not null ? newerHint : null) ?? filter;
        if (Hint != next) Hint = next;
    }

    private ConsoleRecipient RecipientFor(Held held)
    {
        var source = held.Line.SourceChatId!;
        var display = held.Line.DisplayChatId ?? source;
        var target = targets.Entries().FirstOrDefault(e => e.DisplayId == display);
        var network = held.Line.Network ?? ServiceScopes.Name(source);
        return new ConsoleRecipient(source, display, target?.Title ?? held.Title ?? "Someone", held.RouteLabel ?? network, network,
            held.AccountId ?? "", target);
    }

    private bool LockReply()
    {
        if (NewestIncoming() is not { Line.SourceChatId: not null } held) return false;
        replyLock = RecipientFor(held);
        newerHint = null;
        UpdatePrompt(); UpdateHint();
        log($"console target locked kind=reply provider={ConsoleLineFormatter.Provider(replyLock.Network)}");
        return true;
    }

    private void ReleaseReply()
    {
        if (replyLock is null) return;
        replyLock = null; newerHint = null;
        UpdatePrompt(); UpdateHint();
    }

    // A target chosen now replaces a reply target still shown while its /r is being sent (that send keeps its own
    // recipient), so the prompt always names the latest choice and plain text goes there.
    private void SetQuery(ConsoleRecipient recipient, string kind)
    {
        query = recipient;
        replyLock = null; newerHint = null;
        UpdatePrompt(); UpdateHint();
        log($"console target locked kind={kind} provider={ConsoleLineFormatter.Provider(recipient.Network)}");
    }

    private static bool IsReplyStart(string text)
    {
        var line = text.TrimStart();
        foreach (var word in new[] { "/r", "/reply" })
            if (line.Length > word.Length && line.StartsWith(word, StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(line[word.Length]))
                return true;
        return false;
    }

    private static bool StillReply(string text) => text.TrimStart().StartsWith("/r", StringComparison.OrdinalIgnoreCase);

    // "/reply m3 …", "/reply ^ …", "/reply #2 …": a quoted reply, which goes to the quoted message's conversation.
    private static bool IsQuotedReply(string text)
    {
        var line = text.TrimStart();
        if (!line.StartsWith("/reply ", StringComparison.OrdinalIgnoreCase)) return false;
        var rest = line[7..].TrimStart();
        var end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end])) end++;
        return end > 0 && ConsoleCommandParser.IsMessageRef(rest[..end], numbers: false);
    }

    /// <summary>
    /// The input changed. When its first word becomes /r and a space, the reply target is fixed to the newest shown
    /// incoming line at that moment, and it stays fixed while the line is typed, whoever writes meanwhile.
    /// </summary>
    public void InputChanged(string text)
    {
        lock (gate)
        {
            currentInput = text ?? "";
            // An edit (not the line being cleared on Enter) means a held look-alike is no longer the line it warned about.
            if (held is not null && currentInput.Length > 0 && currentInput.Trim() != held.Value.Text) held = null;
            // "/reply m3 …" quotes a message in its own conversation: whoever wrote last isn't who it goes to.
            if (IsQuotedReply(currentInput)) { if (replyLock is not null && replyHold == 0) ReleaseReply(); }
            else if (IsReplyStart(currentInput)) { if (replyLock is null) LockReply(); }
            else if (replyLock is not null && replyHold == 0 && !StillReply(currentInput)) ReleaseReply();
            if (completion is not null && completion.Output != currentInput) completion = null;
        }
    }

    /// <summary>Esc. On an empty input it clears the query target; otherwise it puts the input into history and returns
    /// the empty input.</summary>
    public string Escape(string input)
    {
        lock (gate)
        {
            completion = null;
            historyIndex = -1;
            held = null;
            if (string.IsNullOrEmpty(input))
            {
                query = null;
                ReleaseReply();
                UpdatePrompt();
                return "";
            }
            Remember(input);
            currentInput = "";
            if (replyHold == 0) ReleaseReply();
            return "";
        }
    }

    private void Remember(string input)
    {
        var line = input.Trim();
        if (line.Length == 0) return;
        if (history.Count == 0 || history[^1] != line) history.Add(line);
        if (history.Count > HistoryLimit) history.RemoveRange(0, history.Count - HistoryLimit);
    }

    /// <summary>Up: the previous submitted line, or null when there is none further back.</summary>
    public string? HistoryUp(string current)
    {
        lock (gate)
        {
            if (history.Count == 0) return null;
            if (historyIndex == -1) { historyDraft = current; historyIndex = history.Count - 1; }
            else if (historyIndex > 0) historyIndex--;
            else return null;
            return history[historyIndex];
        }
    }

    /// <summary>Down: the next line, then what was being typed before Up; null when already there.</summary>
    public string? HistoryDown(string current)
    {
        lock (gate)
        {
            if (historyIndex == -1) return null;
            historyIndex++;
            if (historyIndex < history.Count) return history[historyIndex];
            historyIndex = -1;
            return historyDraft;
        }
    }

    /// <summary>
    /// Tab completes command words and options, then recipient names from <see cref="ConversationTargets.Complete"/>.
    /// Pressed again it cycles in the same fixed order; Shift+Tab goes backwards.
    /// Gives back the input and caret unchanged when there is nothing to complete.
    /// </summary>
    public (string Input, int Caret) Complete(string input, int caret, bool backwards)
    {
        lock (gate)
        {
            caret = Math.Clamp(caret, 0, input.Length);
            if (completion is { } last && last.Output == input && last.Caret == caret && last.Choices.Count > 0)
            {
                var index = (last.Index + (backwards ? -1 : 1) + last.Choices.Count) % last.Choices.Count;
                return Complete(last with { Index = index });
            }
            var before = input[..caret];
            if (TerminalCompletion(before) is { } terminal && terminal.Choices.Count > 0)
                return Complete(new Completion("", 0, input[..terminal.Start], input[caret..], terminal.Choices, backwards ? terminal.Choices.Count - 1 : 0));
            var start = -1;
            foreach (var prefix in CompletionPrefixes)
                if (before.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { start = prefix.Length; break; }
            var wordStart = before.LastIndexOfAny([' ', '\t', '\n']) + 1;
            if (wordStart < before.Length && before[wordStart] == '@') start = wordStart + 1;
            if (start < 0) return (input, caret);
            var typed = before[start..];
            var choices = targets.Complete(typed, null);
            if (choices.Count == 0) return (input, caret);
            return Complete(new Completion("", 0, input[..start], input[caret..], choices, backwards ? choices.Count - 1 : 0));
        }
    }

    private (string Input, int Caret) Complete(Completion state)
    {
        var chosen = state.Choices[state.Index] + " ";
        var tail = state.Tail.TrimStart();
        var output = state.Head + chosen + tail;
        var caret = state.Head.Length + chosen.Length;
        completion = state with { Output = output, Caret = caret };
        return (output, caret);
    }

    // ---- Commands ----

    /// <summary>Enter. Plain text goes to the target the prompt shows, once; a command does what it says.</summary>
    public async Task SubmitAsync(string input)
    {
        var line = (input ?? "").Trim();
        var prompt = Prompt;
        if (line.Length == 0)
        {
            // A blank line leaves the prompt in the scroll and gives a fresh one under it, as a shell does. Nothing is
            // sent and nothing is kept in history.
            lock (gate) { if (disposed) return; historyIndex = -1; completion = null; }
            Echo(prompt, "");
            return;
        }
        if (line.StartsWith('!') && Expand(line, out var problem) is var found && (found is not null || problem is not null))
        {
            lock (gate) { if (disposed) return; historyIndex = -1; completion = null; }
            if (found is null)
            {
                await RunEchoed(prompt, line, () => { Command("history_event"); Fail(problem!); return Task.CompletedTask; });
                return;
            }
            if (!found.StartsWith('/') || ConsoleCatalog.StartsWithSend(found) || ConsoleCatalog.StartsWithAct(found))
            {
                // A message is never sent again by recall, nor a reaction, archive or delete made again: it goes back in the
                // prompt, to be run with Enter or not at all.
                Command("history_event");
                InputSuggested?.Invoke(found);
                return;
            }
            line = found;
        }
        // "\!wow" sends "!wow": the way past history expansion for a message that starts with "!".
        if (line.StartsWith("\\!", StringComparison.Ordinal)) line = line[1..];
        else
        {
            ConsoleRecipient? shown;
            lock (gate) shown = replyLock ?? query;
            if (!line.StartsWith('/') && shown is null && CommandWord(line) is not null)
            {
                // No one is selected, so nothing typed here could be sent: a line starting with a command runs it, as if
                // it had its slash. Commands that send still need theirs.
                line = "/" + line;
                log("console command_mode outcome=ran");
            }
            else if (shown is not null && HoldsLookalike(line, input ?? "", shown, prompt)) return;
        }
        if (line.StartsWith('/') && !ConsoleCatalog.StartsWithSend(line) && !line.StartsWith("//", StringComparison.Ordinal))
        {
            lock (gate)
            {
                if (disposed) return;
                Remember(line);
                historyIndex = -1;
                completion = null;
            }
            await RunCommandLineAsync(line, prompt);
            return;
        }
        if (line.StartsWith('/') && ConsoleCatalog.HelpAsked(line) is { } asked)
        {
            // "/w --help" shows the manual and never sends.
            lock (gate) { if (disposed) return; Remember(line); historyIndex = -1; completion = null; }
            await RunEchoed(prompt, line, () => { Command("help"); Manual(asked); return Task.CompletedTask; });
            return;
        }
        var command = ConsoleCommandParser.Parse(line);
        ConsoleRecipient? replyTo = null;
        lock (gate)
        {
            if (disposed) return;
            Remember(input ?? "");
            historyIndex = -1;
            completion = null;
            if (command is ConsoleCommand.Reply)
            {
                // Pasted in one go: fixed now, at Enter, to the newest shown incoming line.
                if (replyLock is null) LockReply();
                replyTo = replyLock;
                replyHold++;
            }
        }
        try { await RunAsync(command, replyTo, direct: true); }
        finally
        {
            if (command is ConsoleCommand.Reply)
                lock (gate)
                {
                    replyHold--;
                    if (replyHold == 0 && !IsReplyStart(currentInput)) ReleaseReply();
                }
        }
    }

    // direct: the command was typed on a line of its own (not chained, piped or from an alias), which a command that acts
    // on a conversation or a message needs.
    private async Task RunAsync(ConsoleCommand command, ConsoleRecipient? replyTo, bool direct = false)
    {
        switch (command)
        {
            case ConsoleCommand.Act act: await ActAsync(act, direct); return;
            case ConsoleCommand.QuotedReply quoted: await QuotedReplyAsync(quoted); return;
            case ConsoleCommand.Chats chats: ListChats(chats.Selector); return;
            case ConsoleCommand.To targetCommand: ChooseTarget(targetCommand.Name); return;
            case ConsoleCommand.Route route: ShowRoute(route.Name); return;
            case ConsoleCommand.Status: PrintStatus(); return;
            case ConsoleCommand.Commands commands: PrintCommands(commands.Query); return;
            case ConsoleCommand.Fortune: Command("fortune"); Say(ConsoleLineKind.Info, Fortunes[Random.Shared.Next(Fortunes.Length)]); return;
            case ConsoleCommand.View view:
                Command(view.App);
                if (view.App == "latest") { ViewRequested?.Invoke("latest"); return; }
                LaunchApp(view.App, view.Argument);
                return;
            case ConsoleCommand.Tool tool:
                Command(tool.Name);
                await ToolAsync(tool.Name, tool.Rest);
                return;
            case ConsoleCommand.Say { Text: var text }:
                if (text.Length == 0) return;
                ConsoleRecipient? to;
                // Exactly who the prompt shows: while a /r is still being sent, that is its reply target, not the
                // query target behind it.
                lock (gate) to = replyLock ?? query;
                if (to is null)
                {
                    // A first word that is nearly a command: say which, as a shell does.
                    var nearly = ConsoleCatalog.Suggest(FirstWord(text).ToLowerInvariant());
                    Say(ConsoleLineKind.Error, "No one is selected. Use /r to reply or /w name message." + (nearly is null ? "" : $" Did you mean {nearly}?"));
                    return;
                }
                await SendAsync(to, text);
                return;
            case ConsoleCommand.Reply { Text: var text }:
                Command("r");
                if (replyTo is null) { Say(ConsoleLineKind.Error, "No one has written since the console started. Use /w name message."); return; }
                if (text.Length == 0) { lock (gate) SetQuery(replyTo, "query"); return; }
                ConsoleRecipient? before;
                lock (gate) before = query;
                // The reply's person becomes the query target, unless another was chosen while it was being sent.
                if (await SendAsync(replyTo, text)) lock (gate) { if (ReferenceEquals(query, before)) query = replyTo; UpdatePrompt(); }
                return;
            case ConsoleCommand.Whisper whisper:
                await WhisperAsync(whisper);
                return;
            case ConsoleCommand.Open open:
                Command("open");
                await OpenAsync(open);
                return;
            case ConsoleCommand.Read read:
                Command("read");
                await ReadAsync(read.Name);
                return;
            case ConsoleCommand.Mute mute:
                Command("mute");
                await MuteAsync(mute.Name, true);
                return;
            case ConsoleCommand.Unmute unmute:
                Command("unmute");
                await MuteAsync(unmute.Name, false);
                return;
            case ConsoleCommand.Filter filter:
                Command("filter");
                Filter(filter.Args);
                return;
            case ConsoleCommand.Events events:
                Command("events");
                lock (gate)
                {
                    verbosity = events.Level;
                    host.Verbosity = events.Level;
                    Rebuild();
                }
                Say(ConsoleLineKind.Info, events.Level switch
                {
                    ConsoleVerbosity.Quiet => "Showing messages only.",
                    ConsoleVerbosity.Normal => "Showing messages and events.",
                    _ => "Showing everything, including read receipts and typing."
                });
                return;
            case ConsoleCommand.Search { Words: var words }:
                Command("search");
                await SearchAsync(words);
                return;
            case ConsoleCommand.Pause { Duration: var duration }:
                Command("pause");
                if (!host.NotificationsOn) { Say(ConsoleLineKind.Info, "Notifications are off in Settings."); return; }
                if (duration is { } span) host.PauseFor(span); else host.PauseUntilMorning();
                Say(ConsoleLineKind.Info, host.PauseText ?? "Notifications are paused.");
                return;
            case ConsoleCommand.Resume:
                Command("resume");
                if (!host.NotificationsOn) { Say(ConsoleLineKind.Info, "Notifications are off in Settings."); return; }
                host.Resume();
                Say(ConsoleLineKind.Info, "Notifications are on again.");
                return;
            case ConsoleCommand.Help { Topic: var topic }:
                Command("help");
                PrintHelp(topic);
                return;
            case ConsoleCommand.Clear:
                Command("clear");
                lock (gate) ClearAll();
                return;
            case ConsoleCommand.History { Hours: var hours }:
                Command("history");
                if (earlier is null) { Say(ConsoleLineKind.Info, "History isn't available here."); return; }
                var wantedSpan = hours is { } count ? TimeSpan.FromHours(count) : ConsoleHistory.DefaultSpan;
                var (items, timedOut) = await ReadEarlierAsync(wantedSpan, "command", CommandHistoryBudget);
                // Redrawn from the top, as /clear does, with the span above and the stream's own lines below it again.
                lock (gate)
                {
                    if (disposed) return;
                    ClearAll();
                    var held = stream.Snapshot();
                    var printed = PrintEarlier(items, held);
                    Notice(timedOut ? ConsoleLineKind.Error : ConsoleLineKind.Info,
                        timedOut ? $"Reading the last {Hours(wantedSpan)} took too long. Try a shorter span, such as /history 24."
                        : printed > 0 ? $"Above: the last {Hours(wantedSpan)} kept on this PC."
                        : $"No earlier messages from the last {Hours(wantedSpan)} are kept on this PC.");
                    TakeHeld(held);
                }
                return;
            case ConsoleCommand.Unknown { Word: var word }:
                Command("unknown");
                Say(ConsoleLineKind.Error, ConsoleCatalog.Suggest(word) is { } close ? $"Unknown command /{word}. Did you mean /{close}?" : $"Unknown command /{word}. Type /help for commands.");
                return;
            case ConsoleCommand.Invalid { Message: var message, Command: var name }:
                Command(name);
                Say(ConsoleLineKind.Error, message);
                return;
        }
    }

    private static readonly HashSet<string> CommandNames = ["r", "w", "open", "read", "mute", "unmute", "filter", "events", "search", "history", "pause", "resume", "help", "clear", "unknown", "chats", "to", "route", "status", "commands", "fortune", "apps", "top", "latest", "banner", "logs", "matrix", "thread", "diag", "mirror", "whoami", "uptime", "neofetch", "cowsay", "date", "echo", "theme", "alias", "unalias", "watch", "next", "shell", "history_event", "app",
        "reply", "react", "unreact", "like", "edit", "unsend", "archive", "unarchive", "pin", "unpin", "unread", "delete", "label", "unlabel",
        "snooze", "copy", "save", "remind", "msgs", "grep", "guide", "tour", "manual"];
    private void Command(string name) => log("console command name=" + (CommandNames.Contains(name) ? name : "unknown"));

    // Inside a command line its output is the command's (wide, or into a pipe); otherwise a notice with its time.
    private void Say(ConsoleLineKind kind, string text)
    {
        if (running.Value is not null)
        {
            var error = kind == ConsoleLineKind.Error;
            Emit(new ConsoleOutLine([new ConsoleSpan(text, error ? ConsoleTone.Error : ConsoleTone.Default)], error));
            return;
        }
        lock (gate) if (!disposed) Notice(kind, text);
    }

    private async Task<bool> SendAsync(ConsoleRecipient to, string text, string? replyToId = null)
    {
        lock (gate) appending++;
        ConsoleSendResult result;
        try { result = replyToId is null ? await sender.SendAsync(to, text) : await sender.SendReplyAsync(to, text, replyToId); }
        catch (Exception)
        {
            lock (gate) { appending--; if (appending == 0) stashed.Clear(); }
            Say(ConsoleLineKind.Error, "Not sent: something went wrong before it was submitted. Nothing was queued.");
            return false;
        }
        lock (gate)
        {
            appending--;
            if (result is ConsoleSendResult.Refused { Message: var why })
            {
                if (appending == 0) stashed.Clear();
                Notice(ConsoleLineKind.Error, why);
                return false;
            }
            var localId = ((ConsoleSendResult.Submitted)result).LocalId;
            var now = clock.GetUtcNow();
            Add(now, (n, withDate) => formatter.Sent(to, text, now, n, localId, withDate), ConsoleVerbosity.Quiet, null, to, ConsoleSendState.Sending);
            if (stashed.Remove(localId, out var early)) foreach (var update in early) Apply(update);
            if (appending == 0) stashed.Clear();
            ReleaseParked(force: false);
            return true;
        }
    }

    private void Sender_Updated(object? source, ConsoleSendUpdate update) => OnUi(() =>
    {
        if (disposed) return;
        if (!byLocalId.ContainsKey(update.LocalId) && appending > 0)
        {
            if (!stashed.TryGetValue(update.LocalId, out var list)) stashed[update.LocalId] = list = [];
            list.Add(update);
            return;
        }
        Apply(update);
        ReleaseParked(force: false);
    });

    private void Apply(ConsoleSendUpdate update)
    {
        if (byLocalId.TryGetValue(update.LocalId, out var held))
        {
            // An echo already said it went; a later "Sending" or "Unconfirmed" from the outbox does not undo that.
            if (!(held.Vm.SendState == ConsoleSendState.Sent && update.State is ConsoleSendState.Sending or ConsoleSendState.Unconfirmed))
                held.Vm.SendState = update.State;
            if (held.Vm.SendState == ConsoleSendState.Sent && update.State == ConsoleSendState.Unconfirmed) return;
        }
        if (update.Notice is { } notice) Notice(update.NoticeKind, notice);
    }

    private async Task WhisperAsync(ConsoleCommand.Whisper whisper)
    {
        ChatTarget? target = null;
        string? text = whisper.Text;
        string? service = whisper.Service;
        string typed = whisper.Name;
        IReadOnlyList<ChatTarget> ambiguous = [];
        if (whisper.Exact)
        {
            // With text to send at once, only a whole name is taken; a name that only begins one is offered instead.
            switch (targets.Resolve(whisper.Name, whisper.Service, toSend: text is not null))
            {
                case TargetMatch.Unique { Target: var unique }: target = unique; break;
                case TargetMatch.Ambiguous { Top: var top }: ambiguous = top; break;
            }
        }
        else
        {
            var lead = targets.Lead(whisper.Name);
            service = lead.Service;
            if (lead.Target is { } found) { target = found; text = lead.Rest.Length == 0 ? null : lead.Rest; }
            else
            {
                ambiguous = lead.Ambiguous;
                typed = lead.Name.Length > 0 ? lead.Name : FirstWord(whisper.Name.Trim());
            }
        }
        if (target is null)
        {
            log("console command name=w target=" + (ambiguous.Count > 0 ? "ambiguous" : "none"));
            Say(ConsoleLineKind.Error, ambiguous.Count > 0 ? AmbiguousText(typed, ambiguous) : NobodyText(typed));
            return;
        }
        ConsoleRoute route;
        lock (gate) route = sender.Route(target, service, NewestByDisplay);
        if (route is ConsoleRoute.Refused { Message: var why, NeedsService: var needs })
        {
            log("console command name=w target=" + (needs ? "needs_service" : "unique"));
            Say(ConsoleLineKind.Error, why);
            return;
        }
        var recipient = ((ConsoleRoute.To)route).Recipient;
        log("console command name=w target=unique");
        lock (gate) SetQuery(recipient, text is null ? "query" : "whisper");
        if (text is not null) await SendAsync(recipient, text);
    }

    private static string FirstWord(string line)
    {
        var space = line.IndexOfAny([' ', '\t']);
        return (space < 0 ? line : line[..space]).Trim('"');
    }

    private static string NobodyText(string typed) =>
        $"Nobody called “{typed}” here. Press Tab after /w to see names, or start a new chat with Ctrl+N.";

    private static string AmbiguousText(string typed, IReadOnlyList<ChatTarget> choices) =>
        $"“{typed}” could be {string.Join(", ", choices.Select(Choice))}. Type more of the name, or add @service.";

    // "Alex Smith · Messenger": a title with the service it is on (a merged person's services joined), unless its label
    // already names it.
    private static string Choice(ChatTarget target)
    {
        if (target.Label != target.Title) return target.Label;
        var tags = target.Networks.Select(ConsoleLineFormatter.Tag).Where(tag => tag.Length > 0).Distinct().ToArray();
        return tags.Length == 0 ? target.Title : target.Title + " · " + string.Join("/", tags);
    }

    private ChatMessage? NewestByDisplay(string displayId) { lock (gate) return newestByDisplay.GetValueOrDefault(displayId); }
    private ChatMessage? NewestByChat(string chatId) { lock (gate) return newestByChat.GetValueOrDefault(chatId); }

    // A name typed after a command, resolved from local data only; null (with the reason said) when it is not one target.
    private ChatTarget? Named(string text)
    {
        var typed = text.Trim();
        // "." is the conversation open in Messages; a number is a row of the last /ls or /search list.
        if (typed == ".")
        {
            if (OpenTarget() is { } open) return open;
            Say(ConsoleLineKind.Error, "No conversation is open in Message Link.");
            return null;
        }
        if (typed.Length is > 0 and <= 4 && typed.All(char.IsAsciiDigit))
        {
            IReadOnlyList<ConsoleRef> list;
            lock (gate) list = refs;
            var number = int.Parse(typed, CultureInfo.InvariantCulture);
            if (list.Count > 0)
            {
                if (number >= 1 && number <= list.Count && RefTarget(list[number - 1]) is { } numbered) return numbered;
                Say(ConsoleLineKind.Error, $"There's no {number.ToString(CultureInfo.CurrentCulture)} in the last list. It has {list.Count.ToString(CultureInfo.CurrentCulture)}.");
                return null;
            }
        }
        var (name, service) = ConsoleCommandParser.SplitName(text);
        switch (targets.Resolve(name, service))
        {
            case TargetMatch.Unique { Target: var target }: return target;
            case TargetMatch.Ambiguous { Top: var top }: Say(ConsoleLineKind.Error, AmbiguousText(name, top)); return null;
            default: Say(ConsoleLineKind.Error, NobodyText(name)); return null;
        }
    }

    // The conversation a command with no name means: the prompt's target, else the newest shown incoming line's.
    private (string? DisplayId, ChatTarget? Target) Implied()
    {
        lock (gate)
        {
            if ((replyLock ?? query) is { } target)
                return (target.DisplayChatId, target.Target ?? targets.Entries().FirstOrDefault(e => e.DisplayId == target.DisplayChatId));
            if (NewestIncoming() is { } held && (held.Line.DisplayChatId ?? held.Line.SourceChatId) is { } display)
                return (display, targets.Entries().FirstOrDefault(e => e.DisplayId == display));
            return (null, null);
        }
    }

    private async Task OpenAsync(ConsoleCommand.Open open)
    {
        if (open.HitIndex is { } number)
        {
            IReadOnlyList<ConsoleRef> list;
            lock (gate) list = refs;
            if (number < 1 || number > list.Count) { Say(ConsoleLineKind.Error, $"There's no result {number.ToString(CultureInfo.CurrentCulture)}. Use /search words or /ls first."); return; }
            var item = list[number - 1];
            var opened = item.Hit is { } hit ? await Safely(() => host.OpenSearchHitAsync(hit))
                : item.Target is { } chat && await Safely(() => host.OpenAsync(chat.DisplayId));
            if (!opened) Say(ConsoleLineKind.Error, item.Hit is not null ? "Couldn't open that result." : "Couldn't open that conversation.");
            return;
        }
        string? display;
        if (open.Name is { } name) display = Named(name)?.DisplayId;
        else
        {
            display = Implied().DisplayId;
            if (display is null) { Say(ConsoleLineKind.Error, "Nothing to open yet. Use /open name."); return; }
        }
        if (display is null) return;
        if (!await Safely(() => host.OpenAsync(display))) Say(ConsoleLineKind.Error, "Couldn't open that conversation.");
    }

    private static async Task<bool> Safely(Func<Task<bool>> action)
    {
        try { return await action(); }
        catch (Exception) { return false; }
    }

    private async Task ReadAsync(string? name)
    {
        ChatTarget? target;
        if (name is not null) { target = Named(name); if (target is null) return; }
        else target = Implied().Target;
        if (target is null)
        {
            log("console read provider=unknown outcome=unavailable");
            Say(ConsoleLineKind.Info, "Mark read isn't available for this conversation.");
            return;
        }
        await MarkReadAsync(target);
    }

    private async Task MarkReadAsync(ChatTarget target)
    {
        var provider = string.Join("|", target.Networks.Select(ConsoleLineFormatter.Provider).Distinct());
        if (provider.Length == 0) provider = "unknown";
        ReadOutcome outcome;
        try { outcome = await reads.MarkAsync(target, NewestByChat); }
        catch (Exception) { outcome = ReadOutcome.Failed; }
        var word = outcome switch
        {
            ReadOutcome.Complete => "complete",
            ReadOutcome.NothingUnread => "nothing",
            ReadOutcome.Unavailable => "unavailable",
            _ => "failed"
        };
        log($"console read provider={provider} outcome={word}");
        var services = string.Join(", ", target.Networks.Where(n => n.Length > 0));
        Say(outcome == ReadOutcome.Failed ? ConsoleLineKind.Error : ConsoleLineKind.Info, outcome switch
        {
            ReadOutcome.Complete => "Marked read: " + target.Title + (services.Length > 0 ? " · " + services : ""),
            ReadOutcome.NothingUnread => $"Nothing unread in {target.Title}.",
            ReadOutcome.Unavailable => "Mark read isn't available for this conversation.",
            _ => "Couldn't mark it read. Nothing was retried."
        });
    }

    private async Task MuteAsync(string name, bool muted)
    {
        var target = Named(name);
        if (target is null) return;
        await MuteTargetAsync(target, muted);
    }

    private async Task MuteTargetAsync(ChatTarget target, bool muted)
    {
        if (target.Members.Count == 0) return;
        // The row the list shows: a merged person's own id (its mute lives in the merge), else the conversation.
        var chat = MergedConversations.IsMerged(target.DisplayId)
            ? target.Members[0] with { Id = target.DisplayId, Title = target.Title, IsMuted = !muted }
            : target.Members[0];
        try { await host.SetMutedAsync(chat, muted); }
        catch (Exception)
        {
            Say(ConsoleLineKind.Error, muted ? $"Couldn't mute {target.Title}." : $"Couldn't unmute {target.Title}.");
            return;
        }
        lock (gate)
        {
            mutedHere[target.DisplayId] = muted;
            foreach (var member in target.Members) mutedHere[member.Id] = muted;
            Rebuild();
        }
        Say(ConsoleLineKind.Info, muted ? $"Muted {target.Title} in Message Link. /filter muted shows its lines." : $"Unmuted {target.Title}.");
    }

    private void Filter(IReadOnlyList<string> args)
    {
        var first = args[0].ToLowerInvariant();
        if (first == "all")
        {
            lock (gate) { serviceFilter.Clear(); personFilter = null; showMuted = false; Rebuild(); UpdateHint(); }
            return;
        }
        if (first == "muted" && args.Count == 1)
        {
            lock (gate) { showMuted = true; Rebuild(); UpdateHint(); }
            return;
        }
        if (first == "person")
        {
            if (args.Count < 2) { Say(ConsoleLineKind.Error, "Type a name: /filter person name"); return; }
            var target = Named(string.Join(" ", args.Skip(1).Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)));
            if (target is null) return;
            lock (gate)
            {
                personFilter = (target.DisplayId, target.Members.Select(m => m.Id).ToHashSet(StringComparer.Ordinal), target.Title);
                Rebuild(); UpdateHint();
            }
            return;
        }
        var services = new List<(string Network, string? Account, string Label)>();
        foreach (var arg in args)
        {
            if (ConversationTargets.ServiceOf(arg) is not { } label)
            {
                Say(ConsoleLineKind.Error, $"There's no service called “{arg}”. Use google, messenger, instagram, voice, voice:label, person name, muted or all.");
                return;
            }
            var dot = label.IndexOf(" · ", StringComparison.Ordinal);
            var entry = dot < 0 ? (label, (string?)null, label) : (label[..dot], label[(dot + 3)..], label);
            if (!services.Contains(entry)) services.Add(entry);
        }
        lock (gate)
        {
            serviceFilter.Clear();
            serviceFilter.AddRange(services);
            Rebuild(); UpdateHint();
        }
    }

    private async Task SearchAsync(string words)
    {
        // "@messenger" anywhere in the words limits the search to that service.
        string? service = null;
        var kept = new List<string>();
        foreach (var word in words.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.StartsWith('@') && ConversationTargets.ServiceOf(word[1..]) is { } label)
            {
                var dot = label.IndexOf(" · ", StringComparison.Ordinal);
                service = dot < 0 ? label : label[..dot];
            }
            else kept.Add(word);
        }
        var query = string.Join(' ', kept);
        if (query.Length == 0) { Say(ConsoleLineKind.Error, "Type what to search for: /search words"); return; }
        Page<SearchHit> page;
        try { page = await search.SearchAsync(new SearchRequest(query, SearchKind.Messages, null, service is null ? null : new SearchFilters(Service: service))); }
        catch (Exception)
        {
            log("console search outcome=failed");
            Say(ConsoleLineKind.Error, "Search didn't finish. Try again.");
            return;
        }
        var hits = page.Items.Take(SearchShown).ToArray();
        lock (gate) refs = [.. hits.Select(hit => new ConsoleRef(TargetOf(hit.Chat.Id), hit))];
        log($"console search hits={hits.Length.ToString(CultureInfo.InvariantCulture)}");
        if (hits.Length == 0) { Say(ConsoleLineKind.Info, $"No messages found for “{query}”."); return; }
        for (var index = 0; index < hits.Length; index++) Emit(Hit(index + 1, hits[index], query));
        if (page.HasMore || page.Items.Count > hits.Length)
            Out($"First {hits.Length.ToString(CultureInfo.CurrentCulture)} shown. /open N opens one, /thread N inspects its conversation.", ConsoleTone.Dim);
    }

    private ChatTarget? TargetOf(string chatId)
    {
        var display = merges.DisplayId(chatId);
        return targets.Entries().FirstOrDefault(t => t.DisplayId == display || t.Members.Any(m => m.Id == chatId));
    }

    // "[1] 23 Sep  Messenger  Alex: …snippet", the words found in the match ink.
    private ConsoleOutLine Hit(int number, SearchHit hit, string words)
    {
        var chat = hit.Chat;
        var display = merges.DisplayId(chat.Id);
        var title = (display != chat.Id ? merges.Find(display)?.Title : null) ?? (string.IsNullOrWhiteSpace(chat.Title) ? "Someone" : chat.Title.Trim());
        var name = chat.Type == "group" && hit.Message is { IsOutgoing: false, SenderName: { Length: > 0 } sender } ? title + " › " + sender : title;
        var at = hit.Message?.Timestamp ?? chat.LastActivity ?? clock.GetUtcNow();
        var text = hit.Message is { } message
            ? SearchSnippet.Excerpt(ConsoleLineFormatter.Words(message), words, 80)
            : "";
        var snippet = new ConsoleOutLine([new ConsoleSpan(text)]);
        foreach (var word in words.Split(' ', StringSplitOptions.RemoveEmptyEntries)) snippet = ConsoleShell.Highlight(snippet, word);
        return new ConsoleOutLine([
            new ConsoleSpan($"[{number.ToString(CultureInfo.CurrentCulture)}] ", ConsoleTone.Accent),
            new ConsoleSpan(formatter.ShortDate(at) + "  ", ConsoleTone.Dim),
            ConsoleSpan.For(ConsoleLineFormatter.Tag(chat.Network), BubbleColours.TagFamily(chat.Network, hit.Message?.Transport)),
            new ConsoleSpan("  "), new ConsoleSpan(name, ConsoleTone.Strong),
            .. text.Length > 0 ? [new ConsoleSpan(": "), .. snippet.Spans] : Array.Empty<ConsoleSpan>(),
        ]);
    }

    // ---- Typing strip ----

    // Whether a conversation's typing shows under the current filter: never spam or archived; muted only with /filter muted.
    private bool ChatShown(string chatId)
    {
        lock (gate)
        {
            var chat = merges.Source(chatId);
            if (chat is { IsSpam: true }) return false;
            var display = merges.DisplayId(chatId);
            var group = display != chatId ? merges.Find(display) : null;
            if (group?.Archived ?? chat?.IsArchived ?? false) return false;
            var muted = mutedHere.TryGetValue(display, out var here) ? here : group?.Muted ?? chat?.IsMuted ?? false;
            if (muted && !showMuted) return false;
            var network = chat?.Network ?? ServiceScopes.Name(chatId);
            if (serviceFilter.Count > 0 && !serviceFilter.Any(f => ServiceMatches(f, network, formatter.RouteLabel(chatId, network, chat?.AccountId))))
                return false;
            if (personFilter is { } person && person.DisplayId != display && !person.Members.Contains(chatId)) return false;
            return true;
        }
    }

    // ---- Threads and timers ----

    private void OnUi(Action action)
    {
        if (ui is null || SynchronizationContext.Current == ui) { lock (gate) action(); return; }
        ui.Post(_ => { lock (gate) action(); }, null);
    }

    private async Task LaterAsync(TimeSpan delay, int version, Action action)
    {
        try { await Task.Delay(delay, clock); }
        catch (Exception) { return; }
        OnUi(() => { if (!disposed && version == generation) action(); });
    }
}
