namespace MessageLink.GoogleMessages.Spike;

// What the transport does with each helper method, in one place so the two columns can't drift apart again (they had:
// an edit, an unsend and a batch photo send waited behind background reads, and Messenger's group changes were counted
// by neither). A method not listed is a background read: no priority, not counted.
//
// Priority: the person is waiting on it, so its frame goes ahead of queued background reads (HelperCallGate).
// Mutation: someone on the other end would see it (a send, a reaction, a read mark, an edit, a deletion, a new
// conversation or a group change). A restart's idle seal refuses while one is in flight and shutdown gives it a moment to
// be answered, rather than closing its helper with the outcome unknown. Staging and typing are not: the other side keeps
// neither. Every helper's own mutationMethod list (dispatch.go) is checked against this one (HelperConcurrencyChecks).
internal static class HelperMethods
{
    private const bool Yes = true, No = false;

    private static readonly Dictionary<string, (bool Priority, bool Mutation)> Table = new(StringComparer.Ordinal)
    {
        ["ui.sendText"] = (Yes, Yes),
        ["ui.sendMedia"] = (Yes, Yes),
        ["ui.sendMediaBatch"] = (Yes, Yes),
        ["ui.react"] = (Yes, Yes),
        ["ui.markRead"] = (Yes, Yes),
        ["ui.editMessage"] = (Yes, Yes),
        ["ui.deleteMessage"] = (Yes, Yes),
        ["ui.updateChat"] = (Yes, Yes),
        ["ui.deleteChat"] = (Yes, Yes),
        ["ui.startDirect"] = (Yes, Yes),
        ["ui.startDirectWithText"] = (Yes, Yes),
        ["ui.startGroup"] = (Yes, Yes),
        ["ui.createGroup"] = (Yes, Yes),
        ["ui.renameGroup"] = (Yes, Yes),
        ["ui.addGroupMembers"] = (Yes, Yes),
        ["ui.removeGroupMember"] = (Yes, Yes),
        // A send about to happen: staged ahead of reads, but nothing anyone sees yet.
        ["ui.mediaStage"] = (Yes, No),
        ["ui.mediaBatchStage"] = (Yes, No),
        ["ui.sendPermission"] = (Yes, No),
    };

    // Queued with the sends by a helper, but neither: typing is worth nothing late and is skipped when the pipe is busy,
    // discarding a failed batch's staged files is cleanup nobody waits on, and probe.receipt is the receipt probe's own.
    internal static readonly IReadOnlySet<string> HelperQueuedOnly = new HashSet<string>(StringComparer.Ordinal)
    { "ui.typing", "ui.mediaStage", "ui.mediaBatchStage", "ui.mediaBatchDiscard", "probe.receipt" };

    public static bool IsPriority(string method) => Table.TryGetValue(method, out var row) && row.Priority;
    public static bool IsMutation(string method) => Table.TryGetValue(method, out var row) && row.Mutation;
    internal static IEnumerable<string> Mutations => Table.Where(row => row.Value.Mutation).Select(row => row.Key);
}
