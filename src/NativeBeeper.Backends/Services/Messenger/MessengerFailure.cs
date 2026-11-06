namespace NativeBeeper.App.Services.Messenger;

internal static class MessengerFailure
{
    public static bool IsUnknown(string code) => code is not ("send_rejected" or "upload_failed" or "react_rejected" or "not_connected" or "login_required"
        or "invalid_input" or "reply_unavailable" or "reaction_unavailable" or "e2ee_off" or "e2ee_offline");

    public static string Explain(string code) => code switch
    {
        "send_rejected" => "Facebook refused the message.",
        "upload_failed" => "Messenger could not upload that attachment, so it was not sent.",
        "not_connected" or "login_required" => "Messenger is not connected. Reconnect it in Settings.",
        "invalid_input" => "That message cannot be sent to Messenger.",
        "reply_unavailable" => "That message is no longer loaded, so it cannot be quoted.",
        "reaction_unavailable" => "That message is no longer available for a reaction.",
        "e2ee_off" => "This conversation is end-to-end encrypted. Turn on encrypted conversations in Settings to reply to it here.",
        "e2ee_offline" => "The encrypted connection is not up, so this message was not sent. It will work once Messenger reconnects.",
        _ => "Messenger did not confirm the message.",
    };

    // Deleting a conversation. delete_failed is the request itself erroring, so whether Facebook deleted it is not
    // known; the refusals, a conversation that is not loaded and a helper not connected are all definite. Anything
    // else, sanitized text included, is treated as unknown so nothing ever repeats a deletion that may have happened.
    public static bool IsDeleteUnknown(string code) => code is not ("delete_rejected" or "chat_unavailable" or "not_connected"
        or "login_required" or "invalid_input");

    public static string ExplainDelete(string code) => code switch
    {
        "delete_rejected" => "Facebook refused to delete this conversation.",
        "delete_unconfirmed" => "Messenger did not confirm deletion, so the conversation was kept. Check Messenger before trying again.",
        "chat_unavailable" => "That conversation is no longer loaded.",
        "not_connected" or "login_required" => "Messenger is not connected. Reconnect it in Settings.",
        _ => "The delete outcome is unknown. Check Messenger before trying again.",
    };

    // Archiving or bringing back a conversation. The helper moves its copy only when Messenger states the new folder, so
    // update_unconfirmed means Messenger took the request without saying it moved anything: like an errored request, it
    // may yet have happened, and is never repeated by itself. A refusal, an unloaded conversation and a helper not
    // connected are definite; nothing was moved.
    public static bool IsUpdateUnknown(string code) => code is not ("update_rejected" or "chat_unavailable" or "not_connected"
        or "login_required" or "invalid_input");

    public static string ExplainUpdate(string code) => code switch
    {
        "update_rejected" or "chat_unavailable" => "Messenger would not change that conversation.",
        "update_unconfirmed" => "Messenger didn't confirm the move, so the conversation stays where it was here. Check Messenger before trying again.",
        "not_connected" or "login_required" => "Messenger is not connected. Reconnect it in Settings.",
        "invalid_input" => "That change can't be made to this conversation.",
        _ => "The archive result is unknown. Refresh chats before trying again.",
    };

    // Facebook's own answer to a saved session, which signing in again with it cannot change: only the person can,
    // at facebook.com or by connecting afresh. login_failed is deliberately not here. It is also how a network that is
    // not up yet at boot or wake reads, and that is exactly the case retrying exists for.
    public static bool IsLoginRefusal(string code) => code is "checkpoint_required" or "challenge_required"
        or "consent_required" or "session_expired" or "missing_cookies";

    // A connect attempt that a second one moments later cannot fix: a login refusal, a helper missing from the install or
    // from another version, or one Windows refused to start (HelperStartFailure).
    public static bool IsDefiniteConnectFailure(Exception failure) => failure is InvalidOperationException { Message: var code } &&
        (IsLoginRefusal(code) || code is "unsupported_protocol" || NativeBeeper.App.Services.HelperStartFailure.Sentence(code) is not null);

    // What Settings says when connecting fails, by the fixed code. saved: whether a saved sign-in was used, which is what
    // decides how to sign in afresh: with one saved, Connect only uses it again, so the way back is Sign out and Connect.
    // login_failed is also what no network looks like (IsLoginRefusal), so it must not say Facebook refused anything.
    public static string ExplainConnect(string code, bool saved) => code switch
    {
        "checkpoint_required" => "Facebook wants to confirm it's you. Open facebook.com in your browser, follow its steps, then connect again.",
        "challenge_required" => "Facebook asked for an extra security check. Complete it at facebook.com, then connect again.",
        "consent_required" => "Facebook is waiting for you to accept something. Accept it at facebook.com, then connect again.",
        "session_expired" or "missing_cookies" when saved => "Facebook has signed Messages out. Choose Sign out, then Connect to sign in again.",
        "session_expired" => "Facebook didn't accept this sign-in. Choose Connect to try again.",
        "missing_cookies" => "The Facebook sign-in didn't finish. Choose Connect to try again.",
        "login_failed" => "Couldn't sign in to Facebook. Check your internet connection and try again.",
        "connect_failed" => "Couldn't connect to Messenger. Check your internet connection and try again.",
        var other when NativeBeeper.App.Services.HelperStartFailure.Sentence(other) is { } start => start,
        _ => "Couldn't connect to Messenger. Try again.",
    };

    // How a helper that stopped by itself is written down: how long it ran and, in the fixed words Instagram's stops use,
    // what kind of ending it was. Its standard error is Go runtime output, and a panic's value can be anything an upstream
    // goroutine held, so none of its text is kept (HelperConnection.LastError is never logged). It was, verbatim.
    public static string StoppedLine(TimeSpan uptime, string? lastError) =>
        $"helper stopped after {(int)uptime.TotalSeconds}s {NativeBeeper.App.Services.Instagram.InstagramHelperExit.Describe(lastError)}";

    // What a failed connect may put in a log: a helper's fixed code, or only the exception's type. An
    // InvalidOperationException is not always one of ours (a malformed reply raises one with prose), so only a
    // snake_case token is taken for a code.
    public static string Code(Exception failure) => failure is InvalidOperationException { Message: { Length: > 0 and <= 48 } code }
        && code.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') ? code : failure.GetType().Name;
}
