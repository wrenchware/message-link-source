using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// The host's half of what the window, the console and Settings show about the running app (SWITCHBOARD.md 3.0b). A read: it changes
// nothing and is not journaled. The answer is counts, sizes, times and fixed words (IHostDiagnostics).
public static partial class ProtocolMethods
{
    /// <summary>The host's helpers and the sizes of what its backends hold. A client asks about once a second while the console's /top is open.</summary>
    public const string HostDiagnosticsRead = "host.diagnostics";

    private static Entry[] DiagnosticsEntries() =>
    [
        new(HostDiagnosticsRead, "IHostDiagnostics.ReadAsync", null, typeof(HostDiagnostics), ProtocolVersion.Original),
    ];
}
