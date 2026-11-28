using MessageLink.App.Services;
using MessageLink.Protocol;

namespace MessageLink.Server.Linux;

/// <summary>
/// The logs the Linux server gives its owner (<c>host.logs</c>, protocol 18), by name. Each is written under the codebase's log rules (steps, counts, states,
/// fixed words and one-way tags); nothing else on the data or keys volume is ever named here. The providers' logs are <c>server.log</c> on this server
/// (Program.cs sets <c>Diagnostics.ProviderLogFile</c>), and the Messenger helper writes its own in its private root on the keys volume (LinuxServerKeys.HelperFolder).
/// </summary>
internal static class LinuxServerLogs
{
    public const string ProviderLog = "server.log";

    public static IReadOnlyDictionary<string, string> Paths(string dataRoot, string helperFolder) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ServerLogNames.Server] = Path.Combine(dataRoot, ProviderLog),
        [ServerLogNames.ServerPrevious] = LogRotation.PreviousPath(Path.Combine(dataRoot, ProviderLog)),
        [ServerLogNames.GoogleMessages] = Path.Combine(dataRoot, "GoogleMessages", ProviderLog),
        [ServerLogNames.GoogleSignInHistory] = Path.Combine(dataRoot, "GoogleMessages", "sign-in-history." + ProviderLog),
        [ServerLogNames.GoogleVoice] = Path.Combine(dataRoot, "GoogleVoice", ProviderLog),
        [ServerLogNames.Instagram] = Path.Combine(dataRoot, "Instagram", ProviderLog),
        [ServerLogNames.InstagramPrevious] = LogRotation.PreviousPath(Path.Combine(dataRoot, "Instagram", ProviderLog)),
        [ServerLogNames.MessengerHelper] = Path.Combine(helperFolder, "helper.log"),
        [ServerLogNames.MessengerHelperPrevious] = LogRotation.PreviousPath(Path.Combine(helperFolder, "helper.log")),
        [ServerLogNames.Messenger] = Path.Combine(dataRoot, "Messenger", ProviderLog),
        [ServerLogNames.GoogleMessagesPrevious] = LogRotation.PreviousPath(Path.Combine(dataRoot, "GoogleMessages", ProviderLog)),
        [ServerLogNames.GoogleVoicePrevious] = LogRotation.PreviousPath(Path.Combine(dataRoot, "GoogleVoice", ProviderLog)),
    };
}
