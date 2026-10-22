namespace NativeBeeper.Protocol;

// scheduled.create, edit, cancel and sendNow (ProtocolMethods.Scheduled.cs): journaled mutations like any other, so a repeat of one operation id answers the
// first answer and a lost answer is settled with ops.status. What each does to the schedule, and its once-only rules, are the host's schedule's
// (HostScheduledSends); nothing is logged here.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddScheduledRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.ScheduledCreate] = Mutation<IScheduledCommands, ScheduledCreateParams, ScheduledAnswer>(s => s.Scheduled, (port, p, ct) => port.CreateAsync(p, ct));
        routes[ProtocolMethods.ScheduledEdit] = Mutation<IScheduledCommands, ScheduledCommandParams, ScheduledAnswer>(s => s.Scheduled, (port, p, ct) => port.EditAsync(p, ct));
        routes[ProtocolMethods.ScheduledCancel] = Mutation<IScheduledCommands, ScheduledCommandParams, ScheduledAnswer>(s => s.Scheduled, (port, p, ct) => port.CancelAsync(p, ct));
        routes[ProtocolMethods.ScheduledSendNow] = Mutation<IScheduledCommands, ScheduledCommandParams, ScheduledAnswer>(s => s.Scheduled, (port, p, ct) => port.SendNowAsync(p, ct));
        routes[ProtocolMethods.OutboxDismiss] = Mutation<IScheduledCommands, OutboxDismissParams, Done>(s => s.Scheduled, (port, p, ct) => port.DismissAsync(p, ct));
        return routes;
    }
}
