using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// The host's half of what the window, the console and Settings show about the running app. A read: it changes
// nothing and is not journaled. The answer is counts, sizes, times and fixed words (IServerDiagnostics).
public static partial class ProtocolMethods
{
    /// <summary>The host's helpers and the sizes of what its backends hold. A client asks about once a second while the console's /top is open.</summary>
    public const string ServerDiagnosticsRead = "host.diagnostics";

    private static Entry[] DiagnosticsEntries() =>
    [
        new(ServerDiagnosticsRead, "IServerDiagnostics.ReadAsync", null, typeof(ServerDiagnostics), ProtocolVersion.Original),
    ];
}
