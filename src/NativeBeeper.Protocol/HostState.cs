using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

/// <summary>How a scope's conversations answer "can this history be read around a moment here" (<c>IHistoryWindowReader.CanReadWindow</c>).</summary>
public enum WindowReads
{
    /// <summary>Never: the service's history is only what a session loaded, so paging back reaches it.</summary>
    Never,
    /// <summary>Always, whether or not the service is connected (Messenger's helper keeps its own store).</summary>
    Always,
    /// <summary>Per conversation (Google's archive has crawled some conversations and not others); the snapshot cannot say which.</summary>
    PerChat,
}

/// <summary>
/// What a client needs to know about one connection scope (<c>google-direct:</c>, <c>messenger:</c>, <c>instagram:</c>,
/// <c>gvoice:&lt;key&gt;:</c>) without a round trip. Flags only: no conversation, message or person is named here.
/// </summary>
/// <param name="Scope">The conversation-id prefix the connection owns.</param>
/// <param name="Connected">The connection is up and signed in.</param>
/// <param name="Configured">A session is saved for it, whether or not it has reconnected.</param>
/// <param name="NeedsSignInOnly">The service refused only its sign-in and keeps its pairing, so renewing the sign-in brings it back (Google Messages).</param>
/// <param name="HoldsPartialCopies">Some of its messages are kept only in part (Instagram's list words for a photo).</param>
/// <param name="CanSave">Attachments of this scope can be saved now.</param>
/// <param name="DrawsPhotosAtOnce">Several still photos can all be drawn the moment Send is pressed (Google Messages).</param>
public sealed record ScopeState(string Scope, bool Connected, bool Configured, bool NeedsSignInOnly = false, bool HoldsPartialCopies = false,
    WindowReads WindowReads = WindowReads.Never, bool CanSave = false, bool DrawsPhotosAtOnce = false);

/// <summary>The host's fixed limits and capabilities that do not depend on a conversation. Zero and false are the conservative answers.</summary>
/// <param name="GoogleAbsent">This host does not run Google Messages at all (a Linux server), so a client says so instead of offering a
/// sign-in the host would refuse. False means "not known to be absent"; the host refuses whatever it does not run either way.</param>
/// <param name="VoiceAbsent">The same for Google Voice.</param>
/// <param name="InstagramAbsent">The same for Instagram.</param>
/// <param name="UnarchivesChats">This host returns archived one-to-one conversations outside every merge to the inbox itself (stage 6 slice 5): it runs the
/// incoming unarchive and can read the merges from its shared state. A client then leaves them to it; otherwise (false, which is also what a host from before
/// the flag says) the client returns them itself. Stated rather than inferred from the protocol version, since a host of any version may lack the shared
/// state or the identity the merges are read by.</param>
/// <param name="FiresScheduled">This host keeps and fires its clients' scheduled sends itself (stage 6 slice 11, protocol 9): it serves <c>scheduled.*</c> and writes the <c>scheduled:</c> rows. False
/// (an in-process host, the loopback, a host from before the flag) means the client keeps and fires its own. A client of a host of 9 or later never fires one itself
/// whatever this says; the flag only tells it whether the host can take a new one.</param>
/// <param name="ChromeOutOfDate">The Chrome this host signs Google Messages in through (protocol 14) is older than the server's image was meant to keep it (21 days since the image was built), so the
/// client says in Settings that the server should be reinstalled. False (a host from before the flag, or one with no such Chrome) says nothing.</param>
public sealed record HostLimits(bool HasLocalActionOptions = false, int MaxFirstMessageLength = 0, int GroupNameMaximum = 0, int AddMaximum = 0, int BatchItemLimit = 0,
    bool GoogleAbsent = false, bool VoiceAbsent = false, bool InstagramAbsent = false, bool UnarchivesChats = false, bool FiresScheduled = false,
    bool ChromeOutOfDate = false);

/// <summary>Typing, in aggregate: the flags <c>ITypingService.CanReceive</c> and <c>CanSend</c> answer, which <see cref="TypingProxy"/> reads.</summary>
public sealed record TypingState(bool CanReceive = false, bool CanSend = false);

/// <summary>
/// How many times each account's <c>Changed</c> was raised on the host in this run. Part of the state so that a change the snapshot
/// itself does not show (Messenger's or Instagram's conversation list changed) still reaches a client as a state change.
/// </summary>
public sealed record AccountRevisions(int Messenger = 0, int Instagram = 0, int Google = 0, int Voice = 0);

/// <summary>
/// Everything a client's synchronous members answer from, pushed by the host as a <c>state</c> event (SWITCHBOARD.md 2.6b) whenever
/// it changes, and once after every resync. No credential, cookie or session is in it, and no conversation, message or chat id. The
/// account records are the Core snapshots as they are, so an account id, a Voice number or an Instagram name travel to the client
/// by design (a client of the owner's own server); they must never be logged.
/// </summary>
/// <param name="Identity">
/// Whether Google is paired, the key local data is filed under and each account's saved-message scope (SWITCHBOARD.md 3.0b), all one-way
/// derived values. Absent from a host that has none and from a state written before it existed; a client then keeps what it last knew.
/// </param>
public sealed record HostState(IReadOnlyList<ScopeState> Scopes, MessengerAccountState Messenger, InstagramAccountState Instagram,
    GoogleMessagesAccountState Google, IReadOnlyList<GoogleVoiceAccountState> Voice, IReadOnlyList<string> VoiceKnownKeys, HostLimits Limits,
    TypingState Typing, AccountRevisions Revisions, HostIdentity? Identity = null);

/// <summary>
/// Which connection a conversation id or an attachment belongs to, read from the id alone: the one prefix convention the routers
/// share (<c>google-direct:</c>, <c>messenger:</c>, <c>instagram:</c>, <c>gvoice:&lt;key&gt;:</c>; media have <c>-media:</c> forms). A
/// client uses it to find the <see cref="ScopeState"/> that answers for an id without asking the host. The harness checks it against
/// each provider's own ownership rules.
/// </summary>
public static class ProviderScopes
{
    public const string Google = "google-direct:";
    public const string Messenger = "messenger:";
    public const string Instagram = "instagram:";
    public const string VoicePrefix = "gvoice:";

    private const string GoogleMedia = "google-media:", MessengerMedia = "messenger-media:", InstagramMedia = "instagram-media:", VoiceMedia = "gvoice-media:";
    private const int VoiceKeyLength = 16;

    public static string Voice(string key) => VoicePrefix + key + ":";

    /// <summary>
    /// The scope that owns a conversation id, as the routers decide it (Instagram, then Voice, then Messenger, and everything else is
    /// Google's). Null for a Voice id whose account key is malformed, which no connection owns.
    /// </summary>
    public static string? OfChat(string chatId)
    {
        if (AccountNamespace.KeyOf(chatId) is { } slot)
        {
            var service = chatId.StartsWith(Instagram, StringComparison.Ordinal) ? Instagram : chatId.StartsWith(Messenger, StringComparison.Ordinal) ? Messenger : chatId.StartsWith(Google, StringComparison.Ordinal) ? Google : null;
            return service is null ? null : new AccountNamespace(slot).Qualify(service);
        }
        if (chatId.StartsWith(Instagram, StringComparison.Ordinal)) return Instagram;
        if (chatId.StartsWith(VoicePrefix, StringComparison.Ordinal)) return VoiceKeyOf(chatId, VoicePrefix) is { } key ? Voice(key) : null;
        return chatId.StartsWith(Messenger, StringComparison.Ordinal) ? Messenger : Google;
    }

    /// <summary>
    /// The scope an attachment is saved from, as <c>MediaRouter.CanSave</c> decides it: Voice media by the key in its id, Instagram's and
    /// Messenger's by their media prefix on the id or the source, and a Google attachment only when its id says so. Null where no
    /// connection would save it.
    /// </summary>
    public static string? OfAttachment(MessageAttachment attachment)
    {
        if (attachment.Id is { } id && id.StartsWith(VoiceMedia, StringComparison.Ordinal)) return VoiceKeyOf(id, VoiceMedia) is { } key ? Voice(key) : null;
        if (Has(attachment, InstagramMedia)) return MediaScope(attachment, InstagramMedia, Instagram);
        if (Has(attachment, MessengerMedia)) return MediaScope(attachment, MessengerMedia, Messenger);
        return attachment.Id?.StartsWith(GoogleMedia, StringComparison.Ordinal) == true ? MediaScope(attachment, GoogleMedia, Google) : null;
    }

    /// <summary>An attachment id that <see cref="OfAttachment"/> maps back to <paramref name="scope"/>: what the host asks its own port about to learn whether a scope saves.</summary>
    private static string MediaScope(MessageAttachment attachment, string prefix, string service)
    {
        var value = attachment.Id?.StartsWith(prefix, StringComparison.Ordinal) == true ? attachment.Id : attachment.SourceUrl!;
        return new AccountNamespace(AccountNamespace.KeyOf(value)).Qualify(service);
    }

    public static string ProbeAttachmentId(string scope) => AccountNamespace.KeyOf(scope) is { } slot
        ? new AccountNamespace(slot).Qualify(ProbeAttachmentId(new AccountNamespace(slot).Native(scope))) : scope switch
    {
        Google => GoogleMedia + "probe",
        Messenger => MessengerMedia + "probe",
        Instagram => InstagramMedia + "probe",
        _ => VoiceMedia + scope[VoicePrefix.Length..] + "probe",
    };

    private static bool Has(MessageAttachment attachment, string prefix) =>
        attachment.Id?.StartsWith(prefix, StringComparison.Ordinal) == true || attachment.SourceUrl?.StartsWith(prefix, StringComparison.Ordinal) == true;

    // gvoice:<16 lowercase hex or digit>:<something>, the shape GoogleVoiceModels.Parse accepts.
    private static string? VoiceKeyOf(string value, string prefix)
    {
        var rest = value.AsSpan(prefix.Length);
        if (rest.Length <= VoiceKeyLength + 1 || rest[VoiceKeyLength] != ':') return null;
        var key = rest[..VoiceKeyLength];
        foreach (var c in key) if (!char.IsAsciiHexDigitLower(c) && !char.IsAsciiDigit(c)) return null;
        return key.ToString();
    }
}
