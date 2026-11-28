using System.Runtime.CompilerServices;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

/// <summary>
/// What the typing router is to the window (SWITCHBOARD.md 2.6d). <c>CanReceive</c> and <c>CanSend</c> are the host's flags from its pushed
/// state. Starting and stopping typing cross as <c>typing.set</c>; the plain call is not admitted, and the private-reading gate over this
/// proxy passes the admission it decided (<see cref="IAdmittedTypingService"/>). The typing of others comes down the connection's event
/// feed as <c>typing</c> frames: every <see cref="WatchAsync"/> hears them from the moment it subscribes, newest snapshot per
/// conversation, with nothing replayed (a connection that is lost and comes back misses what changed meanwhile, and what was showing
/// expires by its own <c>ExpiresAt</c>). Typing is sent only to a feed that asked for it, which <see cref="RealtimeFeedProxy"/> does when
/// it has a state cache.
/// </summary>
public sealed class TypingProxy(ProtocolClient client, HostStateCache state) : IAdmittedTypingService
{
    public bool CanReceive => state.Typing.CanReceive;
    public bool CanSend => state.Typing.CanSend;

    public async IAsyncEnumerable<TypingUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var slot = new TypingSlot();
        void Arrived(object? sender, TypingUpdate update) => slot.Push(update);
        state.TypingArrived += Arrived;
        try
        {
            while (await slot.ReadAsync(cancellationToken).ConfigureAwait(false) is { } update) yield return update;
        }
        finally
        {
            state.TypingArrived -= Arrived;
            slot.Complete();
        }
    }

    /// <summary>Not admitted: the host's router refuses a start for a conversation read privately.</summary>
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default) => SetTypingAsync(chatId, isTyping, false, cancellationToken);

    public Task SetTypingAsync(string chatId, bool isTyping, bool admitted, CancellationToken cancellationToken = default) =>
        client.CallAsync<TypingSetParams, Done>(ProtocolMethods.TypingSet, new(chatId, isTyping, admitted), cancellationToken);
}
