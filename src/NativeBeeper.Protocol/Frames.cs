using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NativeBeeper.Protocol;

/// <summary>
/// One message on the wire: a JSON object whose <c>"t"</c> member names its kind. Read with <see cref="Frames.TryParse(string, out Frame?, out ProtocolError?)"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "t", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(HelloFrame), "hello")]
[JsonDerivedType(typeof(RequestFrame), "req")]
[JsonDerivedType(typeof(ResponseFrame), "res")]
[JsonDerivedType(typeof(CancelFrame), "cancel")]
[JsonDerivedType(typeof(EventFrame), "event")]
public abstract record Frame;

/// <summary>
/// First frame each way. The client says which <paramref name="Protocol"/> version it speaks (the highest;
/// <paramref name="MinProtocol"/> is the lowest it can fall back to, absent meaning only <paramref name="Protocol"/>) and
/// who it is (<paramref name="Client"/>: "windows", "android"); the server answers with the version it chose and its
/// product word (<paramref name="Server"/>). <paramref name="App"/> is the sender's application version, for logs and
/// update notices. A server that speaks no version the client can answers with <paramref name="Error"/>
/// (protocol_unsupported) in a hello of its own, because a hello has no request id for a response frame to name.
/// A client may also say what it runs on, for the server page's list of devices only: <paramref name="Os"/> ("Windows 11 Pro", "Android 17"),
/// <paramref name="OsVersion"/> ("25H2, build 26300.1000") and <paramref name="Model"/> ("Samsung SM-S948U"). Plain words, never acted on; a server
/// that does not know them skips them.
/// </summary>
public sealed record HelloFrame(int Protocol, string App, string? Client = null, string? Server = null, int? MinProtocol = null,
    ProtocolError? Error = null, string? Os = null, string? OsVersion = null, string? Model = null) : Frame
{
    public const string ServerName = "messages-server";
}

/// <summary>A call. <paramref name="Id"/> is the client's, unique per connection; <paramref name="Params"/> is a JSON object or absent.</summary>
public sealed record RequestFrame(string Id, string Method,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Params = default) : Frame
{
    public static RequestFrame Create<T>(string id, string method, T value) => new(id, method, ProtocolJson.ToElement(value));
    public bool HasParams => Params.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
    public T? ParamsAs<T>() => ProtocolJson.FromElement<T>(Params);
}

/// <summary>
/// The answer to the request with the same <paramref name="Id"/>: exactly one of <paramref name="Result"/> (any JSON,
/// a literal <c>null</c> included) or <paramref name="Error"/>.
/// </summary>
public sealed record ResponseFrame(string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Result = default,
    ProtocolError? Error = null) : Frame
{
    public static ResponseFrame Success<T>(string id, T value) => new(id, ProtocolJson.ToElement(value));
    public static ResponseFrame Failure(string id, ProtocolError error) => new(id, default, error);
    public bool HasResult => Result.ValueKind is not JsonValueKind.Undefined;
    public T? ResultAs<T>() => ProtocolJson.FromElement<T>(Result);
}

/// <summary>Asks the host to stop working on the outstanding request <paramref name="Id"/>. The request still gets its answer.</summary>
public sealed record CancelFrame(string Id) : Frame;

/// <summary>
/// One item of the event feed a connection asked for with <c>events.subscribe</c>. <paramref name="Seq"/> counts the host's
/// events from 1 within one <paramref name="Epoch"/> (a new one each time the host starts), in the order the connection
/// receives them; a client that remembers the last pair it saw can resume with them. <paramref name="Kind"/> is
/// <see cref="EventKinds.Realtime"/>, whose <paramref name="Payload"/> is a <c>RealtimeEvent</c>, or
/// <see cref="EventKinds.Resync"/>, which has none and means "everything may have changed: read it all again". A resync
/// carries the sequence number after which live events continue, so a client sets its cursor to it. <see cref="EventKinds.State"/>
/// carries the host's current <c>HostState</c>; one follows every resync.
/// </summary>
public sealed record EventFrame(long Seq, string Epoch, string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Payload = default) : Frame
{
    public bool HasPayload => Payload.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
}

/// <summary>The kinds of <see cref="EventFrame"/>. A kind a client does not know is ignored, so a newer host may add more.</summary>
public static class EventKinds
{
    public const string Realtime = "realtime";
    public const string Resync = "resync";
    /// <summary>The host's pushed state (<c>HostState</c>): what the client's synchronous members answer from (SWITCHBOARD.md 2.6b).</summary>
    public const string State = "state";
    /// <summary>
    /// A fact about an account that no state shows (<see cref="AccountSignal"/>: the phone's SIMs changed, the default SMS app changed, the
    /// conversations were renumbered, Instagram stopped reconnecting). It sits in the host's sequence like any event, so a client that
    /// resumes is given the ones it missed (SWITCHBOARD.md 2.6c).
    /// </summary>
    public const string Account = "account";
    /// <summary>
    /// What a running mutation reports to the connection that made it (<see cref="ProgressPayload"/>: the pairing emoji of a Google sign-in).
    /// Not part of the sequence and not for resuming: it has <c>seq</c> 0, goes to that one connection only, and arrives before the
    /// answer to the call it belongs to.
    /// </summary>
    public const string Progress = "progress";
    /// <summary>
    /// Who is typing in one conversation (<c>TypingUpdate</c>, a snapshot of that conversation), sent to a connection that asked for typing
    /// when it subscribed. Ephemeral: it has <c>seq</c> 0, is not in the host's sequence or ring, is never replayed to a client that
    /// resumes, and a slow client sees only the newest snapshot of each conversation (SWITCHBOARD.md 2.6d).
    /// </summary>
    public const string Typing = "typing";
    /// <summary>
    /// A provider caught up on messages after connecting (<see cref="NativeBeeper.Core.Services.CaughtUpNotice"/>: the conversation, the
    /// messages' ids and whether their own events follow), so a client can prepare their pictures before the conversation opens
    /// (SWITCHBOARD.md 3.0b). It sits in the host's sequence like any event, and a client that does not know the kind ignores it.
    /// </summary>
    public const string CaughtUp = "caughtUp";
    /// <summary>
    /// One commit to the host's shared state (<see cref="SharedChange"/>, SWITCHBOARD.md stage 6; protocol version 2). It sits in the host's sequence like any
    /// event, so a client that resumes is given the commits it missed, and one that was handed a resync reads them with <c>state.sync</c> instead. A client
    /// applies it to its replica as the frame is read; it never reaches the application as an event.
    /// </summary>
    public const string Shared = "shared";
    /// <summary>
    /// One change to the server's whole copy of a conversation (<see cref="HistoryChange"/>, SWITCHBOARD.md stage 8; protocol version 24), carrying the changed
    /// messages themselves. It sits in the host's sequence like any event, just after the realtime event that caused it, so a client that resumes is given the ones
    /// it missed and one that was handed a resync reads them with <c>history.changes</c>. A connection below 24 is never sent one.
    /// </summary>
    public const string History = "history";
    /// <summary>
    /// One conversation's list row as the server keeps it now (<c>HistoryRowChange</c>, protocol 25, SWITCHBOARD.md stage 9): built again on its own when its copy
    /// changed (its last message) or an event named it, so a connected client's list follows without asking. A whole read of the list makes none: a client
    /// reads those changes with <c>history.feed</c>. A connection below 25 is never sent one.
    /// </summary>
    public const string Row = "row";
}

/// <summary>
/// The facts <see cref="EventKinds.Account"/> carries, by <paramref name="Name"/> (one of <see cref="AccountSignals"/>). <paramref name="Value"/>
/// is the new value where there is one (renumbered or not).
/// </summary>
public sealed record AccountSignal(string Name, bool? Value = null);

public static class AccountSignals
{
    public const string Sims = "sims";
    public const string DefaultSmsApp = "defaultSmsApp";
    public const string Renumbered = "renumbered";
    public const string ReconnectAbandoned = "reconnectAbandoned";
    /// <summary>The server's backup changed how it stands (protocol 26): a client reads <c>server.backup.status</c> again. <c>Value</c> says whether a good backup
    /// was made in the last two days, or backups are not on. Never sent to a connection below 26.</summary>
    public const string Backup = "backup";
    /// <summary>The server's pushes were turned on, or their files replaced (protocol 27): an open phone asks for <c>push.offer</c> again and signs up, rather than
    /// waiting for its next connection. Never sent to a connection below 27.</summary>
    public const string Push = "push";
    /// <summary>Contacts saved through the phone changed (protocol 29): a phone turned the setting on or off, a request was submitted (an open phone with the setting on
    /// takes it) or answered (a PC reads its results). Never sent to a connection below 29.</summary>
    public const string PhoneContacts = "phoneContacts";
}

/// <summary>A progress report from a running mutation: <paramref name="Text"/> is for the caller to show and is never logged.</summary>
public sealed record ProgressPayload(string ClientOpId, string Text);

public static class Frames
{
    public static string Serialize(Frame frame) => JsonSerializer.Serialize(frame, ProtocolJson.Options);
    public static byte[] SerializeToUtf8(Frame frame) => JsonSerializer.SerializeToUtf8Bytes(frame, ProtocolJson.Options);

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = ProtocolJson.Options.Encoder };

    /// <summary>
    /// The text of a successful <c>res</c> whose result is already JSON (<paramref name="resultJson"/>, written with
    /// <see cref="ProtocolJson.Options"/>). Byte for byte what <see cref="Serialize"/> writes for the same
    /// <see cref="ResponseFrame"/>, without building the result as a JSON tree first.
    /// </summary>
    public static string SerializeResult(string id, ReadOnlySpan<byte> resultJson)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(resultJson.Length + 48);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("t", "res");
            writer.WriteString("id", id);
            writer.WritePropertyName("result");
            writer.WriteRawValue(resultJson, skipInputValidation: true);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static bool TryParse(string json, [NotNullWhen(true)] out Frame? frame, [NotNullWhen(false)] out ProtocolError? error) =>
        TryParse(System.Text.Encoding.UTF8.GetBytes(json), out frame, out error);

    /// <summary>
    /// Reads one frame. A frame that is not JSON, has no or an unknown <c>t</c>, or lacks what its kind needs yields
    /// false and an <c>invalid_request</c> error the transport can answer with; nothing in it is echoed back, and
    /// nothing is thrown.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> utf8, [NotNullWhen(true)] out Frame? frame, [NotNullWhen(false)] out ProtocolError? error)
    {
        frame = null;
        error = null;
        try { frame = JsonSerializer.Deserialize<Frame>(utf8, ProtocolJson.Options); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }
        if (frame is null)
        {
            error = Invalid("The frame is not a known message.");
            return false;
        }
        var problem = frame switch
        {
            HelloFrame hello => string.IsNullOrEmpty(hello.App) ? "A hello needs an app version." : null,
            RequestFrame request => string.IsNullOrEmpty(request.Id) || string.IsNullOrEmpty(request.Method) ? "A request needs an id and a method."
                : request.HasParams && request.Params.ValueKind is not JsonValueKind.Object ? "Params must be an object." : null,
            ResponseFrame response => string.IsNullOrEmpty(response.Id) ? "A response needs an id."
                : response.HasResult == (response.Error is not null) ? "A response carries exactly one of result and error." : null,
            CancelFrame cancel => string.IsNullOrEmpty(cancel.Id) ? "A cancel needs an id." : null,
            EventFrame ev => ev.Seq < 0 || string.IsNullOrEmpty(ev.Epoch) || string.IsNullOrEmpty(ev.Kind) ? "An event needs a sequence number, an epoch and a kind." : null,
            _ => "The frame is not a known message.",
        };
        if (problem is null) return true;
        frame = null;
        error = Invalid(problem);
        return false;
    }

    private static ProtocolError Invalid(string message) => new(ProtocolErrorCodes.InvalidRequest, message);
}
