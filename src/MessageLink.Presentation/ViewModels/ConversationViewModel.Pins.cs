using MessageLink.Core.Messaging;
using MessageLink.Presentation.Services;

namespace MessageLink.Presentation.ViewModels;

// "Always send with <service> to <name>" in a merged conversation (MergedConversations.Pins.cs).
public sealed partial class ConversationViewModel
{
    /// <summary>
    /// Said once when a pin ended by itself (its conversation left the merge, or its service signed out). Kept apart from
    /// Status, whose routine progress lines would take it down again within the same open.
    /// </summary>
    public string? RouteNotice { get; private set; }
    private EndedPin? announcedPinEnd;

    /// <summary>Pins the route the user chose in the picker, or clears the pin. Errors are reported, never retried.</summary>
    public async Task SetRoutePinnedAsync(ConversationMember member, bool pinned)
    {
        if (!IsMerged || merges is null || ActiveChatId is not { } id || !Members.Any(m => m.Id == member.Id)) return;
        var version = generation;
        try
        {
            if (pinned) await merges.PinSendingRouteAsync(id, member.Id);
            else await merges.UnpinSendingRouteAsync(id);
            if (version != generation || ActiveChatId != id) return;
            if (pinned) SetSendChat(member.Id);
            RefreshMergedRoutes();
        }
        catch (Exception)
        {
            if (version == generation) Status = pinned ? "Could not keep this service. Try again." : "Could not stop always using this service. Try again.";
        }
    }

    // Once per ended pin: the notice, and the write that keeps it from being said again. A failed write leaves it to be
    // said the next time the conversation opens, which is still once as far as anyone reading is concerned.
    private void AnnouncePinEnded(MergedConversation? group)
    {
        // The very record read from the store, not its contents: the same service can end the same way twice in a session.
        if (group?.PinEnded is not { } ended || merges is null || ReferenceEquals(announcedPinEnd, ended)) return;
        announcedPinEnd = ended;
        RouteNotice = MergedConversations.PinEndedText(ended);
        OnPropertyChanged(nameof(RouteNotice));
        _ = AcknowledgePinEndedAsync(merges, group.Id);
    }
    // A pin passed over while its service waits to be signed in again is said once, when the route first leaves it; it is
    // said again only after the service has been back (RefreshMergedRoutes clears this when the pin holds again).
    private string? pinWaitingSaid;
    private void AnnouncePinWaiting(ChatSummary pinned)
    {
        if (pinWaitingSaid == pinned.Id) return;
        pinWaitingSaid = pinned.Id;
        RouteNotice = MergedConversations.PinWaitingText(pinned.Network);
        OnPropertyChanged(nameof(RouteNotice));
    }
    private static async Task AcknowledgePinEndedAsync(MergedConversations merges, string id)
    {
        try { await merges.AcknowledgePinEndedAsync(id); }
        catch (Exception) { /* Said again at the next open; nothing else depends on it. */ }
    }
    private void ClearRouteNotice()
    {
        if (RouteNotice is null) return;
        RouteNotice = null;
        OnPropertyChanged(nameof(RouteNotice));
    }
}
