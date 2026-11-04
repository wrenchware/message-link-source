namespace NativeBeeper.Protocol;

// The server's own logs, read by its owner (protocol 18). A read: it changes nothing and is not journaled. The client names a log from a fixed list and a
// byte offset; it never names a path, and the host maps the name to its file itself (IHostLogs). The logs are the fixed-format ones every host writes
// (steps, counts, states, one-way tags), and any paired device may read them: it reads every conversation anyway.
public static partial class ProtocolMethods
{
    /// <summary>A chunk of one of the server's logs from an offset. The owner's Messages asks about once a minute while it is the server's client.</summary>
    public const string HostLogsRead = "host.logs";

    private static Entry[] HostLogsEntries() =>
    [
        new(HostLogsRead, "IHostLogs.ReadAsync", typeof(HostLogReadParams), typeof(HostLogChunk)),
    ];
}

/// <summary>The logs a host may give, by the names the wire uses. Anything else is answered as not there, whatever it looks like.</summary>
public static class HostLogNames
{
    public const string Server = "server", ServerPrevious = "server.previous", GoogleMessages = "google-messages", GoogleSignInHistory = "google-messages.sign-in-history",
        GoogleVoice = "google-voice", Instagram = "instagram", InstagramPrevious = "instagram.previous", MessengerHelper = "messenger-helper",
        MessengerHelperPrevious = "messenger-helper.previous", Messenger = "messenger", GoogleMessagesPrevious = "google-messages.previous", GoogleVoicePrevious = "google-voice.previous";

    public static IReadOnlyList<string> All { get; } =
        [Server, ServerPrevious, GoogleMessages, GoogleSignInHistory, GoogleVoice, Instagram, InstagramPrevious, MessengerHelper, MessengerHelperPrevious, Messenger,
            GoogleMessagesPrevious, GoogleVoicePrevious];

    public static bool IsKnown(string? name) => name is not null && All.Contains(name, StringComparer.Ordinal);
}

/// <summary>Which log, and the byte offset to read from (0 for the start).</summary>
public sealed record HostLogReadParams(string Name, long Offset = 0);

/// <summary>
/// A chunk of a log. <paramref name="Exists"/> false says only that there is no such log here (an unknown name, or a file not written): nothing else is.
/// <paramref name="Length"/> is the file's length now and <paramref name="Next"/> the offset to ask from next; <paramref name="Text"/> is the bytes between, as
/// UTF-8, cut after the last whole line (a line still being written waits for the next ask). <paramref name="Head"/> is a short digest of the file's first
/// <see cref="HeadBytes"/> bytes (or all of a shorter file): a new run's log starts with a new first line, so a file that got longer than the client's copy
/// while it was not asking is still told from the one it copied. A file shorter than the client's offset is a new one as well.
/// </summary>
public sealed record HostLogChunk(bool Exists, long Length, long Next, string Text, string? Head = null)
{
    public const int HeadBytes = 64;
    /// <summary>The most one answer carries.</summary>
    public const int MaxChunk = 256 * 1024;
    public static HostLogChunk Missing { get; } = new(false, 0, 0, "");
}

/// <summary>
/// The host's side of <c>host.logs</c>: the logs it keeps, by name. A host without one (the app's in-process host, the Windows server) answers not supported.
/// </summary>
public interface IHostLogs
{
    Task<HostLogChunk> ReadAsync(string name, long offset, CancellationToken cancellationToken = default);
}
