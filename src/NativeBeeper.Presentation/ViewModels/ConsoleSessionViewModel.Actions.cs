using System.Globalization;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

// Acting on a conversation or a message from the console: /msgs and the message references it numbers, then the
// conversation menu's actions (archive, pin, unread, delete, labels, snooze) and the message menu's (react, reply, edit,
// unsend, delete, copy, save, remind). Every one goes through the host, which calls the same action the menu does, with
// the same checks; nothing is retried, and an uncertain outcome is said to be unknown. /msgs reads only what this PC
// already holds and marks nothing read. The log gets the command, the provider and the outcome; never words or names.
public sealed partial class ConsoleSessionViewModel
{
    public const int MsgsDefault = 10, MsgsMax = 50, MsgsAllDefault = 20, MsgsAllMax = 200;
    /// <summary>How long a /delete or /unsend waits for its "yes".</summary>
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(60);

    // A message the console can act on: the provider conversation it is in, and the named conversation shown for it.
    private sealed record MessageRef(ChatSummary Chat, ChatMessage Message, ChatTarget? Target);
    // The last /msgs: what m1, m2 … mean.
    private IReadOnlyList<MessageRef> messageRefs = [];
    // A /delete or /unsend waiting for its yes: the words typed, the thing they named, and when.
    private sealed record Confirmation(string Verb, string Typed, string Key, DateTimeOffset At);
    private Confirmation? confirming;

    private static readonly HashSet<string> ChatVerbs = ["archive", "unarchive", "pin", "unpin", "unread", "label", "unlabel", "snooze"];

    private async Task ActAsync(ConsoleCommand.Act act, bool direct)
    {
        Command(act.Verb);
        if (ConsoleCatalog.Find(act.Verb) is { Acts: true } && !direct) { Fail(NotAlone(act.Verb)); return; }
        switch (act.Verb)
        {
            case "msgs": await MsgsAsync(act.Ref, act.Rest); return;
            case "delete" or "unsend": await DeleteAsync(act); return;
            case var verb when ChatVerbs.Contains(verb): await ChatActAsync(verb, act.Ref!, act.Rest); return;
            default: await MessageActAsync(act.Verb, act.Ref!, act.Rest); return;
        }
    }

    private static string NotAlone(string verb) =>
        $"/{verb} changes a conversation or a message, so it runs only typed on a line of its own: not in a chain, a pipe or an alias.";

    // ---- /msgs ----

    private async Task MsgsAsync(string? typed, string countText)
    {
        var all = typed is "*" or "all";
        var count = all ? MsgsAllDefault : MsgsDefault;
        if (countText.Length > 0)
        {
            var max = all ? MsgsAllMax : MsgsMax;
            if (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1 || count > max)
            { Fail($"Show between 1 and {max.ToString(CultureInfo.CurrentCulture)} messages{(all ? " from each conversation" : "")}."); return; }
        }
        if (all) { await MsgsEverywhereAsync(count); return; }
        ChatTarget? target;
        if (typed is not null) { target = Named(typed); if (target is null) return; }
        else
        {
            target = Target is { } to ? to.Target ?? TargetOf(to.SourceChatId) : null;
            target ??= Implied().Target ?? OpenTarget();
            if (target is null) { Fail("Which conversation? /msgs name, /msgs N, /msgs . or /msgs * for every one."); return; }
        }
        var kept = await KeptAsync(target);
        var shown = kept.TakeLast(count).ToArray();
        lock (gate) messageRefs = [.. shown];
        log($"console msgs conversations=1 held={kept.Count.ToString(CultureInfo.InvariantCulture)} shown={shown.Length.ToString(CultureInfo.InvariantCulture)}");
        if (shown.Length == 0) { Out($"Nothing from {target.Title} is kept on this PC yet. Opening it in Messages keeps its history; /search looks deeper.", ConsoleTone.Dim); return; }
        var services = string.Join(", ", target.Networks.Where(n => n.Length > 0).Distinct());
        Out(new ConsoleSpan(target.Title, ConsoleTone.Strong), new ConsoleSpan($" · {services} · the last {shown.Length.ToString(CultureInfo.CurrentCulture)} kept here, newest last", ConsoleTone.Dim));
        for (var i = 0; i < shown.Length; i++) Emit(MessageLine(i + 1, shown[i], withConversation: false));
        Out("/react m1 👍, /reply m1 text, /copy m1 … act on one. Nothing was marked read.", ConsoleTone.Dim);
    }

    // Every conversation's kept messages, read one conversation at a time so the console keeps answering meanwhile.
    private async Task MsgsEverywhereAsync(int count)
    {
        var chats = targets.Entries().Where(c => !c.Members.All(m => m.IsCallHistory)).ToArray();
        var gathered = new List<MessageRef>();
        var empty = 0;
        foreach (var target in chats)
        {
            var kept = await KeptAsync(target);
            if (kept.Count == 0) empty++;
            gathered.AddRange(kept.TakeLast(count));
            await Task.Yield();
        }
        var shown = gathered.OrderBy(m => m.Message.Timestamp).ToArray();
        lock (gate) messageRefs = [.. shown];
        log($"console msgs conversations={chats.Length.ToString(CultureInfo.InvariantCulture)} held={shown.Length.ToString(CultureInfo.InvariantCulture)} empty={empty.ToString(CultureInfo.InvariantCulture)}");
        for (var i = 0; i < shown.Length; i++) Emit(MessageLine(i + 1, shown[i], withConversation: true));
        var note = empty == 0 ? "" : $"{empty.ToString(CultureInfo.CurrentCulture)} conversation{(empty == 1 ? " has" : "s have")} nothing kept on this PC. ";
        Out(note + "/search words looks deeper: Messenger's full store and Google's archive.", ConsoleTone.Dim);
    }

    // "m3  23 Sep 14:02  Alex: words", or with the conversation and service first for /msgs *.
    private ConsoleOutLine MessageLine(int number, MessageRef item, bool withConversation)
    {
        var message = item.Message;
        var title = item.Target?.Title ?? (string.IsNullOrWhiteSpace(item.Chat.Title) ? "Someone" : item.Chat.Title.Trim());
        var sender = message.IsOutgoing ? "You" : item.Chat.Type == "group" || item.Target?.IsGroup == true ? message.SenderName is { Length: > 0 } name ? name : "Someone" : title;
        var words = ConsoleLineFormatter.Words(message).Replace("\n", " ↵ ", StringComparison.Ordinal);
        var family = BubbleColours.TagFamily(item.Chat.Network, message.Transport);
        var time = formatter.Time(message.Timestamp, true);
        List<ConsoleSpan> spans = [new ConsoleSpan(("m" + number.ToString(CultureInfo.InvariantCulture)).PadRight(5), ConsoleTone.Accent)];
        if (withConversation)
            spans.AddRange([new ConsoleSpan(title, ConsoleTone.Strong), new ConsoleSpan(" · ", ConsoleTone.Dim),
                ConsoleSpan.For(ConsoleLineFormatter.Tag(item.Chat.Network), family), new ConsoleSpan(" · " + time + " · ", ConsoleTone.Dim)]);
        else spans.Add(new ConsoleSpan(time + "  ", ConsoleTone.Dim));
        spans.Add(new ConsoleSpan(sender, message.IsOutgoing ? ConsoleTone.Accent : ConsoleTone.Default));
        spans.Add(new ConsoleSpan(": " + words));
        if (message.Reactions.Count > 0) spans.Add(new ConsoleSpan("  " + string.Concat(message.Reactions.Select(r => r.Key).Distinct()), ConsoleTone.Dim));
        return new ConsoleOutLine(spans);
    }

    // A conversation's messages as this PC holds them, every member's, oldest first, without repeats: the saved copy
    // (memory first, then disk) and whatever newer the console itself has seen. Never a provider read.
    private async Task<IReadOnlyList<MessageRef>> KeptAsync(ChatTarget target)
    {
        var all = new Dictionary<(string, string), MessageRef>();
        foreach (var member in target.Members)
        {
            HistorySnapshot? snapshot;
            try { snapshot = await host.SavedHistoryAsync(member.Id); }
            catch (Exception) { snapshot = null; }
            foreach (var message in snapshot?.Messages ?? []) all.TryAdd((message.ChatId, message.Id), new(member, message, target));
            if (NewestByChat(member.Id) is { } seen) all.TryAdd((seen.ChatId, seen.Id), new(member, seen, target));
        }
        return [.. all.Values.Where(m => !m.Message.IsHidden && !m.Message.Id.StartsWith("local:", StringComparison.Ordinal))
            .OrderBy(m => m.Message.Timestamp)];
    }

    // ---- Message references ----

    // m3, ^, ^2, #4 or 4; null (with the reason said) when it names no message.
    private async Task<MessageRef?> MessageAsync(string typed)
    {
        var word = typed.Trim();
        if (word.Length > 1 && (word[0] is 'm' or 'M') && word[1..].All(char.IsAsciiDigit))
        {
            IReadOnlyList<MessageRef> list;
            lock (gate) list = messageRefs;
            var n = int.Parse(word[1..], CultureInfo.InvariantCulture);
            if (list.Count == 0) { Fail("There are no message numbers yet. /msgs name lists messages as m1, m2 …"); return null; }
            if (n < 1 || n > list.Count) { Fail($"There's no m{n.ToString(CultureInfo.InvariantCulture)}. The last /msgs listed m1 to m{list.Count.ToString(CultureInfo.InvariantCulture)}."); return null; }
            return list[n - 1];
        }
        if (word.StartsWith('^'))
        {
            var n = word.Length == 1 ? 1 : int.Parse(word[1..], CultureInfo.InvariantCulture);
            var to = Target;
            var target = to is null ? null : to.Target ?? TargetOf(to.SourceChatId);
            if (target is null) { Fail("^ is the newest message from the person the prompt shows, and no one is selected. /to name, or /msgs name for numbers."); return null; }
            var theirs = (await KeptAsync(target)).Where(m => !m.Message.IsOutgoing && !m.Message.IsDeleted).Reverse().ToArray();
            if (n < 1 || n > theirs.Length)
            {
                Fail(theirs.Length == 0 ? $"Nothing from {target.Title} is kept on this PC yet." : $"There's no {word}: {theirs.Length.ToString(CultureInfo.CurrentCulture)} message{(theirs.Length == 1 ? "" : "s")} from {target.Title} kept here.");
                return null;
            }
            return theirs[n - 1];
        }
        var number = word.StartsWith('#') ? word[1..] : word;
        if (number.Length is > 0 and <= 4 && number.All(char.IsAsciiDigit))
        {
            IReadOnlyList<ConsoleRef> list;
            lock (gate) list = refs;
            var n = int.Parse(number, CultureInfo.InvariantCulture);
            if (n < 1 || n > list.Count) { Fail(list.Count == 0 ? "There's no list yet. /search words numbers messages; /msgs name numbers m1, m2 …" : $"There's no {n.ToString(CultureInfo.CurrentCulture)} in the last list. It has {list.Count.ToString(CultureInfo.CurrentCulture)}."); return null; }
            if (list[n - 1] is { Hit: { Message: { } found } hit }) return new MessageRef(hit.Chat, found, RefTarget(list[n - 1]));
            Fail($"{n.ToString(CultureInfo.CurrentCulture)} in the last list is a conversation, not a message. /msgs {n.ToString(CultureInfo.CurrentCulture)} numbers its messages.");
            return null;
        }
        Fail($"“{word}” isn't a message. Use m3 from /msgs, a /search number, or ^ for the newest message from the person the prompt shows.");
        return null;
    }

    private static string Service(ChatSummary chat) => chat.Network.Length > 0 ? chat.Network : ServiceScopes.Name(chat.Id);

    // "Alex's message" / "your message", for what a command did.
    private static string Whose(MessageRef item) =>
        item.Message.IsOutgoing ? "your message" : (item.Chat.Type == "group" ? item.Message.SenderName : item.Target?.Title ?? item.Chat.Title) is { Length: > 0 } name ? name + "'s message" : "their message";

    private static string Preview(ChatMessage message) => ConsoleLineFormatter.Quote(ConsoleLineFormatter.Words(message));

    // The message menu's options; null (said) when the host can't say, which counts as nothing offered.
    private async Task<MessageActionOptions?> OptionsAsync(MessageRef item, string what)
    {
        MessageActionOptions? options;
        try { options = await host.MessageOptionsAsync(item.Message); }
        catch (Exception) { options = null; }
        if (options is null) Fail($"{Service(item.Chat)} can't {what} this message from here right now (it isn't connected, or Messages can't say). Nothing was sent.");
        return options;
    }

    // ---- Message commands ----

    private async Task MessageActAsync(string verb, string typed, string rest)
    {
        if (await MessageAsync(typed) is not { } item) return;
        var provider = ConsoleLineFormatter.Provider(Service(item.Chat));
        switch (verb)
        {
            case "copy":
                var words = item.Message.IsDeleted || item.Message.IsHidden ? "" : MessageText.PlainText(item.Message.Text ?? "");
                if (string.IsNullOrWhiteSpace(words)) { Fail("That message has no words to copy."); return; }
                try { host.Copy(words); } catch (Exception) { Fail("The clipboard is busy. Try again."); return; }
                log("console act name=copy provider=" + provider + " outcome=done");
                Out($"Copied the words of {Whose(item)} ({words.Length.ToString(CultureInfo.CurrentCulture)} characters).", ConsoleTone.Dim);
                return;
            case "save":
                if (item.Message.IsDeleted || item.Message.IsHidden) { Fail("A deleted message can't be saved."); return; }
                Report("save", provider, await Safely(() => host.SaveMessageAsync(item.Chat, item.Message)),
                    $"Saved {Whose(item)} in Saved Messages.", "Couldn't save it. Check that its account is still signed in.");
                return;
            case "remind":
                if (ConsoleCommandParser.When(rest, clock.GetUtcNow(), clock.LocalTimeZone) is not { } at)
                { Fail($"“{rest}” isn't a time to remind you. Use 30m, 1h, tonight, tomorrow or a time like 17:30."); return; }
                Report("remind", provider, await Safely(() => host.RemindAsync(item.Chat, item.Message, at)),
                    $"You'll be reminded about {Whose(item)} {Moment(at)}. It's in Saved Messages.", "Couldn't set the reminder. Check that its account is still signed in.");
                return;
        }
        var what = verb switch { "edit" => "edit", "unreact" => "change reactions on", _ => "react to" };
        if (await OptionsAsync(item, what) is not { } options) return;
        var service = Service(item.Chat);
        switch (verb)
        {
            case "react" or "like":
            {
                if (options.AddReactions.Count == 0 && options.RemoveReactions.Count == 0) { Fail($"{service} can't react to this message."); return; }
                string? key;
                if (verb == "like")
                {
                    var preferred = service == "Instagram" ? "❤️" : "👍";
                    key = Match(preferred, options.AddReactions) ?? (Match(preferred, options.RemoveReactions) is null ? options.AddReactions.FirstOrDefault() : null);
                    if (key is null) { Fail($"You've already liked {Whose(item)} ({preferred}). /unreact {typed} takes it back."); return; }
                }
                else
                {
                    var wanted = Reaction(rest);
                    key = Match(wanted, options.AddReactions);
                    if (key is null)
                    {
                        Fail(Match(wanted, options.RemoveReactions) is { } mine ? $"You've already reacted {mine}. /unreact {typed} {mine} takes it back."
                            : options.AddReactions.Count == 0 ? $"{service} offers no more reactions on this message."
                            : $"{service} doesn't offer {wanted} here. It offers {string.Join(" ", options.AddReactions)}.");
                        return;
                    }
                }
                Report(verb, provider, await Safely(() => host.ReactAsync(item.Message, key, false)),
                    $"Reacted {key} to {Whose(item)}. {service} confirms it in the conversation.", "Not reacted.");
                return;
            }
            case "unreact":
            {
                if (options.RemoveReactions.Count == 0) { Fail($"You haven't reacted to {Whose(item)}."); return; }
                string? key;
                if (rest.Length == 0)
                {
                    if (options.RemoveReactions.Count > 1) { Fail($"You reacted {string.Join(" and ", options.RemoveReactions)}. Say which: /unreact {typed} {options.RemoveReactions[0]}"); return; }
                    key = options.RemoveReactions[0];
                }
                else if ((key = Match(Reaction(rest), options.RemoveReactions)) is null) { Fail($"You didn't react {rest} to it. Yours: {string.Join(" ", options.RemoveReactions)}."); return; }
                Report("unreact", provider, await Safely(() => host.ReactAsync(item.Message, key, true)),
                    $"Took back {key} from {Whose(item)}.", "Not changed.");
                return;
            }
            case "edit":
            {
                if (!item.Message.IsOutgoing) { Fail("Only your own messages can be edited."); return; }
                if (!options.CanEdit) { Fail($"{service} can't edit this message."); return; }
                if (rest.Length > options.MaxEditLength) { Fail($"That's longer than {service} allows ({options.MaxEditLength.ToString("N0", CultureInfo.CurrentCulture)} characters)."); return; }
                Report("edit", provider, await Safely(() => host.EditAsync(item.Message, rest)),
                    $"Edited your message. Everyone in the conversation sees the new words.", "Not edited.");
                return;
            }
        }
    }

    // ---- /reply m3 text ----

    private async Task QuotedReplyAsync(ConsoleCommand.QuotedReply reply)
    {
        Command("reply");
        if (await MessageAsync(reply.Ref) is not { } item) return;
        if (await OptionsAsync(item, "reply to") is not { } options) return;
        var chat = item.Chat;
        if (!options.CanReply) { Fail($"{Service(chat)} can't reply to this message."); return; }
        if (!sender.CanReply) { Fail("Replies to a message aren't available here."); return; }
        var display = merges.DisplayId(chat.Id);
        var target = item.Target ?? TargetOf(chat.Id);
        var to = new ConsoleRecipient(chat.Id, display, target?.Title ?? (string.IsNullOrWhiteSpace(chat.Title) ? "Someone" : chat.Title.Trim()),
            merges.ServiceRouteLabel(chat), chat.Network, chat.AccountId, target);
        await SendAsync(to, reply.Text, item.Message.Id);
    }

    // ---- Conversation commands ----

    // The row the conversation list shows for a named conversation: a linked person's own row, else the conversation.
    private static ChatSummary RowOf(ChatTarget target) =>
        MergedConversations.IsMerged(target.DisplayId)
            ? target.Members[0] with
            {
                Id = target.DisplayId, Title = target.Title, IsPinned = target.Members.Any(m => m.IsPinned),
                IsArchived = target.Members.All(m => m.IsArchived), IsMarkedUnread = target.Members.Any(m => m.IsMarkedUnread),
                UnreadCount = target.Members.Sum(m => m.UnreadCount),
            }
            : target.Members[0];

    private static bool KeptHere(ChatSummary row) => MergedConversations.IsMerged(row.Id) || row.Network == "Instagram";

    private async Task ChatActAsync(string verb, string typed, string rest)
    {
        if (Named(typed) is not { } target || target.Members.Count == 0) return;
        var row = RowOf(target);
        var provider = string.Join("|", target.Networks.Select(ConsoleLineFormatter.Provider).Distinct());
        if (provider.Length == 0) provider = "unknown";
        switch (verb)
        {
            case "archive" or "unarchive":
            {
                var archived = verb == "archive";
                if (row.IsArchived == archived) { Out(archived ? $"{target.Title} is already archived." : $"{target.Title} isn't archived.", ConsoleTone.Dim); return; }
                var where = KeptHere(row) ? "in Messages" : "on " + Service(row);
                var why = row.Network == "Instagram" && !MergedConversations.IsMerged(row.Id) ? " (Instagram can't archive from Messages)" : "";
                Report(verb, provider, await Safely(() => host.SetArchivedAsync(row, archived)),
                    (archived ? "Archived " : "Unarchived ") + target.Title + " " + where + why + ".", "Not changed. Check your phone before trying again.");
                return;
            }
            case "pin" or "unpin":
            {
                var pinned = verb == "pin";
                if (row.IsPinned == pinned) { Out(pinned ? $"{target.Title} is already pinned." : $"{target.Title} isn't pinned.", ConsoleTone.Dim); return; }
                Report(verb, provider, await Safely(() => host.SetPinnedAsync(row, pinned)), (pinned ? "Pinned " : "Unpinned ") + target.Title + " in Messages.", "Not changed.");
                return;
            }
            case "unread":
                // The menu offers Mark as unread only where nothing is unread already.
                if (row.IsMarkedUnread) { Out($"{target.Title} is already marked unread.", ConsoleTone.Dim); return; }
                if (row.UnreadCount > 0) { Out($"{target.Title} already has unread messages.", ConsoleTone.Dim); return; }
                Report("unread", provider, await Safely(() => host.MarkUnreadAsync(row)), $"Marked {target.Title} unread in Messages. The service isn't told.", "Not changed.");
                return;
            case "label" or "unlabel":
            {
                var names = host.LabelNames;
                if (names.Count == 0) { Fail("There are no labels yet. Make one from a conversation's menu, Labels."); return; }
                if (names.FirstOrDefault(n => n.Equals(rest, StringComparison.CurrentCultureIgnoreCase)) is not { } label)
                { Fail($"There's no label called “{rest}”. Yours: {string.Join(", ", names)}."); return; }
                var on = verb == "label";
                Report(verb, provider, await Safely(() => host.SetLabelAsync(row, label, on)),
                    on ? $"Labelled {target.Title} {label}, in Messages." : $"Took {label} off {target.Title}.", "Not changed.");
                return;
            }
            case "snooze":
            {
                DateTimeOffset? until = null;
                if (!rest.Equals("off", StringComparison.OrdinalIgnoreCase) &&
                    (until = ConsoleCommandParser.When(rest, clock.GetUtcNow(), clock.LocalTimeZone)) is null)
                { Fail($"“{rest}” isn't a time. Use 30m, 1h, tonight, tomorrow, a time like 17:30, or off."); return; }
                Report("snooze", provider, await Safely(() => host.SnoozeAsync(row, until)),
                    until is { } at ? $"Snoozed {target.Title}: notifications wait, and you'll be reminded {Moment(at)}. In Messages." : $"Called off the reminder for {target.Title}.",
                    "Couldn't save it. Try again.");
                return;
            }
        }
    }

    // ---- /delete and /unsend: say what will happen, then do it on "yes" ----

    private async Task DeleteAsync(ConsoleCommand.Act act)
    {
        var typed = act.Ref!.Trim();
        var message = act.Verb == "unsend" || ConsoleCommandParser.IsMessageRef(typed, numbers: false) || IsMessageRow(typed);
        string key, question, provider;
        Func<Task<ConsoleActionResult>> action;
        string done, failed;
        if (message)
        {
            if (await MessageAsync(typed) is not { } item) return;
            if (await OptionsAsync(item, act.Verb == "unsend" ? "unsend" : "delete") is not { } options) return;
            var service = Service(item.Chat);
            bool everyone;
            if (act.Verb == "unsend")
            {
                if (!item.Message.IsOutgoing) { Fail("Only your own messages can be unsent. /delete removes one for you only, where its service can."); return; }
                if (!options.CanDeleteForEveryone) { Fail($"{service} can't unsend this message."); return; }
                everyone = true;
            }
            else if (options.CanDeleteForMe) everyone = false;
            else if (options.CanDeleteForEveryone) everyone = true;
            else { Fail($"{service} can't delete this message."); return; }
            key = "message\n" + item.Message.ChatId + "\n" + item.Message.Id;
            // The message menu's dialog, word for word.
            question = (everyone ? "Delete for everyone: Request removal of this message for everyone in the conversation?"
                : "Delete for me: Remove this message only for your account? Other participants keep their copy.") + " " + Preview(item.Message);
            provider = ConsoleLineFormatter.Provider(service);
            action = () => host.DeleteMessageAsync(item.Message, everyone);
            done = everyone ? $"Asked {service} to remove {Whose(item)} for everyone." : $"Deleted {Whose(item)} for you.";
            failed = "Not deleted.";
        }
        else
        {
            if (Named(typed) is not { } target || target.Members.Count == 0) return;
            var row = RowOf(target);
            string? asked;
            try { asked = host.DeleteChatQuestion(row); }
            catch (Exception) { asked = null; }
            if (asked is null)
            {
                Fail(MergedConversations.IsMerged(row.Id) ? $"{target.Title} links several conversations; delete each one from its own conversation."
                    : $"{Service(row)} conversations can't be deleted from Messages.");
                return;
            }
            key = "chat\n" + row.Id;
            question = "Delete this conversation? " + asked;
            provider = ConsoleLineFormatter.Provider(Service(row));
            action = () => host.DeleteChatAsync(row);
            done = $"Deleted {target.Title}.";
            failed = "The delete outcome is unknown. Check your phone before trying again.";
        }
        var now = clock.GetUtcNow();
        if (!act.Confirmed)
        {
            lock (gate) confirming = new Confirmation(act.Verb, typed, key, now);
            log($"console confirm name={act.Verb} kind={(message ? "message" : "chat")} provider={provider}");
            Out(question, ConsoleTone.Warn);
            Out(new ConsoleSpan("Type ", ConsoleTone.Dim), new ConsoleSpan($"/{act.Verb} {typed} yes", ConsoleTone.Strong),
                new ConsoleSpan($" within {((int)ConfirmWindow.TotalSeconds).ToString(CultureInfo.CurrentCulture)} seconds to {(act.Verb == "unsend" ? "unsend" : "delete")} it.", ConsoleTone.Dim));
            return;
        }
        Confirmation? waiting;
        lock (gate) { waiting = confirming; confirming = null; }
        if (waiting is null || waiting.Verb != act.Verb || !string.Equals(waiting.Typed, typed, StringComparison.OrdinalIgnoreCase))
        { log($"console confirm name={act.Verb} outcome=nothing_waiting"); Fail($"Nothing is waiting for that yes. Type /{act.Verb} {typed} first: it says what will happen."); return; }
        if (now - waiting.At > ConfirmWindow)
        { log($"console confirm name={act.Verb} outcome=expired"); Fail($"That waited more than {((int)ConfirmWindow.TotalSeconds).ToString(CultureInfo.CurrentCulture)} seconds. Type /{act.Verb} {typed} again."); return; }
        if (waiting.Key != key)
        { log($"console confirm name={act.Verb} outcome=changed"); Fail($"{typed} means something else now (the list changed). Nothing was deleted. Type /{act.Verb} {typed} again."); return; }
        Report(act.Verb, provider, await Safely(action), done, failed);
    }

    // A number of the last list that is a search result with a message: /delete 3 then means that message.
    private bool IsMessageRow(string typed)
    {
        if (typed.Length is 0 or > 4 || !typed.All(char.IsAsciiDigit)) return false;
        IReadOnlyList<ConsoleRef> list;
        lock (gate) list = refs;
        var n = int.Parse(typed, CultureInfo.InvariantCulture);
        return n >= 1 && n <= list.Count && list[n - 1].Hit?.Message is not null;
    }

    // ---- Saying what happened ----

    private static async Task<ConsoleActionResult> Safely(Func<Task<ConsoleActionResult>> action)
    {
        try { return await action(); }
        catch (Exception) { return new ConsoleActionResult(ConsoleActionOutcome.Unknown); }
    }

    private void Report(string verb, string provider, ConsoleActionResult result, string done, string refused)
    {
        var outcome = result.Outcome switch
        {
            ConsoleActionOutcome.Done => "done",
            ConsoleActionOutcome.Unavailable => "unavailable",
            ConsoleActionOutcome.Refused => "refused",
            _ => "unknown",
        };
        log($"console act name={verb} provider={provider} outcome={outcome}");
        switch (result.Outcome)
        {
            case ConsoleActionOutcome.Done: Out(done, ConsoleTone.Ok); return;
            case ConsoleActionOutcome.Unavailable: Fail(result.Message ?? $"/{verb} isn't available here."); return;
            case ConsoleActionOutcome.Refused: Fail(result.Message is { Length: > 0 } why ? why : refused); return;
            default: Fail((result.Message is { Length: > 0 } said ? said + " " : "") + "The outcome is unknown. Check the conversation (/open) before trying again; nothing was retried."); return;
        }
    }

    // "at 8:00 PM", "tomorrow at 8:00 AM", or "on 3 Oct at 9:00 AM".
    private string Moment(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, clock.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone).Date;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        return local.Date == today ? "at " + time : local.Date == today.AddDays(1) ? "tomorrow at " + time
            : "on " + local.ToString("d MMM", CultureInfo.CurrentCulture) + " at " + time;
    }

    // An emoji as typed, or one by name ("heart", "laugh") from the emoji picker's catalogue.
    private static string Reaction(string typed)
    {
        var value = typed.Trim().Trim(':');
        if (value.Length > 0 && value.Any(char.IsLetter) && value.All(c => char.IsLetter(c) || c is ' ' or '-' or '_'))
            return EmojiCatalog.Search(value.Replace('_', ' ')).FirstOrDefault()?.Glyph ?? value;
        return value;
    }

    // The offered key a typed emoji means, ignoring variation selectors: ❤ and ❤️ are the same reaction.
    private static string? Match(string wanted, IReadOnlyList<string> offered)
    {
        static string Bare(string glyph) => glyph.Replace("️", "", StringComparison.Ordinal).Replace("︎", "", StringComparison.Ordinal);
        var bare = Bare(wanted);
        return offered.FirstOrDefault(key => Bare(key) == bare);
    }

    /// <summary>The message references Tab offers: ^, ^2, and the last /msgs numbers, newest first.</summary>
    private IReadOnlyList<string> MessageRefWords()
    {
        int count;
        lock (gate) count = messageRefs.Count;
        return ["^", "^2", .. Enumerable.Range(1, count).Reverse().Take(20).Select(n => "m" + n.ToString(CultureInfo.InvariantCulture))];
    }
}
