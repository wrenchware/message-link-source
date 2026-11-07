using NativeBeeper.Core.Messaging;

namespace NativeBeeper.App.Services;

// A slot is chosen before constructing a backend and never changes during its lifetime. No process-wide path switch.
internal sealed class ProviderAccountSlot
{
    public static ProviderAccountSlot Legacy { get; } = new(null, null);
    public AccountNamespace Ids { get; }
    public string Root { get; }
    public string? Key => Ids.Key;
    public ProviderAccountSlot(string? key, string? root)
    {
        Ids = new(key);
        Root = Path.GetFullPath(root ?? AppPaths.DataRoot);
    }
    public string PathFor(params string[] parts) => Path.Combine([Root, .. parts]);
    public IReadOnlyDictionary<string, string> HelperEnvironment
    {
        get
        {
            var environment = new Dictionary<string, string> { ["NATIVEBEEPER_DATA_ROOT"] = Root };
            if (Key is not null && Environment.GetEnvironmentVariable("MESSAGES_HELPER_PRIVATE_ROOT") is { Length: > 0 } privateRoot)
                environment["MESSAGES_HELPER_PRIVATE_ROOT"] = Path.Combine(privateRoot, "slots", Key);
            return environment;
        }
    }
}
