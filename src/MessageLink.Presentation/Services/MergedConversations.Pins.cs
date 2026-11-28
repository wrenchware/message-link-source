using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public sealed record PinnedSendingRoute(string ChatId, DateTimeOffset PinnedAt);
/// <summary>A pin that ended without the user clearing it: which service it named, and why it ended.</summary>
public sealed record EndedPin(string Service, string Reason)
{
    public const string Left = "left", SignedOut = "signed-out";
}

// "Always send with <service> to <name>": an opt-in, per merged conversation, that keeps one linked conversation as the
// sending route whatever arrives. Without it the approved rule is untouched: a manual choice lasts until the next incoming
// message. Replies still use the quoted message's own service for that one message. Local, like everything else about a
// merge: no service is told.
public sealed partial class MergedConversations
{
    public PinnedSendingRoute? PinnedRoute(string id) => Find(id)?.PinnedRoute;

    /// <summary>
    /// Whether a conversation's service has stopped and is waiting on the user — its session expired, a checkpoint, a
    /// pairing the phone removed, a sign-in Google no longer accepts — given the connection scope ("instagram:",
    /// "gvoice:&lt;account&gt;:"). Set by the app from what the services report; unset, nothing is.
    /// </summary>
    /// <remarks>
    /// A pin to such a conversation is passed over, as a read-only one is, without being ended: the composer and the chat
    /// console send by the usual rule until the service is back, and then by the pin again. Only that state, not a
    /// connection that merely dropped: a helper restarting is back within seconds, and the route would otherwise move under
    /// whoever is writing and move back. A service the user signs out of ends its pins (<see cref="EndPinsAsync"/>).
    /// </remarks>
    public Func<string, bool>? ServiceNeedsSignIn { get; set; }

    /// <summary>Whether this conversation's service is waiting on the user to sign in again (<see cref="ServiceNeedsSignIn"/>).</summary>
    public bool NeedsSignIn(ChatSummary member) =>
        ServiceNeedsSignIn is { } waiting && ServiceScopes.Scope(member.Id) is { } scope && waiting(scope);

    /// <summary>Said once when a pinned service is passed over because it needs signing in again.</summary>
    public static string PinWaitingText(string service) =>
        $"{service} has stopped and needs attention in Settings, so messages go with the newest message's service until it's back.";

    /// <summary>
    /// Keeps this linked text conversation as the route until the user clears it, it leaves the merge or its service signs
    /// out. It is also made the manual choice, so clearing the pin later leaves the route where it is until the next
    /// incoming message, as any manual choice would.
    /// </summary>
    public Task PinSendingRouteAsync(string id, string chatId)
    {
        if (!Members(id).Any(c => c.Id == chatId && !IsCall(c) && !c.IsReadOnly))
            throw new InvalidOperationException("Choose a linked text conversation.");
        var now = Now();
        return SetAsync(id, group => group with { PinnedRoute = new(chatId, now), SendingRoute = new(chatId, now), PinEnded = null });
    }

    /// <summary>
    /// Clears the pin. The service it named stays the choice as if chosen now, so nothing moves at the moment it is cleared;
    /// the next incoming message decides after that, as it always did.
    /// </summary>
    public Task UnpinSendingRouteAsync(string id)
    {
        if (Find(id)?.PinnedRoute is null) return Task.CompletedTask;
        var now = Now();
        return SetAsync(id, group => group.PinnedRoute is { } pin ? group with { PinnedRoute = null, SendingRoute = new(pin.ChatId, now) } : group);
    }

    /// <summary>
    /// Ends every pin whose conversation belongs to a service that was signed out, in one write, and remembers for each
    /// merged conversation that it has to say so once. A manual choice of the same conversation goes too, so sending falls
    /// back to the newest incoming message. Returns the merged conversations whose pin ended.
    /// </summary>
    public async Task<IReadOnlyList<string>> EndPinsAsync(Func<ChatSummary, bool> signedOut)
    {
        await EnsureLoadedAsync();
        bool Ends(MergedConversation group) => group.PinnedRoute is { } pin && Members(group).FirstOrDefault(m => m.Id == pin.ChatId) is { } member && signedOut(member);
        if (!groups.Any(Ends)) return [];
        IReadOnlyList<string> result = [];
        await ChangeAsync(existing =>
        {
            // A list of its own each time: a client of a server makes this change again over another device's merges (CatchUpAsync), after the list
            // handed back here is the caller's.
            var ended = new List<string>();
            result = ended;
            return [.. existing.Select(group =>
            {
                if (!Ends(group)) return group;
                ended.Add(group.Id);
                var pin = group.PinnedRoute!;
                var service = Members(group).First(m => m.Id == pin.ChatId).Network;
                return group with
                {
                    PinnedRoute = null, PinEnded = new(service, EndedPin.SignedOut),
                    SendingRoute = group.SendingRoute?.ChatId == pin.ChatId ? null : group.SendingRoute
                };
            })];
        });
        return result;
    }

    /// <summary>The ended pin has been told; it is not told again.</summary>
    public Task AcknowledgePinEndedAsync(string id) =>
        Find(id)?.PinEnded is null ? Task.CompletedTask : SetAsync(id, group => group with { PinEnded = null });

    public static string PinEndedText(EndedPin ended) => ended.Reason == EndedPin.SignedOut
        ? $"{ended.Service} was signed out, so it's no longer always used here. Messages go with the newest message's service again."
        : $"{ended.Service} left this conversation, so it's no longer always used here. Messages go with the newest message's service again.";

    // A group whose pinned conversation does not stay loses the pin and remembers, once, that it did, and why.
    private static MergedConversation WithoutPinOn(MergedConversation group, IReadOnlyList<ChatSummary> before, Func<string, bool> stays,
        string reason = EndedPin.Left)
    {
        if (group.PinnedRoute is not { } pin || stays(pin.ChatId)) return group;
        var service = before.FirstOrDefault(m => m.Id == pin.ChatId)?.Network ?? "That service";
        return group with { PinnedRoute = null, PinEnded = new(service, reason) };
    }
}
