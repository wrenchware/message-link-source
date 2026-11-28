using System.Text.Json;
using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// media.prepare and media.requestCard (ProtocolMethods.Incoming.cs). Nothing here is logged: the services behind them write their own lines.
public sealed partial class ProtocolDispatcher
{
    /// <summary>The most a prepared clip may hold, whatever a client asks (the app asks for 8 MiB: a GIF's clip is a few hundred kilobytes).</summary>
    public const long PreparedClipCeiling = 8L * 1024 * 1024;

    /// <summary>The longest conversation or message id a card is asked for (the longest real one is a Voice account's, well under this).</summary>
    public const int MaxCardId = 512;

    private static Dictionary<string, Route> AddIncomingRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.MediaPrepare] = Of<IMediaPreparer, MediaPrepareParams, MediaPrepared>(s => s.MediaPreparer, async (port, p, ct) =>
        {
            if (p.Limit is < 0) throw Invalid();
            if (p.Limit is not { } limit)
            {
                var bytes = await port.PrepareImageAsync(p.Attachment, ct).ConfigureAwait(false);
                return new MediaPrepared(bytes > 0, bytes);
            }
            return new MediaPrepared(await port.PrepareClipAsync(p.Attachment, Math.Min(limit, PreparedClipCeiling), ct).ConfigureAwait(false));
        });
        routes[ProtocolMethods.MediaRequestCard] = new(typeof(RequestCardParams), typeof(Done), async (services, _, request, cancellationToken) =>
        {
            var parameters = ReadParams<RequestCardParams>(request);
            if (!ParamsShape.HasRequiredValues(typeof(RequestCardParams), request.Params) || parameters.ChatId.Length is 0 or > MaxCardId || parameters.MessageId.Length is 0 or > MaxCardId)
                throw Invalid();
            var port = services.IncomingCards ?? throw new NotSupportedException();
            await port.RequestAsync(parameters.ChatId, parameters.MessageId, parameters.LinkPreviews, parameters.Spam, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
        });
        return routes;
    }
}
