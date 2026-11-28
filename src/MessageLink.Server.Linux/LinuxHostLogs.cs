using MessageLink.App.Services;
using MessageLink.Protocol;

namespace MessageLink.Server.Linux;

/// <summary>
/// The logs the Linux host gives its owner (<c>host.logs</c>, protocol 18), by name. Each is written under the codebase's log rules (steps, counts, states,
/// fixed words and one-way tags); nothing else on the data or keys volume is ever named here. The providers' logs are <c>server.log</c> on this host
/// (Program.cs sets <c>Diagnostics.ProviderLogFile</c>), and the Messenger helper writes its own in its private root on the keys volume (HostKeys.HelperFolder).
/// </summary>
internal static class LinuxHostLogs
{
    public const string ProviderLog = "server.log";

    public static IReadOnlyDictionary<string, string> Paths(string dataRoot, string helperFolder) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [HostLogNames.Server] = Path.Combine(dataRoot, ProviderLog),
        [HostLogNames.ServerPrevious] = LogRotation.PreviousPath(Path.Combine(dataRoot, ProviderLog)),
        [HostLogNames.GoogleMessages] = Path.Combine(dataRoot, "GoogleMessages", ProviderLog),
        [HostLogNames.GoogleSignInHistory] = Path.Combine(dataRoot, "GoogleMessages", "sign-in-history." + ProviderLog),
        [HostLogNames.GoogleVoice] = Path.Combine(dataRoot, "GoogleVoice", ProviderLog),
        [HostLogNames.Instagram] = Path.Combine(dataRoot, "Instagram", ProviderLog),
        [HostLogNames.InstagramPrevious] = LogRotation.PreviousPath(Path.Combine(dataRoot, "Instagram", ProviderLog)),
        [HostLogNames.MessengerHelper] = Path.Combine(helperFolder, "helper.log"),
        [HostLogNames.MessengerHelperPrevious] = LogRotation.PreviousPath(Path.Combine(helperFolder, "helper.log")),
        [HostLogNames.Messenger] = Path.Combine(dataRoot, "Messenger", ProviderLog),
        [HostLogNames.GoogleMessagesPrevious] = LogRotation.PreviousPath(Path.Combine(dataRoot, "GoogleMessages", ProviderLog)),
        [HostLogNames.GoogleVoicePrevious] = LogRotation.PreviousPath(Path.Combine(dataRoot, "GoogleVoice", ProviderLog)),
    };
}
