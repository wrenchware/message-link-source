using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

// What the encryption answer rests on: the network saying so (Messenger, or a Google conversation that sends by text),
// a switch line in loaded history, a switch line in saved history, nothing, or (appended) a loaded message the network
// marked as sent end-to-end encrypted.
public enum EncryptionEvidence { None, Network, SwitchLine, SavedHistory, Message }
public readonly record struct EncryptionDecision(bool? Encrypted, EncryptionEvidence Evidence);

// Whether a conversation is end-to-end encrypted, known positively or not at all. A lock shown on a conversation that
// is not encrypted is a false security claim; a lock missing from one that is, is only a missed nicety. So unknown
// never becomes a lock, and never becomes "not encrypted" either.
public static class ConversationEncryption
{
    public static bool IsKnownEncrypted(ChatSummary chat, IEnumerable<ChatMessage> messages) => Decide(chat, messages).Encrypted == true;

    // Messages is every loaded message of this one conversation.
    public static EncryptionDecision Decide(ChatSummary chat, IEnumerable<ChatMessage> messages)
    {
        if (chat.IsEncrypted is { } said) return new(said, EncryptionEvidence.Network);
        // Google Messages says it only in system lines when a conversation switches. The newest one known decides.
        (DateTimeOffset At, bool Switch)? line = null;
        DateTimeOffset? oldest = null, marked = null;
        foreach (var message in messages)
        {
            var at = message.Timestamp;
            if (oldest is null || at < oldest) oldest = at;
            if (message.EncryptionSwitch is { } switched && (line is null || at > line.Value.At)) line = (at, switched);
            if (message.IsEncrypted == true && (marked is null || at > marked)) marked = at;
        }
        // Google marks each message that went encrypted. The newest such message says the conversation is, unless a
        // switch line after it, loaded or saved, says it stopped.
        if (marked is { } sealedAt && (line is null || sealedAt > line.Value.At) && (chat.SavedSwitch is null || sealedAt > chat.SavedSwitch.At))
            return new(true, EncryptionEvidence.Message);
        if (chat.SavedSwitch is { } saved && (line is null || saved.At > line.Value.At))
        {
            // A saved switch off is safe to believe: at worst it hides a lock. A saved switch on counts only when nothing
            // after it can be missing — the saved history runs on without a gap until loaded history takes over — since
            // a later switch off in that gap would make the lock a false claim.
            if (!saved.Encrypted) return new(false, EncryptionEvidence.SavedHistory);
            if (oldest is { } first && first <= (saved.SavedThrough ?? saved.At)) return new(true, EncryptionEvidence.SavedHistory);
        }
        return line is { } newest ? new(newest.Switch, EncryptionEvidence.SwitchLine) : new(null, EncryptionEvidence.None);
    }

    // The lock goes under a message only when the message itself went the encrypted way. Google marks each message that
    // did, as Messages for Web shows beside each one: an RCS message from before a group switched to encryption is not
    // marked, whatever the conversation is now, and one still sending is not marked yet. Elsewhere the conversation's
    // own answer stands, except for a message sent by SMS or MMS.
    public static bool LockAllowed(ChatMessage message, ChatSummary chat)
    {
        if (IsGoogle(chat)) return message.IsEncrypted == true && !PendingRows.IsLocal(message);
        return chat.IsEncrypted == true && message.Transport is not ("SMS" or "MMS" or "SMS/MMS");
    }

    private static bool IsGoogle(ChatSummary chat) => chat.Id.StartsWith("google-direct:", StringComparison.Ordinal);

    // Google's status names for those system lines. Anything naming a switch away from E2EE, or to plain RCS or SMS,
    // ends encryption; anything else naming E2EE or an encrypted conversation starts or confirms it. The MLS line that
    // came with encryption between iPhone and Android says neither: what it means has not been observed, so it stays
    // unknown and never makes a lock.
    public static bool? FromGoogleStatus(string status)
    {
        if (status.Contains("E2EE_TO_", StringComparison.Ordinal) || status.EndsWith("PROTOCOL_SWITCH_TO_RCS", StringComparison.Ordinal) ||
            status.EndsWith("PROTOCOL_SWITCH_TO_TEXT", StringComparison.Ordinal)) return false;
        if (!status.Contains("TOMBSTONE", StringComparison.Ordinal)) return null;
        return status.Contains("E2EE", StringComparison.Ordinal) || status.Contains("ENCRYPTED", StringComparison.Ordinal) ? true : null;
    }
}
