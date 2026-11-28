using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// scheduled.create, edit, cancel and sendNow (ProtocolMethods.Scheduled.cs): journaled mutations like any other, so a repeat of one operation id answers the
// first answer and a lost answer is settled with ops.status. What each does to the schedule, and its once-only rules, are the server's schedule's
// (ServerScheduledSends); nothing is logged here.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddScheduledRoutes(Dictionary<string, Route> routes)
    {
        // The Hermes privacy line (ProtocolDispatcher.Hermes.cs) holds through the schedule too: a client below 31 neither schedules, rewrites nor sends early a
        // message to Hermes. A cancel only stops one, so it stays open to every client.
        routes[ProtocolMethods.ScheduledCreate] = ScheduledHermesFrom31(Mutation<IScheduledCommands, ScheduledCreateParams, ScheduledAnswer>(s => s.Scheduled, (port, p, ct) => port.CreateAsync(p, ct)),
            (IScheduledCommands port, ScheduledCreateParams p, CancellationToken ct) => HermesCreateFrom31(port, p, ct));
        routes[ProtocolMethods.ScheduledEdit] = ScheduledHermesFrom31(Mutation<IScheduledCommands, ScheduledCommandParams, ScheduledAnswer>(s => s.Scheduled, (port, p, ct) => port.EditAsync(p, ct)),
            (IScheduledCommands port, ScheduledCommandParams p, CancellationToken ct) => port.EditAsync(p, HermesIds.Owns, HermesNeedsUpdate, ct));
        routes[ProtocolMethods.ScheduledCancel] = Mutation<IScheduledCommands, ScheduledCommandParams, ScheduledAnswer>(s => s.Scheduled, (port, p, ct) => port.CancelAsync(p, ct));
        routes[ProtocolMethods.ScheduledSendNow] = ScheduledHermesFrom31(Mutation<IScheduledCommands, ScheduledCommandParams, ScheduledAnswer>(s => s.Scheduled, (port, p, ct) => port.SendNowAsync(p, ct)),
            (IScheduledCommands port, ScheduledCommandParams p, CancellationToken ct) => port.SendNowAsync(p, HermesIds.Owns, HermesNeedsUpdate, ct));
        routes[ProtocolMethods.RefusedDismiss] = Mutation<IScheduledCommands, RefusedDismissParams, Done>(s => s.Scheduled, (port, p, ct) => port.DismissAsync(p, ct));
        return routes;
    }
}
