using System.Text.Json;
using System.Text.Json.Serialization;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

/// <summary>An error as it crosses the wire. <paramref name="Message"/> is safe to show; see <see cref="ProtocolErrors.From"/>.</summary>
/// <param name="Transient">The provider is putting this right by itself (<see cref="BackendException.Transient"/>).</param>
/// <param name="OutcomeUnknown">A mutation may or may not have happened; it must be reported, never resent.</param>
/// <param name="Done">The operation went through and only what followed it failed (<see cref="SendException.Done"/>); never to be offered again.</param>
public sealed record ProtocolError(string Code, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Transient = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool OutcomeUnknown = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Done = false);

public static class ProtocolErrorCodes
{
    // BackendException.Failure, snake_case of the enum member.
    public const string Unavailable = "unavailable";
    public const string AuthenticationRequired = "authentication_required";
    public const string Forbidden = "forbidden";
    public const string Unsupported = "unsupported";
    public const string RateLimited = "rate_limited";
    public const string InvalidResponse = "invalid_response";
    public const string ServerError = "server_error";
    public const string NotReady = "not_ready";
    // SendException and FirstMessageRequiredException.
    public const string SendFailed = "send_failed";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string FirstMessageRequired = "first_message_required";
    public const string NotSupported = "not_supported";
    public const string Cancelled = "cancelled";
    /// <summary>A connect or sign-in refused with a fixed code word, which the message carries (and nothing else may): see <c>ProtocolDispatcher.IsCodeWord</c>.</summary>
    public const string AccountFailed = "account_failed";
    // The protocol itself.
    public const string InvalidRequest = "invalid_request";
    public const string UnknownMethod = "unknown_method";
    public const string ProtocolUnsupported = "protocol_unsupported";
    public const string Internal = "internal";
}

/// <summary>Code words an <see cref="ProtocolErrorCodes.AccountFailed"/> error carries that come from the host itself rather than from a provider.</summary>
public static class AccountFailureWords
{
    /// <summary>This host does not run the provider at all (the Linux server and Google Messages, Google Voice or Instagram): nothing was tried, and trying again will not help.</summary>
    public const string NotOnThisHost = "not_on_this_host";

    /// <summary>The account moved away with a move back, and this host holds it (a hold) until a new move or an offline clear: nothing was tried.</summary>
    public const string AccountMovedAway = "account_moved_away";
}

/// <summary>A protocol-level failure raised by <see cref="ProtocolErrors.ToException"/>.</summary>
public sealed class ProtocolException(ProtocolError error) : Exception(error.Message)
{
    public ProtocolError Error { get; } = error;
}

public static class ProtocolErrors
{
    private static readonly Dictionary<string, BackendFailure> Failures =
        Enum.GetValues<BackendFailure>().ToDictionary(Code, StringComparer.Ordinal);

    // A new BackendFailure member gets its code from its name, so it crosses the wire without a change here.
    private static string Code(BackendFailure failure) => JsonNamingPolicy.SnakeCaseLower.ConvertName(failure.ToString());

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        ProtocolErrorCodes.SendFailed, ProtocolErrorCodes.OutcomeUnknown, ProtocolErrorCodes.FirstMessageRequired, ProtocolErrorCodes.NotSupported,
        ProtocolErrorCodes.Cancelled, ProtocolErrorCodes.AccountFailed, ProtocolErrorCodes.InvalidRequest, ProtocolErrorCodes.UnknownMethod, ProtocolErrorCodes.ProtocolUnsupported,
        ProtocolErrorCodes.Internal,
    };

    /// <summary>Whether this build knows <paramref name="code"/>. A code it does not know (from a newer host) says nothing certain about a mutation.</summary>
    public static bool IsKnown(string code) => Failures.ContainsKey(code) || Known.Contains(code);

    /// <summary>
    /// The error to put on the wire for <paramref name="exception"/>. Only the app's own exception types carry their
    /// messages (they are written to be safe to display); every other exception, the framework's included, becomes a
    /// fixed sentence, because its message may name an account, a path or a response body.
    /// </summary>
    public static ProtocolError From(Exception exception) => exception switch
    {
        ProtocolException protocol => protocol.Error,
        BackendException backend => new(Code(backend.Failure), backend.Message, backend.Transient),
        SendException send => new(send.OutcomeUnknown ? ProtocolErrorCodes.OutcomeUnknown : ProtocolErrorCodes.SendFailed, send.Message,
            OutcomeUnknown: send.OutcomeUnknown, Done: send.Done),
        FirstMessageRequiredException first => new(ProtocolErrorCodes.FirstMessageRequired, first.Message),
        NotSupportedException => new(ProtocolErrorCodes.NotSupported, "This is not supported."),
        OperationCanceledException => new(ProtocolErrorCodes.Cancelled, "The request was canceled."),
        _ => new(ProtocolErrorCodes.Internal, "Something went wrong on the server."),
    };

    /// <summary>
    /// The exception a caller of a port would have seen had it called the backend directly. An error that says its
    /// outcome is unknown always becomes an unknown-outcome <see cref="SendException"/>, whatever its code, because
    /// treating a mutation that may have happened as one that did not is the mistake that resends it. A code this build
    /// does not know becomes a <see cref="BackendFailure.ServerError"/>, so a newer host cannot crash an older client.
    /// </summary>
    public static Exception ToException(ProtocolError error)
    {
        if (error.OutcomeUnknown || error.Code == ProtocolErrorCodes.OutcomeUnknown) return new SendException(true, error.Message);
        if (Failures.TryGetValue(error.Code, out var failure)) return new BackendException(failure, error.Message) { Transient = error.Transient };
        return error.Code switch
        {
            ProtocolErrorCodes.SendFailed => new SendException(false, error.Message) { Done = error.Done },
            ProtocolErrorCodes.FirstMessageRequired => new FirstMessageRequiredException(error.Message),
            ProtocolErrorCodes.NotSupported => new NotSupportedException(error.Message),
            ProtocolErrorCodes.Cancelled => new OperationCanceledException(error.Message),
            // The message is a code word or nothing: a peer that sends anything else is not believed.
            ProtocolErrorCodes.AccountFailed => ProtocolDispatcher.IsCodeWord(error.Message) ? new InvalidOperationException(error.Message) : new ProtocolException(error),
            ProtocolErrorCodes.InvalidRequest or ProtocolErrorCodes.UnknownMethod or ProtocolErrorCodes.ProtocolUnsupported
                or ProtocolErrorCodes.Internal => new ProtocolException(error),
            _ => new BackendException(BackendFailure.ServerError, error.Message) { Transient = error.Transient },
        };
    }
}
