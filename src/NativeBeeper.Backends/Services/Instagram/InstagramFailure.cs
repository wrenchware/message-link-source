using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Instagram;

/// <summary>
/// What an Instagram connection failure means: the code logged for it, the sentence Settings shows, and whether trying
/// again without the user could change it.
/// </summary>
/// <remarks>
/// The helper answers with fixed codes. Only those are ever written down: arbitrary exception text may carry session or
/// account detail, so anything else is logged as one word.
/// </remarks>
internal static class InstagramConnectionFailure
{
    // Every code login, session and connect can answer with. A code missing here was logged as unknown_helper_error,
    // which is how a resnapshot, a timeout and a suspended account all read the same in the log.
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "request_timeout", "connection_failed", "resnapshot_required", "login_required",
        "challenge_required", "checkpoint_required", "consent_required", "session_expired", "account_suspended",
        "invalid_session", "identity_mismatch", "invalid_cookies", "missing_cookies",
        "invalid_response", "session_failed", "internal_error",
    };

    // Answers that cannot change until the user acts — an account check, an ended or foreign session, cookies without
    // an account — so asking again only repeats the question, and on a flagged account repeats it to Instagram.
    // login_required is not one: it can mean the helper lost its client after an earlier close, which a retry mends.
    private static readonly HashSet<string> Settled = new(StringComparer.Ordinal)
    {
        "challenge_required", "checkpoint_required", "consent_required", "session_expired", "account_suspended",
        "invalid_session", "identity_mismatch", "invalid_cookies", "missing_cookies",
    };

    /// <summary>The fixed code for a failure, or one word naming its kind where it carried none.</summary>
    public static string Code(Exception failure) => failure is InvalidOperationException
        ? Codes.Contains(failure.Message) ? failure.Message : "unknown_helper_error"
        : failure.GetType().Name;

    /// <summary>
    /// Whether retrying without the user cannot help: a settled refusal, a helper that is missing or a different
    /// version, or a close that was asked for.
    /// </summary>
    public static bool Definitive(Exception failure) => failure switch
    {
        OperationCanceledException => true,
        BackendException { Failure: BackendFailure.Unsupported or BackendFailure.Unavailable } => true,
        InvalidOperationException => Settled.Contains(failure.Message),
        _ => false,
    };

    /// <summary>Whether an answer means the account needs the user, so nothing reconnects it behind their back.</summary>
    public static bool NeedsUser(string? code) => code is not null && Settled.Contains(code);

    /// <summary>
    /// The realtime state word from a helper frame as it may be logged: one the helper is known to set, or "other".
    /// </summary>
    public static string RealtimeState(string? state) =>
        state is { } known && (known is "authenticating" or "session_loaded" or "authenticated" or "connected" or "disconnected"
            or "request_cancelled" or "typing_unstable" || Codes.Contains(known)) ? known : "other";

    /// <summary>The sentence Settings shows for a code.</summary>
    /// <param name="firstSignIn">
    /// True when the cookies came from the sign-in window just now. Nothing was saved before then, so an answer about the
    /// cookies means that sign-in did not finish, not that a saved one expired. A saved one is resumed by Connect again,
    /// which would only be refused again: the way back is Sign out, then Connect.
    /// </param>
    public static string Sentence(string code, bool firstSignIn = false) => code switch
    {
        "request_timeout" => "Instagram took too long to connect. Try Reconnect again later.",
        "challenge_required" or "checkpoint_required" or "consent_required" =>
            "Instagram requires an account check. Open Instagram to complete it, then choose Sign out and Connect here to sign in again.",
        "session_expired" or "invalid_session" or "identity_mismatch" or "invalid_cookies" or "missing_cookies" when firstSignIn =>
            "The Instagram sign-in didn't finish. Choose Connect to try again.",
        "session_expired" or "invalid_session" or "identity_mismatch" or "invalid_cookies" or "missing_cookies" =>
            "Instagram has signed Messages out. Choose Sign out, then Connect to sign in again.",
        "account_suspended" => "Instagram reports that this account is suspended.",
        // Messages already reloads once from the saved sign-in when Instagram asks for this, so reaching here means
        // that reload did not finish either; another launch gets its own.
        "resnapshot_required" => "Instagram asked to load this account again from the start, and that did not finish. Restart Messages to try again.",
        _ => "Instagram couldn't connect. Try Reconnect again later.",
    };

    /// <summary>
    /// The sentence for a failure. A backend failure's message is the app's own sentence (a helper missing, blocked or from
    /// another version), never account detail; anything else is said by its fixed code.
    /// </summary>
    public static string SentenceFor(Exception failure, bool firstSignIn = false) =>
        failure is BackendException backend ? backend.Message : Sentence(Code(failure), firstSignIn);
}

/// <summary>
/// What a refused Instagram action tells the person, and whether it may already have happened.
/// </summary>
/// <remarks>
/// A code the helper answers before it contacts Instagram is definite: nothing was sent, so the action can simply be
/// tried again. Everything else — including every network code, since the same code can come from before or after the
/// send — stays outcome-unknown, because an unknown outcome is never retried and a definite one is never feared.
/// </remarks>
internal static class InstagramRefusal
{
    internal enum MessageChange { Reaction, Edit, Unsend }

    private static string Noun(MessageChange kind) => kind switch
    {
        MessageChange.Reaction => "reaction",
        MessageChange.Edit => "edit",
        _ => "removal",
    };

    // Answered by the helper before any mutation reaches Instagram (send.go writeClientFor and writable).
    private static readonly HashSet<string> BeforeSending = new(StringComparer.Ordinal)
    {
        "invalid_params", "chat_unknown", "chat_not_writable", "read_only", "login_required", "unsupported", "unknown_method",
    };

    // A file adds the staging and the upload, neither of which sends a message (sendmedia.go).
    private static readonly HashSet<string> BeforeSendingFile = new(BeforeSending, StringComparer.Ordinal)
    {
        "no_media_staged", "upload_failed",
    };

    // A first message resolves the conversation before it sends: its invalid_response comes only from that lookup,
    // since a failed send is mapped by failureCode, which never answers it (startchat.go).
    private static readonly HashSet<string> BeforeFirstMessage = new(BeforeSending, StringComparer.Ordinal)
    {
        "chat_is_self", "invalid_response",
    };

    // A group's members are checked before it is created (startchat.go uiCreateGroup).
    private static readonly HashSet<string> BeforeCreating = new(StringComparer.Ordinal)
    {
        "invalid_params", "chat_is_self", "read_only", "login_required", "unsupported", "unknown_method",
    };

    private static string? CodeOf(Exception failure) => failure is InvalidOperationException ? failure.Message : null;

    // A failure as a word fit for the log: the helper's own code when it is one, else the failure's type.
    public static string LogCode(Exception failure) => CodeOf(failure) is { Length: > 0 and <= 40 } code && code.All(c => char.IsAsciiLetterLower(c) || c == '_')
        ? code : failure.GetType().Name;

    public static SendException Send(Exception failure) =>
        new(!BeforeSending.Contains(CodeOf(failure) ?? ""), SendSentence(failure));

    /// <summary>
    /// A reaction, an edit or taking a message back. Only a code the helper answers before it contacts Instagram is a
    /// plain refusal; everything else, a dropped pipe or a timeout included, may have reached the conversation and is
    /// outcome-unknown, so it is reported and never offered again.
    /// </summary>
    public static SendException Change(Exception failure, MessageChange kind) =>
        new(!BeforeSending.Contains(CodeOf(failure) ?? ""), ChangeSentence(failure, kind));

    public static SendException File(Exception failure) =>
        new(!BeforeSendingFile.Contains(CodeOf(failure) ?? ""), FileSentence(failure));

    public static SendException FirstMessage(Exception failure) =>
        new(!BeforeFirstMessage.Contains(CodeOf(failure) ?? ""), FirstMessageSentence(failure));

    public static SendException Group(Exception failure) =>
        new(!BeforeCreating.Contains(CodeOf(failure) ?? ""), GroupSentence(failure));

    /// <summary>
    /// Opening a conversation is a lookup and never sends, so its refusals are all definite. One of them is not a
    /// refusal at all: no conversation yet means New chat asks for the first message that makes one.
    /// </summary>
    public static Exception Start(Exception failure, string name) => CodeOf(failure) == "chat_not_started"
        ? new FirstMessageRequiredException("You and " + name + " have no Instagram conversation yet. Instagram creates one when the first message is sent — write it here and it will open.")
        : new SendException(false, StartSentence(failure));

    // Only the fixed codes the helper produces reach the interface; nothing upstream describes the account.
    public static string SendSentence(Exception failure) => CodeOf(failure) switch
    {
        "chat_not_writable" => "Instagram does not allow replies in this conversation.",
        "invalid_params" => "Instagram would not accept that.",
        "chat_unknown" => "That Instagram conversation is not loaded. Open it again and retry.",
        "read_only" => "Instagram can't send right now.",
        "login_required" or "unsupported" or "unknown_method" => "Instagram is not ready to send yet. Nothing was sent; try again in a moment.",
        "send_uncertain" => "Instagram did not confirm the message. Check the conversation before sending it again.",
        _ => "The message couldn't be sent to Instagram. Check the conversation before sending it again.",
    };

    private static string ChangeSentence(Exception failure, MessageChange kind) => CodeOf(failure) switch
    {
        "chat_not_writable" => kind switch
        {
            MessageChange.Reaction => "Instagram does not allow reactions in this conversation.",
            MessageChange.Edit => "Instagram does not allow editing messages in this conversation.",
            _ => "Instagram does not allow removing messages in this conversation.",
        },
        "chat_unknown" => "That Instagram conversation is not loaded. Open it again and retry.",
        "invalid_params" => "Instagram would not accept that " + Noun(kind) + ".",
        "read_only" => "This Instagram connection cannot change messages.",
        "login_required" or "unsupported" or "unknown_method" => "Instagram is not ready yet. Nothing was changed; try again in a moment.",
        _ => "Instagram did not confirm the " + Noun(kind) + ". Check the message before trying again.",
    };

    private static string FileSentence(Exception failure) => CodeOf(failure) switch
    {
        "no_media_staged" => "The file did not reach Instagram. Try again.",
        "upload_failed" => "Instagram would not accept that file. A video from some accounts cannot be sent this way.",
        "chat_not_writable" => "Instagram does not allow replies in this conversation.",
        "chat_unknown" => "That Instagram conversation is not loaded. Open it again and retry.",
        "invalid_params" or "read_only" or "login_required" or "unsupported" or "unknown_method" => "The file was not sent to Instagram. Try again in a moment.",
        "send_uncertain" => "Instagram did not confirm the file. Check the conversation before sending it again.",
        _ => "The file couldn't be sent to Instagram. Check the conversation before sending it again.",
    };

    private static string FirstMessageSentence(Exception failure) => CodeOf(failure) switch
    {
        "chat_not_writable" => "That Instagram conversation is a message request or filed as spam, so a message cannot be sent into it from here.",
        "chat_is_self" => "That is this Instagram account.",
        "chat_unknown" or "invalid_response" => "Instagram couldn't say which conversation this is. Nothing was sent.",
        "send_uncertain" => "Instagram did not say whether the message was sent. Check Instagram before sending it again.",
        "read_only" => "This Instagram connection cannot send messages.",
        "login_required" or "session_expired" => "Instagram needs you to sign in again. Reconnect Instagram in Settings.",
        "checkpoint_required" or "challenge_required" or "consent_required" => "Instagram wants something confirmed in its own app before this account can be used.",
        "account_suspended" => "Instagram has suspended this account.",
        "invalid_params" => "Instagram would not accept that message.",
        "unsupported" or "unknown_method" => "This Instagram connection cannot start conversations. Nothing was sent.",
        _ => "The result is unknown. Check Instagram before sending it again.",
    };

    private static string StartSentence(Exception failure) => CodeOf(failure) switch
    {
        "chat_is_self" => "That is this Instagram account.",
        "invalid_response" => "Instagram answered with a different conversation than the one asked for. Nothing was opened.",
        "invalid_params" => "Choose an Instagram account to write to.",
        "read_only" or "unsupported" or "unknown_method" => "This Instagram connection cannot start conversations.",
        "login_required" or "session_expired" => "Instagram needs you to sign in again. Reconnect Instagram in Settings.",
        "checkpoint_required" or "challenge_required" or "consent_required" => "Instagram wants something confirmed in its own app before this account can be used.",
        "account_suspended" => "Instagram has suspended this account.",
        "request_timeout" => "Instagram did not answer in time. Try again.",
        _ => "That Instagram conversation couldn't be opened. Try again.",
    };

    private static string GroupSentence(Exception failure) => CodeOf(failure) switch
    {
        "group_uncertain" => "Instagram did not say whether the group was created. Check Instagram before creating it again.",
        "chat_is_self" => "That list includes this Instagram account, which is already in every group it creates.",
        "invalid_params" => "Instagram would not accept those members.",
        "read_only" or "unsupported" or "unknown_method" => "This Instagram connection cannot create groups.",
        "login_required" or "session_expired" => "Instagram needs you to sign in again. Reconnect Instagram in Settings.",
        "checkpoint_required" or "challenge_required" or "consent_required" => "Instagram wants something confirmed in its own app before this account can be used.",
        "account_suspended" => "Instagram has suspended this account.",
        _ => "The result is unknown. Check Instagram before creating the group again.",
    };
}

/// <summary>
/// When a helper that stopped by itself is started again from the saved session, without the user.
/// </summary>
/// <remarks>
/// Only a helper that had connected. One that dies while starting belongs to whoever was starting it — the launch
/// restore has its own three attempts, and recovering behind it as well multiplied sign-ins against a real account.
/// Never while the app is closing, and never once Instagram has refused the session: signing in again is the user's.
/// Three attempts, and the budget comes back only after a helper stays up two minutes, so a helper that dies soon after
/// every connection is a loop that ends rather than one that signs in forever. Not thread-safe; the backend's own
/// helper stop is its only caller.
/// </remarks>
internal sealed class InstagramRecovery
{
    public const int Attempts = 3;
    private static readonly TimeSpan Settles = TimeSpan.FromMinutes(2);
    private int used;

    public int Used => used;

    /// <summary>Why a stopped helper is not recovered — a fixed word — or null when it is.</summary>
    public string? Refuse(bool reachedReady, TimeSpan uptime, bool closing, string? refusedWith)
    {
        if (closing) return "closing";
        if (!reachedReady) return "never_ready";
        if (refusedWith is not null) return "refused";
        if (uptime > Settles) used = 0;
        return used >= Attempts ? "budget_spent" : null;
    }

    /// <summary>Takes one attempt from the budget, or answers zero when none is left.</summary>
    public int Take() => used >= Attempts ? 0 : ++used;

    /// <summary>
    /// Why a restart somebody asked for in Connection health may not run — a fixed word — or null once it has taken one
    /// attempt from this same budget. It is a sign-in like any recovery, so it gets no allowance of its own: a helper
    /// that has stayed connected two minutes gives the budget back first, exactly as its stop would, and a session
    /// Instagram refused is never tried again, least of all after an account check.
    /// </summary>
    public string? TakeForUser(bool reachedReady, TimeSpan uptime, string? refusedWith) => Decide(reachedReady, uptime, refusedWith, take: true);

    /// <summary>The same answer, taking nothing: what Connection health checks before it waits for any send.</summary>
    public string? RefuseUser(bool reachedReady, TimeSpan uptime, string? refusedWith) => Decide(reachedReady, uptime, refusedWith, take: false);

    /// <summary>A restart the user asked for that stopped nothing (a send began first) hands its attempt back.</summary>
    public void Refund() { if (used > 0) used--; }

    private string? Decide(bool reachedReady, TimeSpan uptime, string? refusedWith, bool take)
    {
        if (refusedWith is not null)
            return refusedWith is "challenge_required" or "checkpoint_required" or "consent_required" ? "account_check" : "needs_sign_in";
        var spent = reachedReady && uptime > Settles ? 0 : used;
        if (spent >= Attempts) return "budget_spent";
        if (take) used = spent + 1;
        return null;
    }

    public static TimeSpan Delay(int attempt) => TimeSpan.FromSeconds(3 * attempt);
}

/// <summary>
/// How a helper process ended, in fixed words. Its standard error is Go runtime output, and a panic's value can be
/// anything an upstream goroutine held, so none of its text is written down: only which kind of ending it was.
/// </summary>
internal static class InstagramHelperExit
{
    public static string Describe(string? lastError)
    {
        var text = lastError ?? "";
        var how = text.StartsWith("the helper closed its output", StringComparison.Ordinal) ? "closed_output"
            : text.StartsWith("the connection to the helper failed", StringComparison.Ordinal) ? "pipe_failed"
            : text.StartsWith("exit ", StringComparison.Ordinal) ? "exited"
            : text.Length == 0 ? "unknown" : "other";
        var said = text.Contains("panic:", StringComparison.Ordinal) ? "panic"
            : text.Contains("fatal error:", StringComparison.Ordinal) ? "fatal"
            : text.Contains(": ", StringComparison.Ordinal) ? "other" : "none";
        return $"how={how} said={said}";
    }
}
