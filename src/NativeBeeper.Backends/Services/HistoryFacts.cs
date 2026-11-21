namespace NativeBeeper.App.Services;

/// <summary>
/// What a service says of itself that the server's whole history reads and fills by. Each service states its own; one
/// that says nothing is <see cref="Plain"/>.
/// </summary>
/// <param name="ReadsCostBudget">Every read is a request on the real account out of a budget (Instagram's). The history
/// then reads nothing nobody asked for outside that budget: no read again when a sync finds it stale or the list names
/// it, no list row read for it, and fewer pages looking back for the floor.</param>
/// <param name="EchoesOwnSends">Announces what is sent from here as it announces what arrives. A service that does not
/// (Instagram announces only arrivals) has its conversation read back after a send, or the copy never holds the reply.</param>
/// <param name="EventsNameEveryArrival">An event names every message that arrived. One that names only the newest since
/// the last (Instagram's polls) never says the copy is whole up to it, and the conversation is read again.</param>
public sealed record HistoryFacts(bool ReadsCostBudget = false, bool EchoesOwnSends = true, bool EventsNameEveryArrival = true)
{
    public static HistoryFacts Plain { get; } = new();
}

/// <summary>Each conversation's service's <see cref="HistoryFacts"/>, and its budget for reads nobody asked for.</summary>
public interface IHistoryFacts
{
    HistoryFacts FactsOf(string chatId);

    /// <summary>Takes one read nobody asked for (filling or catching up a conversation in the background) from the
    /// account's budget: false puts it off. True wherever reads cost no budget.</summary>
    bool TryTakeUnaskedRead(string chatId);
}
