using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

/// <summary>The real ports a host builds its state from. Each is the object the Core interface names; the typing port may be absent.</summary>
public sealed record HostStatePorts(IProviderConnectionState Connections, IMessagePeek Peek, IHistoryWindowReader History, IAttachmentBackend Attachments,
    IAttachmentBatchBackend Batches, IMessageActions Actions, IFirstMessageStarter FirstMessage, IGroupManagement Groups, IMessengerAccount Messenger,
    IInstagramAccount Instagram, IGoogleMessagesAccount Google, IGoogleVoiceAccounts Voice, ITypingService? Typing = null, IHostIdentity? Identity = null,
    bool GoogleAbsent = false, bool VoiceAbsent = false, bool InstagramAbsent = false, bool UnarchivesChats = false, bool FiresScheduled = false,
    Func<bool>? ChromeOutOfDate = null);

/// <summary>
/// Reads a <see cref="HostState"/> from the real ports. It asks the ports themselves, not a copy of their rules, so the state says
/// what the routers say: a flag per scope comes from asking the port about a made-up id in that scope (<c>HoldsPartialCopies</c> of
/// <c>instagram:probe</c>), which answers the same for any id the scope owns. Reads only: nothing here starts, sends or marks anything.
/// </summary>
public sealed class HostStateBuilder(HostStatePorts ports, int batchItemLimit)
{
    private static readonly AttachmentFile ProbeFile = new("probe.jpg", "image/jpeg", 1, _ => Task.FromResult<Stream>(Stream.Null));

    public HostState Build()
    {
        // Google, Messenger and Instagram always; one scope per Voice account the host knows (listed or saved), in a fixed order.
        var keys = ports.Voice.KnownAccountKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var scopes = new List<ScopeState> { Scope(ProviderScopes.Google), Scope(ProviderScopes.Messenger), Scope(ProviderScopes.Instagram) };
        scopes.AddRange(keys.Select(key => Scope(ProviderScopes.Voice(key))));
        return new HostState(scopes, ports.Messenger.State, ports.Instagram.State, ports.Google.State, [.. ports.Voice.Accounts], keys,
            new HostLimits(ports.Actions.HasLocalActionOptions, ports.FirstMessage.MaxFirstMessageLength, ports.Groups.GroupNameMaximum, ports.Groups.AddMaximum, batchItemLimit,
                ports.GoogleAbsent, ports.VoiceAbsent, ports.InstagramAbsent, ports.UnarchivesChats, ports.FiresScheduled, ports.ChromeOutOfDate?.Invoke() == true),
            new TypingState(ports.Typing?.CanReceive ?? false, ports.Typing?.CanSend ?? false), new AccountRevisions(), Identity());
    }

    // Absent rather than failed when it cannot be read: a client then keeps the identity it last knew, and the next look tries again. The
    // read is asynchronous (the Google profile is unprotected from a file); it runs on the pool so a caller on an interface thread cannot deadlock on it.
    private HostIdentity? Identity()
    {
        if (ports.Identity is not { } identity) return null;
        try { return Task.Run(() => identity.ReadAsync()).GetAwaiter().GetResult(); }
        catch (Exception) { return null; }
    }

    private ScopeState Scope(string scope)
    {
        var sampleChat = scope + "probe";
        var sampleAttachment = new MessageAttachment(null, "file", null, null, false, false, false, Id: ProviderScopes.ProbeAttachmentId(scope));
        // Google's archive has crawled some conversations and not others, which one id cannot say; the others answer for every id.
        var window = scope == ProviderScopes.Google ? WindowReads.PerChat : ports.History.CanReadWindow(sampleChat) ? WindowReads.Always : WindowReads.Never;
        return new ScopeState(scope, ports.Connections.IsScopeConnected(scope), ports.Connections.IsConfigured(scope), ports.Connections.NeedsSignInOnly(scope),
            ports.Peek.HoldsPartialCopies(sampleChat), window, ports.Attachments.CanSave(sampleAttachment),
            ports.Batches.DrawsPhotosAtOnce(sampleChat, [ProbeFile, ProbeFile]));
    }
}
