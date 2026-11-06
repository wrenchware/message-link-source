using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

/// <summary>
/// The client's copy of the host's pushed state (<see cref="HostState"/>): what the proxies' synchronous members answer from, so a
/// client on another machine needs no round trip for them. <see cref="RealtimeFeedProxy"/> feeds it from the event stream.
/// </summary>
/// <remarks>
/// <para><b>Before the first state</b> every answer is the conservative one: nothing connected, nothing configured, no capability, a
/// limit of zero ("unknown capability means unavailable"). A client that wants no such gap primes the cache with a state it
/// already holds (<see cref="Apply"/>): the in-process loopback does, from the host's builder, so the window never reads the gap.
/// A real subscription cannot lack a state for long, because the host follows every resync with one.</para>
/// <para><b>While the connection to the host is lost</b> (<see cref="MarkLost"/>) nothing is connected either, whatever the last state
/// said; what the accounts and scopes are configured as stays. The next state, or a resumed subscription, ends it.</para>
/// <para>Answers are read on any thread, from one immutable snapshot taken at the call. The events are raised on the thread that
/// applied a state, which is not the interface thread.</para>
/// </remarks>
public sealed class HostStateCache
{
    // The shape of a state with nothing known, for every account reader before the first state.
    private static readonly MessengerAccountState NoMessenger = new(false, false, false, MessengerEncryption.Unknown);
    private static readonly InstagramAccountState NoInstagram = new(false, false, null, null);
    private static readonly GoogleMessagesAccountState NoGoogle = new(false, false, false, false, null, null, false);

    private sealed class Snapshot
    {
        public Snapshot(HostState state)
        {
            State = state;
            Scopes = new Dictionary<string, ScopeState>(StringComparer.Ordinal);
            // A scope named twice is the host's mistake; the first stands.
            foreach (var scope in state.Scopes ?? []) if (scope?.Scope is { } name) Scopes.TryAdd(name, scope);
            Voice = state.Voice ?? [];
            VoiceKnownKeys = state.VoiceKnownKeys ?? [];
            Limits = state.Limits ?? new HostLimits();
            Typing = state.Typing ?? new TypingState();
            Revisions = state.Revisions ?? new AccountRevisions();
            AnyConnected = Scopes.Values.Any(scope => scope.Connected);
            IdentityJson = state.Identity is { } identity ? JsonSerializer.Serialize(identity, ProtocolJson.Options) : null;
        }

        public string? IdentityJson { get; }

        public HostState State { get; }
        public Dictionary<string, ScopeState> Scopes { get; }
        public IReadOnlyList<GoogleVoiceAccountState> Voice { get; }
        public IReadOnlyList<string> VoiceKnownKeys { get; }
        public HostLimits Limits { get; }
        public TypingState Typing { get; }
        public AccountRevisions Revisions { get; }
        public bool AnyConnected { get; }
    }

    private readonly object gate = new();
    private volatile Snapshot? current;
    private volatile bool lost;
    private string? currentJson;
    private string? appliedEpoch;
    private long appliedSeq;

    /// <summary>A state has been applied since this cache was made.</summary>
    public bool HasState => current is not null;

    /// <summary>The connection to the host is lost: nothing counts as connected until a state or a resumed subscription says otherwise.</summary>
    public bool IsLost => lost;

    /// <summary>The newest state applied, or none.</summary>
    public HostState? Current => current?.State;

    /// <summary>A state that differs from the last was applied (or the connection was lost or came back).</summary>
    public event EventHandler<HostState>? StateChanged;

    /// <summary>Each account's own change, with the same meaning as that account port's <c>Changed</c>: its snapshot differs, or the host said its conversation list changed.</summary>
    public event EventHandler? MessengerChanged;
    public event EventHandler? InstagramChanged;
    public event EventHandler? GoogleChanged;
    public event EventHandler? VoiceChanged;

    /// <summary>
    /// The identity the host reported (Google paired, the key local data is filed under, the saved-message scopes) differs from the last one, or is
    /// the first. Not raised by a state that carries none: the host then said nothing, and what was known stands.
    /// </summary>
    public event EventHandler<HostIdentity>? IdentityChanged;

    /// <summary>The facts no state shows, as the host announced them (<see cref="Signal"/>). Raised on the thread that delivered them, which is not the interface thread.</summary>
    public event EventHandler? SimsChanged;
    public event EventHandler? DefaultSmsAppChanged;
    public event EventHandler? ReconnectAbandoned;
    public event EventHandler<bool>? RenumberedChanged;

    /// <summary>
    /// Who is typing in one conversation, as the host sent it (SWITCHBOARD.md 2.6d). Not part of the state: typing is ephemeral, so nothing
    /// is kept here, a listener that comes later hears the next change, and a connection that is lost leaves the indicators to expire by
    /// their own <c>ExpiresAt</c>. Raised on the thread that read the frame, which is not the interface thread; a listener must hand
    /// the update on without waiting (<see cref="TypingProxy"/> does).
    /// </summary>
    public event EventHandler<TypingUpdate>? TypingArrived;

    /// <summary>Hands a typing snapshot the host sent to the listeners.</summary>
    public void ReceiveTyping(TypingUpdate update)
    {
        try { TypingArrived?.Invoke(this, update); }
        catch (Exception) { /* a listener that fails must not fail the feed */ }
    }

    /// <summary>
    /// A provider caught up on messages after connecting, as the host announced it in its feed (SWITCHBOARD.md 3.0b, <see cref="EventKinds.CaughtUp"/>). Like an
    /// account signal it is not state: nothing is kept, and a listener that comes later hears the next one. Raised on the thread that delivered it.
    /// </summary>
    public event EventHandler<CaughtUpNotice>? CaughtUpArrived;

    /// <summary>Hands a catch-up the host announced to the listeners.</summary>
    public void NoteCaughtUp(CaughtUpNotice notice)
    {
        try { CaughtUpArrived?.Invoke(this, notice); }
        catch (Exception) { /* a listener that fails must not fail the feed */ }
    }

    /// <summary>The server's own backup changed how it stands (protocol 26): a listener reads <c>server.backup.status</c> again. Raised on the thread that delivered it.</summary>
    public event EventHandler? BackupChanged;

    /// <summary>Contacts saved through the phone changed (protocol 29): a phone turned the setting on or off, or a request was submitted or answered. Raised on the thread that delivered it.</summary>
    public event EventHandler? PhoneContactsChanged;

    /// <summary>Raises the event an account signal names (SWITCHBOARD.md 2.6c). A name this build does not know says nothing it can act on and is ignored.</summary>
    public void Signal(AccountSignal signal)
    {
        try
        {
            switch (signal.Name)
            {
                case AccountSignals.Sims: SimsChanged?.Invoke(this, EventArgs.Empty); break;
                case AccountSignals.DefaultSmsApp: DefaultSmsAppChanged?.Invoke(this, EventArgs.Empty); break;
                case AccountSignals.ReconnectAbandoned: ReconnectAbandoned?.Invoke(this, EventArgs.Empty); break;
                case AccountSignals.Backup: BackupChanged?.Invoke(this, EventArgs.Empty); break;
                case AccountSignals.PhoneContacts: PhoneContactsChanged?.Invoke(this, EventArgs.Empty); break;
                // A renumbering with no value is not a statement either way.
                case AccountSignals.Renumbered when signal.Value is { } value: RenumberedChanged?.Invoke(this, value); break;
            }
        }
        catch (Exception) { /* a listener that fails must not fail the feed */ }
    }

    /// <summary>
    /// Takes a state the host pushed (or one a client primes the cache with). One identical to the last changes nothing and raises
    /// nothing. Ends a lost connection. Returns whether it differed. A state that carries its position (the host's
    /// <paramref name="epoch"/> and the <paramref name="seq"/> it was recorded at) is ignored when the cache already holds a newer one
    /// of the same epoch: the client applies a state the moment its frame arrives, ahead of the answer that follows it
    /// (<see cref="ProtocolClient"/>), and the feed then delivers it again, or older ones, which must not undo it.
    /// </summary>
    public bool Apply(HostState state, string? epoch = null, long seq = 0)
    {
        var next = new Snapshot(state);
        var json = JsonSerializer.Serialize(state, ProtocolJson.Options);
        Snapshot? previous;
        bool wasLost;
        lock (gate)
        {
            if (epoch is not null && string.Equals(epoch, appliedEpoch, StringComparison.Ordinal) && seq < appliedSeq) return false;
            if (epoch is not null) (appliedEpoch, appliedSeq) = (epoch, seq);
            wasLost = lost;
            lost = false;
            if (current is not null && string.Equals(json, currentJson, StringComparison.Ordinal))
            {
                if (wasLost) RaiseStateChanged(current.State);
                return false;
            }
            previous = current;
            current = next;
            currentJson = json;
        }
        RaiseStateChanged(state);
        if (previous is null || !MessengerNow(previous).Equals(MessengerNow(next)) || previous.Revisions.Messenger != next.Revisions.Messenger) Raise(MessengerChanged);
        if (previous is null || !InstagramNow(previous).Equals(InstagramNow(next)) || previous.Revisions.Instagram != next.Revisions.Instagram) Raise(InstagramChanged);
        if (previous is null || !GoogleNow(previous).Equals(GoogleNow(next)) || previous.Revisions.Google != next.Revisions.Google) Raise(GoogleChanged);
        if (previous is null || !previous.Voice.SequenceEqual(next.Voice) || !previous.VoiceKnownKeys.SequenceEqual(next.VoiceKnownKeys, StringComparer.Ordinal)
            || previous.Revisions.Voice != next.Revisions.Voice) Raise(VoiceChanged);
        if (state.Identity is { } identity && (previous?.IdentityJson is not { } before || !string.Equals(before, next.IdentityJson, StringComparison.Ordinal)))
        {
            try { IdentityChanged?.Invoke(this, identity); } catch (Exception) { /* a listener that fails must not fail the feed */ }
        }
        return true;
    }

    /// <summary>The connection to the host was lost: what was connected is not any more, until the next state.</summary>
    public void MarkLost()
    {
        HostState? announce = null;
        lock (gate)
        {
            if (lost) return;
            lost = true;
            announce = current?.State;
        }
        if (announce is not null) RaiseStateChanged(announce);
    }

    /// <summary>
    /// A subscription began again. One that resumes (<paramref name="resync"/> false) means no state was missed, so what was last
    /// known stands; one that starts with a resync is followed by a state, which ends the loss itself.
    /// </summary>
    public void Resubscribed(bool resync)
    {
        HostState? announce = null;
        lock (gate)
        {
            if (resync || !lost) return;
            lost = false;
            announce = current?.State;
        }
        if (announce is not null) RaiseStateChanged(announce);
    }

    private void RaiseStateChanged(HostState state)
    {
        try { StateChanged?.Invoke(this, state); } catch (Exception) { /* a listener that fails must not fail the feed */ }
    }

    private void Raise(EventHandler? handler)
    {
        try { handler?.Invoke(this, EventArgs.Empty); } catch (Exception) { /* as above */ }
    }

    // ---- The accounts ----

    private static MessengerAccountState MessengerNow(Snapshot snapshot) => snapshot.State.Messenger ?? NoMessenger;
    private static InstagramAccountState InstagramNow(Snapshot snapshot) => snapshot.State.Instagram ?? NoInstagram;
    private static GoogleMessagesAccountState GoogleNow(Snapshot snapshot) => snapshot.State.Google ?? NoGoogle;

    public MessengerAccountState Messenger => current is { } snapshot ? MessengerNow(snapshot) : NoMessenger;
    public InstagramAccountState Instagram => current is { } snapshot ? InstagramNow(snapshot) : NoInstagram;
    public GoogleMessagesAccountState Google => current is { } snapshot ? GoogleNow(snapshot) : NoGoogle;
    public IReadOnlyList<GoogleVoiceAccountState> VoiceAccounts => current?.Voice ?? [];
    public IReadOnlyList<string> VoiceKnownKeys => current?.VoiceKnownKeys ?? [];
    /// <summary>The identity in the newest state, or none: before the first state, or from a host that sent none.</summary>
    public HostIdentity? Identity => current?.State.Identity;
    public TypingState Typing => current?.Typing ?? new TypingState();
    public HostLimits Limits => current?.Limits ?? new HostLimits();

    // ---- IProviderConnectionState ----

    public bool AnyConnected => !lost && current is { AnyConnected: true };

    public bool IsConnected(string chatId) => ProviderScopes.OfChat(chatId) is { } scope && IsScopeConnected(scope);

    public bool IsScopeConnected(string scope) => !lost && current is { } snapshot && snapshot.Scopes.TryGetValue(scope, out var found) && found.Connected;

    // The routers' rule: a Voice scope is configured when it names a known account; Messenger's and Instagram's by their own session;
    // every other scope string is Google's.
    public bool IsConfigured(string scope)
    {
        if (current is not { } snapshot) return false;
        var key = scope.StartsWith(ProviderScopes.VoicePrefix, StringComparison.Ordinal) || scope == ProviderScopes.Messenger || scope == ProviderScopes.Instagram
            ? scope : ProviderScopes.Google;
        return snapshot.Scopes.TryGetValue(key, out var found) && found.Configured;
    }

    public bool NeedsSignInOnly(string scope) =>
        scope == ProviderScopes.Google && current is { } snapshot && snapshot.Scopes.TryGetValue(ProviderScopes.Google, out var found) && found.NeedsSignInOnly;

    // ---- What a scope's conversations and attachments can do ----

    public bool HoldsPartialCopies(string chatId) => ScopeOf(chatId) is { HoldsPartialCopies: true };

    public WindowReads WindowReadsFor(string chatId) => ScopeOf(chatId)?.WindowReads ?? WindowReads.Never;

    /// <summary>Whether the attachment's own scope saves it now. A draft this client made itself is the caller's to know.</summary>
    public bool CanSave(MessageAttachment attachment) =>
        !lost && ProviderScopes.OfAttachment(attachment) is { } scope && current is { } snapshot && snapshot.Scopes.TryGetValue(scope, out var found) && found.CanSave;

    /// <summary>
    /// The router's rule for drawing several photos at once: the conversation's scope allows it, and two to the batch limit of the
    /// files are still pictures (a GIF is not one).
    /// </summary>
    public bool DrawsPhotosAtOnce(string chatId, IReadOnlyList<AttachmentFile> files) =>
        // An id that names no scope is Google's for connection questions, but only a conversation that says so is one it draws for.
        chatId.StartsWith(ProviderScopes.Google, StringComparison.Ordinal) && ScopeOf(chatId) is { DrawsPhotosAtOnce: true } && current is { } snapshot && files.Count >= 2 && files.Count <= snapshot.Limits.BatchItemLimit
        && files.All(file => file.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !file.MimeType.Equals("image/gif", StringComparison.OrdinalIgnoreCase));

    private ScopeState? ScopeOf(string chatId) =>
        current is { } snapshot && ProviderScopes.OfChat(chatId) is { } scope && snapshot.Scopes.TryGetValue(scope, out var found) ? found : null;
}
