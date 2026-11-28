using System.Text.Json;
using MessageLink.Core.Backend;
using MessageLink.Core.Messaging;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;
using MessageLink.Protocol;

namespace MessageLink.Terminal.Session;

// Hermes over the server (protocol 31): New conversation and the server's repeats, each a journaled mutation asked once and never retried. The
// repeats themselves are the server's own repeat: rows, which the replica holds; reading them asks the server nothing. Setting Hermes up is the
// server page's, not mlink's.
internal sealed partial class TerminalHost
{
    private bool SpeaksHermes => Link.Client.Version is >= ProtocolVersion.Hermes;

    public async Task<ConsoleActionResult> NewHermesConversationAsync(string chatId)
    {
        if (!SpeaksHermes || !HermesIds.Owns(chatId)) return ConsoleActionResult.Unavailable;
        try
        {
            await new HermesAccountProxy(Link.Client, State).NewConversationAsync(chatId);
            parts.Context.Log("mlink hermes new_conversation outcome=done");
            return ConsoleActionResult.Done;
        }
        catch (Exception failure)
        {
            var result = Outcome(failure);
            parts.Context.Log($"mlink hermes new_conversation outcome={result.Outcome.ToString().ToLowerInvariant()} error={failure.GetType().Name}");
            return new(result.Outcome, result.Message);
        }
    }

    // Protocol 32: a command Hermes holds, answered once and never retried. The server says how the request stands afterwards.
    public async Task<ConsoleApprovalResult> AnswerHermesApprovalAsync(string chatId, string messageId, bool approve)
    {
        if (Link.Client.Version is not >= ProtocolVersion.HermesApprovals || !HermesIds.Owns(chatId)) return ConsoleApprovalResult.Unavailable;
        try
        {
            var state = await new HermesAccountProxy(Link.Client, State).AnswerApprovalAsync(chatId, messageId, approve);
            parts.Context.Log($"mlink hermes approval outcome=done state={state}");
            return new(ConsoleActionOutcome.Done, state);
        }
        catch (Exception failure)
        {
            var result = Outcome(failure);
            parts.Context.Log($"mlink hermes approval outcome={result.Outcome.ToString().ToLowerInvariant()} error={failure.GetType().Name}");
            return new(result.Outcome, null, result.Message);
        }
    }

    public bool KeepsRepeats => SpeaksHermes && State.Limits.Repeats;

    public async Task<IReadOnlyList<RepeatingSend>> RepeatsAsync(string chatId)
    {
        if (!KeepsRepeats || await NotificationIdentity.ProfileAsync(parts.Tokens) is not { } profile) return [];
        var repeats = new List<RepeatingSend>();
        foreach (var (key, value, _) in await parts.Replica.ReadPrefixWithRevAsync(SharedCategories.Repeat + ":"))
        {
            RepeatingSend? repeat = null;
            try { repeat = JsonSerializer.Deserialize<RepeatingSend>(value); } catch (JsonException) { }
            // A row counts only under the key its own id gives it under this profile, as the scheduled sends are read.
            if (repeat is not { Id.Length: > 0 } || repeat.ChatId != chatId || key != SharedCategories.KeyOf(profile, SharedCategories.Repeat, repeat.Id)) continue;
            repeats.Add(repeat);
        }
        return repeats;
    }

    public async Task<ConsoleRepeatResult> CreateRepeatAsync(string chatId, string text, int days, int minute)
    {
        if (!KeepsRepeats || !HermesIds.Owns(chatId)) return ConsoleRepeatResult.Unavailable;
        return await RepeatAsync("create", () => new RepeatsProxy(Link.Client).CreateAsync(new(ProtocolClient.NewOperationId(), chatId, text, days, minute)));
    }

    public async Task<ConsoleRepeatResult> CancelRepeatAsync(string id)
    {
        if (!KeepsRepeats) return ConsoleRepeatResult.Unavailable;
        return await RepeatAsync("cancel", () => new RepeatsProxy(Link.Client).CancelAsync(new(ProtocolClient.NewOperationId(), id)));
    }

    private async Task<ConsoleRepeatResult> RepeatAsync(string what, Func<Task<RepeatAnswer>> call)
    {
        try
        {
            var answer = await call();
            var done = answer.Status == RepeatStatuses.Done;
            parts.Context.Log($"mlink repeat {what} outcome={(done ? "done" : "refused")}");
            return done ? new(ConsoleActionOutcome.Done, null, answer.NextDue) : new(ConsoleActionOutcome.Refused, answer.Message);
        }
        catch (Exception failure)
        {
            var result = Outcome(failure);
            parts.Context.Log($"mlink repeat {what} outcome={result.Outcome.ToString().ToLowerInvariant()} error={failure.GetType().Name}");
            return new(result.Outcome, result.Message);
        }
    }

    // A mutation that ended without an answer: refused when the server said no, unknown when the answer was lost on the way.
    private static ConsoleActionResult Outcome(Exception failure) => failure switch
    {
        SendException { OutcomeUnknown: true } lost => new(ConsoleActionOutcome.Unknown, lost.Message),
        SendException refused => new(ConsoleActionOutcome.Refused, refused.Message),
        BackendException refused => new(ConsoleActionOutcome.Refused, refused.Message),
        NotSupportedException => new(ConsoleActionOutcome.Unavailable, "This server doesn't offer that."),
        ArgumentException refused => new(ConsoleActionOutcome.Refused, refused.Message),
        _ => new(ConsoleActionOutcome.Unknown),
    };

    // /diag's Hermes lines: each profile's state in fixed words, named by its title (the model's name), never a key.
    private void HermesChecks(List<ConsoleCheck> checks)
    {
        foreach (var agent in State.Hermes.Agents)
        {
            var name = "Hermes · " + (string.IsNullOrWhiteSpace(agent.Title) ? agent.Name : agent.Title);
            checks.Add(agent.Status switch
            {
                "connected" => new(ConsoleCheckLevel.Ok, name, "connected"),
                "unchecked" => new(ConsoleCheckLevel.Info, name, "not checked yet"),
                "key_rejected" => new(ConsoleCheckLevel.Fail, name, "API key refused; change it on the server's page"),
                "no_profile" => new(ConsoleCheckLevel.Fail, name, "no such profile on Hermes"),
                _ => new(ConsoleCheckLevel.Fail, name, "not reachable"),
            });
        }
    }
}
