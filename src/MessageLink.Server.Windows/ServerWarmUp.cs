using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using MessageLink.Protocol;
using MessageLink.WindowsPlatform;

namespace MessageLink.Server.Windows;

/// <summary>
/// Work the start path will need and that touches nothing of the data root, begun on the pool while the shell makes its window and the composition
/// loads its assemblies, so that its first use finds it done (SWITCHBOARD.md 3.1d). Measured on a scratch root (Debug, warm), the first state's
/// serialization was 33 of its 42 ms, all of it the JSON library building its metadata for <see cref="HostState"/>, and the first use of the state
/// protector (WinRT DPAPI) was most of the client token's 19 ms. Nothing here changes what the start does or in what order: a use that comes before
/// one of these has finished meets the same work in progress, or does it itself, exactly as it would have.
/// </summary>
internal static class ServerWarmUp
{
    private sealed class Warm;
    private sealed class WarmDependent(Warm warm) { public Warm Warm { get; } = warm; }

    /// <summary>Begins the warm-up; never throws and nothing waits for it. Call once, after the server has taken its lease.</summary>
    public static void Start()
    {
        // The metadata the first state is serialized with (StatePublisher compares states as JSON), built the way the first serialization would.
        Task.Run(() =>
        {
            try { ProtocolJson.Options.GetTypeInfo(typeof(HostState)); }
            catch (Exception) { /* the real use will meet it again and say so */ }
        });
        // The container's own code (call sites, validation) is compiled on first use; a throwaway one, built with the options Compose gives its own.
        Task.Run(() =>
        {
            try
            {
                var collection = new ServiceCollection();
                collection.AddSingleton<Warm>();
                collection.AddSingleton(provider => new WarmDependent(provider.GetRequiredService<Warm>()));
                using var warm = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
                warm.GetRequiredService<WarmDependent>();
            }
            catch (Exception) { }
        });
        // DPAPI's first call loads its libraries and compiles its path; a one-byte value protected and thrown away, which is never written or read back.
        Task.Run(async () =>
        {
            try { await new WindowsStateProtector().ProtectAsync(new byte[1]).ConfigureAwait(false); }
            catch (Exception) { }
        });
    }
}
