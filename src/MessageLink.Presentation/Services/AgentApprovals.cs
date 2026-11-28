using System.Globalization;
using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

/// <summary>
/// The words for a command an agent holds for approval (Hermes: a message of type <c>approval</c> carrying <see cref="ChatMessage.Approval"/>), shared by the
/// window's card, mlink and the phone's wording. Approve runs it once; Deny refuses it. Nothing broader is ever offered (owner, 2026-11-16).
/// </summary>
public static class AgentApprovals
{
    public const string ApproveLabel = "Approve";
    public const string DenyLabel = "Deny";

    public static bool IsRequest(ChatMessage message) => message.Type == "approval" && message.Approval is not null;

    /// <summary>"Hermes wants to run a command", named after the thread's own sender.</summary>
    public static string Title(ChatMessage message) => (message.SenderName is { Length: > 0 } name ? name : "Hermes") + " wants to run a command";

    /// <summary>Why the agent held it, as a label line; null when it gave no reason.</summary>
    public static string? Reason(AgentApproval approval) => approval.Description is { Length: > 0 } why ? "Reason: " + why : null;

    public const string AdvisedText = "Hermes's safety check advised against running it.";
    /// <summary>On a command too long to keep whole, or with characters that don't show (AgentApproval.Cut): what is shown isn't all of it, so it can only be denied here.</summary>
    public const string CutText = "This command can't be shown in full here, so it can only be denied. To run it, approve it in Hermes.";

    /// <summary>Whether Approve is offered: answerable, and the whole command is shown.</summary>
    public static bool Approvable(AgentApproval approval, DateTimeOffset now) => Answerable(approval, now) && !approval.Cut;

    /// <summary>How the request stands, in one sentence. <paramref name="now"/> and <paramref name="zone"/> place a pending request's deadline.</summary>
    public static string Status(AgentApproval approval, DateTimeOffset now, TimeZoneInfo zone) => approval.State switch
    {
        AgentApprovalStates.Pending when approval.Expires is { } until && until > now =>
            "Hermes waits until " + TimeZoneInfo.ConvertTime(until, zone).ToString("t", CultureInfo.CurrentCulture) + ", then denies it.",
        AgentApprovalStates.Pending => "Hermes is waiting for your answer.",
        AgentApprovalStates.Unknown => "Your answer may not have reached Hermes. Answer again while it waits.",
        AgentApprovalStates.Approved => "Approved to run once.",
        AgentApprovalStates.Denied => "Denied. Hermes didn't run it.",
        AgentApprovalStates.Expired => "No answer in time. Hermes didn't run it.",
        _ => "Hermes is no longer waiting for an answer.",
    };

    /// <summary>Whether Approve and Deny are offered: the request still waits, and its deadline hasn't passed.</summary>
    public static bool Answerable(AgentApproval approval, DateTimeOffset now) => approval.Pending && (approval.Expires is not { } until || until > now);

    /// <summary>What an answer came to, said after it: the state the server reported.</summary>
    public static string Outcome(string state) => state switch
    {
        AgentApprovalStates.Approved => "Approved. Hermes runs it once.",
        AgentApprovalStates.Denied => "Denied. Hermes won't run it.",
        AgentApprovalStates.Expired => "Too late: Hermes had stopped waiting and didn't run it.",
        AgentApprovalStates.Unknown => "Your answer may not have reached Hermes. Answer again while it waits.",
        _ => "Hermes was no longer waiting for an answer.",
    };
}
