namespace MessageLink.Core.Services;

/// <summary>
/// What the window, the chat console and Settings show about the host's half of the running app: its helpers and the sizes of
/// what its backends hold (<c>host.diagnostics</c>). Counts, sizes, times and fixed words only; never a name,
/// a number, text or an id of a person or a conversation. A process's own memory is not in it: each process reports itself, and the
/// client keeps its own <c>memory</c> line.
/// </summary>
public interface IServerDiagnostics
{
    Task<ServerDiagnostics> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IServerDiagnostics"/> answers: one record, read as a whole.</summary>
/// <param name="Helpers">Each service's own helper, by scope, in scope order; a scope that never started one is not listed.</param>
/// <param name="Holdings">The long-lived caches of the server's backends, in the order the memory line prints them.</param>
public sealed record ServerDiagnostics(IReadOnlyList<ServerHelper> Helpers, IReadOnlyList<ServerHolding> Holdings);

/// <summary>
/// A service's helper process as the server sees it: the newest one for the scope, how many the scope has started this session, how
/// the last one ended, and whether the newest is running. <paramref name="Scope"/> is the connection scope the server already pushes
/// in its state ("messenger:", "gvoice:&lt;key&gt;:", the key being a one-way tag). The last three describe the helper's process
/// while it runs and are absent when it does not, or when the server cannot read them; the server reads them because the process is its.
/// </summary>
public sealed record ServerHelper(string Scope, DateTimeOffset StartedAt, int Starts, ServerHelperExit? LastExit, bool Running,
    int? ProcessId = null, long? WorkingSetBytes = null, long? CpuMilliseconds = null);

/// <summary><paramref name="How"/> is a fixed word (gone, eof, killed); <paramref name="Code"/> the exit code as the helper's process gave it.</summary>
public sealed record ServerHelperExit(string How, string Code, DateTimeOffset At);

/// <summary>One cache of the server, by a fixed name (google_messages, google_archive, google_media, messenger_kept), with its size where its holder knows it.</summary>
public sealed record ServerHolding(string Name, long Count, long? Bytes = null);
