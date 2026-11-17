using System.Globalization;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

// Hermes threads from the console. /new and /repeat act on the Hermes conversation the prompt shows, and only there. What Hermes has no such
// thing for (a reply to a message, reactions, edits, removing a message, archive, delete) is refused here by name, before anything is asked of
// the server. Hermes only ever gets what is typed in its own thread: nothing here hands it another conversation.
public sealed partial class ConsoleSessionViewModel
{
    // What a Hermes thread has no such thing for, in the words the console says it with; null for what it has.
    private static string? HermesRefusal(string verb) => verb switch
    {
        "react" or "like" or "unreact" => "Hermes conversations don't take reactions.",
        "edit" => "Messages in a Hermes conversation can't be edited.",
        "unsend" or "delete" => "Messages in a Hermes conversation can't be removed.",
        "reply" => "Hermes conversations don't take replies to a message. Type your message instead.",
        "archive" or "unarchive" => "Hermes conversations can't be archived.",
        "delete_chat" => "Hermes conversations can't be deleted.",
        _ => null,
    };

    private static bool IsHermes(ChatTarget target) => target.Members.Any(m => HermesIds.Owns(m.Id));

    // The Hermes thread the prompt shows; null (said) when it shows none.
    private string? HermesThread(string command)
    {
        var to = Target;
        if (to is not null && HermesIds.Owns(to.SourceChatId)) return to.SourceChatId;
        Fail(to is null ? $"/{command} works in a Hermes conversation, and no one is selected. Use /to Hermes first."
            : $"/{command} works only in a Hermes conversation, and {to.Title} isn't one.");
        return null;
    }

    private string HermesTitle(string chatId) => Target is { } to && to.SourceChatId == chatId ? to.Title : "Hermes";

    // ---- /new ----

    private async Task HermesNewAsync()
    {
        if (HermesThread("new") is not { } chatId) return;
        Report("new", "hermes", await Safely(() => host.NewHermesConversationAsync(chatId)),
            $"Started a new conversation with {HermesTitle(chatId)}. It doesn't see what was said before; the earlier messages stay here.", "Not started.");
    }

    // ---- /approve and /deny ----

    // The last list /approve or /deny printed: its conversation and the requests' message ids, in the order it numbered them.
    private (string Chat, string[] Ids)? approvalRefs;

    // A command Hermes holds for approval in the conversation the prompt shows: the only one waiting, or the one numbered when several wait. Approve runs it
    // once; nothing broader is offered. Read from the server's copy, which carries how each request stands; asked once, never retried.
    private async Task HermesAnswerAsync(string verb, string rest)
    {
        if (HermesThread(verb) is not { } chatId) return;
        int? number = null;
        if (rest.Length > 0)
        {
            if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var typed) || typed < 1) { Fail($"/{verb} takes a request's number: /{verb} 2."); return; }
            number = typed;
        }
        var target = Target is { } to ? to.Target ?? TargetOf(to.SourceChatId) : null;
        if (target is null) { Fail($"/{verb} works in a Hermes conversation. Use /to Hermes first."); return; }
        var now = clock.GetUtcNow();
        var waiting = (await KeptAsync(target, named: true)).Where(m => m.Message.ChatId == chatId && m.Message.Approval is { } a && AgentApprovals.Answerable(a, now)).ToArray();
        log($"console approvals waiting={waiting.Length.ToString(CultureInfo.InvariantCulture)}");
        ChatMessage request;
        if (number is { } n)
        {
            // A number is the one the last list gave, kept: renumbered from what waits now, request 1 gone would make /approve 1 answer what was 2
            // (code review, 2026-11-16).
            (string Chat, string[] Ids)? listed;
            lock (gate) listed = approvalRefs;
            if (listed is not { } list || list.Chat != chatId) { Fail($"/{verb} alone lists the requests waiting in {HermesTitle(chatId)}, numbered; then /{verb} N answers one."); return; }
            if (n > list.Ids.Length)
            {
                Fail($"There's no request {n.ToString(CultureInfo.CurrentCulture)}. The last list had 1 to {list.Ids.Length.ToString(CultureInfo.CurrentCulture)}.");
                return;
            }
            if (waiting.FirstOrDefault(m => m.Message.Id == list.Ids[n - 1]) is not { } still)
            {
                Fail($"Request {n.ToString(CultureInfo.CurrentCulture)} is no longer waiting. /{verb} lists the ones that are.");
                return;
            }
            request = still.Message;
        }
        else
        {
            if (waiting.Length == 0) { Fail($"Nothing in {HermesTitle(chatId)} is waiting for your approval."); return; }
            if (waiting.Length > 1)
            {
                lock (gate) approvalRefs = (chatId, [.. waiting.Select(m => m.Message.Id)]);
                Out(new ConsoleSpan($"{waiting.Length.ToString(CultureInfo.CurrentCulture)} requests wait in {HermesTitle(chatId)}", ConsoleTone.Strong));
                for (var i = 0; i < waiting.Length; i++)
                    Out(new ConsoleSpan((i + 1).ToString(CultureInfo.InvariantCulture).PadRight(3), ConsoleTone.Accent), new ConsoleSpan(waiting[i].Message.Approval!.Command));
                Out($"/{verb} 1 answers the first.", ConsoleTone.Dim);
                return;
            }
            request = waiting[0].Message;
        }
        // A command cut short is never approved here: what would run isn't all shown. The server refuses it too.
        if (verb == "approve" && request.Approval!.Cut) { Fail(AgentApprovals.CutText); return; }
        ConsoleApprovalResult result;
        try { result = await host.AnswerHermesApprovalAsync(request.ChatId, request.Id, verb == "approve"); }
        catch (Exception) { result = new(ConsoleActionOutcome.Unknown); }
        log($"console act name={verb} provider=hermes outcome={result.Outcome.ToString().ToLowerInvariant()} state={(result.State is { } s && s.All(char.IsAsciiLetterLower) ? s : "none")}");
        switch (result.Outcome)
        {
            case ConsoleActionOutcome.Done when result.State is AgentApprovalStates.Approved or AgentApprovalStates.Denied:
                Out(AgentApprovals.Outcome(result.State), ConsoleTone.Ok); return;
            case ConsoleActionOutcome.Done:
                Fail(AgentApprovals.Outcome(result.State ?? AgentApprovalStates.Closed)); return;
            case ConsoleActionOutcome.Unavailable:
                Fail(result.Message ?? "Answering Hermes needs Message Link Server 0.1.468 or later."); return;
            case ConsoleActionOutcome.Refused:
                Fail(result.Message is { Length: > 0 } why ? why : "Not answered."); return;
            default:
                Fail("The outcome is unknown. Check the conversation (/msgs) before answering again; nothing was retried."); return;
        }
    }

    // ---- /repeat ----

    private async Task RepeatAsync(string rest)
    {
        if (HermesThread("repeat") is not { } chatId) return;
        if (!host.KeepsRepeats) { Fail("Repeats are kept by Message Link Server, and this one doesn't keep them."); return; }
        switch (RepeatRequest.Parse(rest))
        {
            case RepeatRequest.Invalid { Message: var why }:
                Fail(why);
                return;
            case RepeatRequest.List:
                await ListRepeatsAsync(chatId);
                return;
            case RepeatRequest.Cancel { Number: var number }:
            {
                var kept = await KeptRepeatsAsync(chatId);
                if (number > kept.Count)
                {
                    Fail(kept.Count == 0 ? $"There are no repeats in {HermesTitle(chatId)}."
                        : $"There's no repeat {number.ToString(CultureInfo.CurrentCulture)}. /repeat lists 1 to {kept.Count.ToString(CultureInfo.CurrentCulture)}.");
                    return;
                }
                var repeat = kept[number - 1];
                var result = await SafelyRepeat(() => host.CancelRepeatAsync(repeat.Id));
                Report("repeat", "hermes", new ConsoleActionResult(result.Outcome, result.Message),
                    $"Stopped asking {ConsoleLineFormatter.Quote(repeat.Text)} {RepeatRequest.When(repeat.Days, repeat.Minute)}.", "Not stopped.");
                return;
            }
            case RepeatRequest.Create { Days: var days, Minute: var minute, Text: var text }:
            {
                var result = await SafelyRepeat(() => host.CreateRepeatAsync(chatId, text, days, minute));
                var next = result.NextDue is { } due ? " Next: " + Moment(due) + "." : "";
                Report("repeat", "hermes", new ConsoleActionResult(result.Outcome, result.Message),
                    $"{HermesTitle(chatId)} will be asked {ConsoleLineFormatter.Quote(text)} {RepeatRequest.When(days, minute)}, the server's time.{next}", "Not set up.");
                return;
            }
        }
    }

    private async Task<IReadOnlyList<RepeatingSend>> KeptRepeatsAsync(string chatId)
    {
        IReadOnlyList<RepeatingSend> kept;
        try { kept = await host.RepeatsAsync(chatId); }
        catch (Exception) { kept = []; }
        return [.. kept.Where(r => r.ChatId == chatId).OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    // "1  Weekdays at 7:00 AM  “Morning brief”  next tomorrow at 7:00 AM · last skipped".
    private async Task ListRepeatsAsync(string chatId)
    {
        var kept = await KeptRepeatsAsync(chatId);
        log($"console repeats shown={kept.Count.ToString(CultureInfo.InvariantCulture)}");
        if (kept.Count == 0) { Out($"No repeats in {HermesTitle(chatId)}. /repeat weekdays 7:00 Morning brief makes one.", ConsoleTone.Dim); return; }
        Out(new ConsoleSpan("Repeats in " + HermesTitle(chatId), ConsoleTone.Strong), new ConsoleSpan(" · times are the server's", ConsoleTone.Dim));
        for (var i = 0; i < kept.Count; i++)
        {
            var repeat = kept[i];
            var last = repeat.LastOutcome is { Length: > 0 } outcome ? " · last " + outcome : "";
            Out(new ConsoleSpan((i + 1).ToString(CultureInfo.InvariantCulture).PadRight(3), ConsoleTone.Accent),
                new ConsoleSpan(RepeatRequest.When(repeat.Days, repeat.Minute) + "  ", ConsoleTone.Strong),
                new ConsoleSpan(ConsoleLineFormatter.Quote(repeat.Text)),
                new ConsoleSpan("  next " + Moment(repeat.NextDue) + last, ConsoleTone.Dim));
        }
        Out("/repeat cancel 1 stops one.", ConsoleTone.Dim);
    }

    private static async Task<ConsoleRepeatResult> SafelyRepeat(Func<Task<ConsoleRepeatResult>> action)
    {
        try { return await action(); }
        catch (Exception) { return new ConsoleRepeatResult(ConsoleActionOutcome.Unknown); }
    }
}
