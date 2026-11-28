using MessageLink.Core.Services;
using System.Text.Json;

namespace MessageLink.Protocol;

// Hermes's methods (protocol 31, the answer to an approval 32; refused to an older connection by their catalogue version).
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddHermesRoutes(Dictionary<string, Route> routes)
    {
        // Keys cross only here, and only from a device the server takes them from: a server refuses every device (its page has the form).
        routes[ProtocolMethods.AccountsHermesConfigure] = PageOnly(ProtocolMethods.AccountsHermesConfigure,
            AccountMutation<IHermesAccount, HermesConfigureParams, Done>(s => s.HermesAccount, async (port, p, ct) =>
            {
                try { await port.ConfigureAsync(p.Address, p.Profiles ?? [], ct).ConfigureAwait(false); }
                catch (ArgumentException refused)
                {
                    throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, refused.Message));
                }
                return new Done();
            }));
        routes[ProtocolMethods.AccountsHermesNewConversation] = AccountMutation<IHermesAccount, HermesChatParams, Done>(s => s.HermesAccount,
            async (port, p, ct) =>
            {
                if (!HermesIds.Owns(p.ChatId)) throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "That isn't a Hermes conversation."));
                await port.NewConversationAsync(p.ChatId, ct).ConfigureAwait(false);
                return new Done();
            });
        routes[ProtocolMethods.AccountsHermesAnswerApproval] = AccountMutation<IHermesAccount, HermesApprovalParams, HermesApprovalAnswer>(
            s => s.HermesAccount, async (port, p, ct) =>
            {
                if (!HermesIds.Owns(p.ChatId) || HermesIds.ProfileOf(p.MessageId) != HermesIds.ProfileOf(p.ChatId))
                    throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "That isn't a Hermes conversation."));
                try { return new HermesApprovalAnswer(await port.AnswerApprovalAsync(p.ChatId, p.MessageId, p.Approve, ct).ConfigureAwait(false)); }
                catch (ArgumentException refused)
                {
                    throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, refused.Message));
                }
            });
        routes[ProtocolMethods.RepeatsCreate] = Mutation<IRepeatCommands, RepeatCreateParams, RepeatAnswer>(s => s.Repeats, (port, p, ct) => port.CreateAsync(p, ct));
        routes[ProtocolMethods.RepeatsCancel] = Mutation<IRepeatCommands, RepeatCancelParams, RepeatAnswer>(s => s.Repeats, (port, p, ct) => port.CancelAsync(p, ct));
        // The privacy line holds for a client that does not know Hermes: one below 31 offers a Hermes thread as a forward target and in a merge, so
        // nothing it sends reaches Hermes.
        routes[ProtocolMethods.MessagesSend] = HermesFrom31<SendTextParams>(routes[ProtocolMethods.MessagesSend], p => p.ChatId);
        routes[ProtocolMethods.AttachmentsSend] = HermesFrom31<AttachmentSendParams>(routes[ProtocolMethods.AttachmentsSend], p => p.ChatId);
        routes[ProtocolMethods.MessagesReply] = HermesFrom31<ReplyParams>(routes[ProtocolMethods.MessagesReply], p => p.ChatId);
        return routes;
    }

    internal const string HermesNeedsUpdate = "Update Message Link to write to Hermes.";

    // A scheduled command from a client below 31 on a Hermes conversation's message: answered refused, which every client shows as not kept, rather than an
    // error a client could read as an unknown outcome. A create names its conversation; an edit or a send-now is looked at by the schedule under the message's
    // own gate (`refusing`), since a concurrent cancel can take the message out of the list while its save is pending and put it back when the save fails.
    private static Route ScheduledHermesFrom31<TParams>(Route route, Func<IScheduledCommands, TParams, CancellationToken, Task<ScheduledAnswer>> refusing) where TParams : class
    {
        var prepare = route.Prepare ?? throw new InvalidOperationException("not a mutation");
        return route with
        {
            Prepare = (connection, services, request) =>
            {
                var prepared = prepare(connection, services, request);
                if (connection.negotiated >= ProtocolVersion.Hermes || services.Scheduled is not { } port) return prepared;
                var parameters = ReadParams<TParams>(request);
                return prepared with
                {
                    Call = async cancellationToken =>
                        JsonSerializer.SerializeToUtf8Bytes(await refusing(port, parameters, cancellationToken).ConfigureAwait(false), ProtocolJson.Options),
                };
            },
        };
    }

    private static Task<ScheduledAnswer> HermesCreateFrom31(IScheduledCommands port, ScheduledCreateParams request, CancellationToken cancellationToken) =>
        HermesIds.Owns(request.ChatId) ? Task.FromResult(new ScheduledAnswer(ScheduledStatuses.Refused, Message: HermesNeedsUpdate)) : port.CreateAsync(request, cancellationToken);

    private static Route HermesFrom31<TParams>(Route route, Func<TParams, string?> chat) where TParams : class
    {
        var prepare = route.Prepare ?? throw new InvalidOperationException("not a mutation");
        return route with
        {
            Prepare = (connection, services, request) =>
            {
                if (connection.negotiated < ProtocolVersion.Hermes && request.HasParams && HermesIds.Owns(chat(ReadParams<TParams>(request))))
                    throw new SendException(false, HermesNeedsUpdate);
                return prepare(connection, services, request);
            },
        };
    }
}
