namespace MessageLink.GoogleMessages.Spike;

// Fixed cross-boundary vocabulary: known provider refusals must not become generic, uncertain transport failures.
// Every entry is a string literal in one of this app's own helpers, never upstream text. A code missing here reaches
// the app as "Helper request failed.", so any branch that matches it is dead: that is how Instagram's first-message
// prompt (chat_not_started) and Google's lost-pairing handling (pairing_lost) went unreachable until 0.1.301.
internal static class HelperErrorCodes
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "access_denied", "media_unavailable", "pairing_required", "missing_cookies", "invalid_session",
        "pair_start_failed", "pair_finish_failed", "connect_failed", "config_failed", "history_failed",
        "unsupported_method", "authentication_required", "rate_limited", "remote_unavailable", "phone_unavailable",
        "connection_closed", "network_unavailable", "unexpected_response", "wrong_account",
        "login_required", "login_failed", "challenge_required", "checkpoint_required", "consent_required",
        "session_expired", "session_already_started", "invalid_input", "not_connected", "send_failed", "send_rejected",
        "upload_failed", "unknown_chat", "unknown_message", "message_unavailable", "reply_unavailable",
        "reaction_unavailable", "react_failed", "react_rejected", "edit_failed", "edit_rejected", "delete_failed",
        "delete_rejected", "delete_unconfirmed", "delete_me_only_unsupported", "chat_unavailable", "mark_read_failed",
        "search_failed", "start_failed", "start_rejected", "start_unavailable", "update_rejected", "e2ee_failed",
        "e2ee_off", "e2ee_offline", "e2ee_register_failed", "e2ee_store_failed", "existing_device_required",
        "no_storage", "unknown_account", "not_yours", "observation_only",
        "auth_required", "voice_unavailable", "unknown_method", "request_cancelled", "update_failed",
        "backup_unavailable", "backup_expired", "backup_other_account", "backup_retired", "backup_unsupported", "backup_failed",
        "invalid_request",
        // Google Messages (connection_state.go, directory.go, actions.go, typing.go, deletemessage.go).
        "pairing_lost", "contacts_failed", "read_failed", "typing_failed", "delete_everyone_unsupported",
        // Messenger (directory.go, updatechat.go, main.go's ui.chat).
        "start_uncertain", "update_unconfirmed", "chat_deleted",
        // Instagram (readonly.go, chats.go, send.go, sendmedia.go, startchat.go, main.go).
        "chat_not_started", "chat_not_writable", "chat_unknown", "chat_is_self", "send_uncertain", "group_uncertain",
        "invalid_params", "invalid_response", "no_media_staged", "read_only", "connection_failed", "request_timeout",
        "account_suspended", "identity_mismatch", "internal_error", "session_failed", "state_expired", "invalid_cookies",
        "resnapshot_required", "already_logged_in", "already_connecting", "unsupported",
        "offline_only",
    };

    // Google's pairing refusals carry Google's own small numbers (pair_errors.go: pair_refused_3_32,
    // pair_start_google_5), so they are admitted by shape rather than listed. The same shape as
    // GooglePairingFailure.IsCode; a contract check keeps the two in agreement.
    private static readonly System.Text.RegularExpressions.Regex PairingShape = new("^pair_[a-z_]+?(_[0-9]{1,4}){0,2}$");

    public static bool IsKnown(string? code) =>
        code is not null && (Known.Contains(code) || code.Length <= 40 && PairingShape.IsMatch(code));

    public static string Sanitize(string? code) => IsKnown(code) ? code! : "Helper request failed.";
}
