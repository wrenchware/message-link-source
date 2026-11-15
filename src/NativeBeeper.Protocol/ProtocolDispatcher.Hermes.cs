using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

// Hermes's methods (protocol 31): only to a connection of 31 or later.
public sealed partial class ProtocolDispatcher
{
    private static Route Since(int version, Route route)
    {
        var prepare = route.Prepare ?? throw new InvalidOperationException("not a mutation");
        return route with
        {
            Prepare = (connection, services, request) =>
            {
                if (connection.negotiated < version) throw new NotSupportedException();
                return prepare(connection, services, request);
            },
        };
    }

    private static Dictionary<string, Route> AddHermesRoutes(Dictionary<string, Route> routes)
    {
        // Keys cross only here, and only from a device the host takes them from: a server refuses every device (its page has the form).
        routes[ProtocolMethods.AccountsHermesConfigure] = Since(ProtocolVersion.Hermes, PageOnly(ProtocolMethods.AccountsHermesConfigure,
            AccountMutation<IHermesAccount, HermesConfigureParams, Done>(s => s.HermesAccount, async (port, p, ct) =>
            {
                try { await port.ConfigureAsync(p.Address, p.Profiles ?? [], ct).ConfigureAwait(false); }
                catch (ArgumentException refused)
                {
                    throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, refused.Message));
                }
                return new Done();
            })));
        routes[ProtocolMethods.AccountsHermesNewConversation] = Since(ProtocolVersion.Hermes, AccountMutation<IHermesAccount, HermesChatParams, Done>(s => s.HermesAccount,
            async (port, p, ct) =>
            {
                if (!HermesIds.Owns(p.ChatId)) throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "That isn't a Hermes conversation."));
                await port.NewConversationAsync(p.ChatId, ct).ConfigureAwait(false);
                return new Done();
            }));
        routes[ProtocolMethods.RepeatsCreate] = Since(ProtocolVersion.Hermes, Mutation<IRepeatCommands, RepeatCreateParams, RepeatAnswer>(s => s.Repeats, (port, p, ct) => port.CreateAsync(p, ct)));
        routes[ProtocolMethods.RepeatsCancel] = Since(ProtocolVersion.Hermes, Mutation<IRepeatCommands, RepeatCancelParams, RepeatAnswer>(s => s.Repeats, (port, p, ct) => port.CancelAsync(p, ct)));
        // The privacy line holds for a client that does not know Hermes: one below 31 offers a Hermes thread as a forward target and in a merge, so
        // nothing it sends reaches Hermes.
        routes[ProtocolMethods.MessagesSend] = HermesFrom31<SendTextParams>(routes[ProtocolMethods.MessagesSend], p => p.ChatId);
        routes[ProtocolMethods.AttachmentsSend] = HermesFrom31<AttachmentSendParams>(routes[ProtocolMethods.AttachmentsSend], p => p.ChatId);
        routes[ProtocolMethods.MessagesReply] = HermesFrom31<ReplyParams>(routes[ProtocolMethods.MessagesReply], p => p.ChatId);
        return routes;
    }

    internal const string HermesNeedsUpdate = "Update Message Link to write to Hermes.";

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
