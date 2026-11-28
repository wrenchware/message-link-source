using MessageLink.Core.Services;

namespace MessageLink.Protocol;

// The owner's request that a host it started stop (SWITCHBOARD.md 3.2c). Not journaled: it is not a send, a repeat only asks again, and the
// answer is a word the client acts on (stopping: wait for the connection to end; busy: ask again shortly; refused: this host is not stopped
// this way). It takes nothing, so a client cannot name a reason or a budget; the host chooses both.
public static partial class ProtocolMethods
{
    public const string HostShutdown = "host.shutdown";

    private static Entry[] ShutdownEntries() =>
    [
        new(HostShutdown, "IHostShutdown.RequestAsync", null, typeof(HostShutdownResult), ProtocolVersion.Original),
    ];
}
