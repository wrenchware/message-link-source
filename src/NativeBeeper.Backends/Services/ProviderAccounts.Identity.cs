using System.Security.Cryptography;
using System.Text;
using NativeBeeper.Core.Backend;

namespace NativeBeeper.App.Services;

internal sealed partial class ProviderAccounts
{
    internal const string UnknownGoogleIdentity = "The existing Google Messages pairing has no stable account identity. Adding another account is unavailable with this pairing.";
    internal static string Identity(string service, string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(service + ":" + value)));

    // Persist the binding before opening a device. A removed slot's identity remains reserved: restoring a backup
    // or reconnecting it must never attach its old drafts and sends to a different person.
    private async Task BindAsync(string? key, string service, string? identity, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (identity is null)
            {
                if (key is null) return; // Preserve the existing account's older session formats.
                throw new BackendException(BackendFailure.Unsupported, "The service did not provide a stable account identity.");
            }
            var account = key is null ? null : ByKey(key, service);
            if (account?.Registration.Identity is { } kept && kept != identity)
                throw new BackendException(BackendFailure.AuthenticationRequired, "Sign in with the same account. To use another account, choose Add account.");
            var legacy = service switch
            {
                "google" => await google.SavedIdentityAsync(),
                "messenger" => await messenger.SavedIdentityAsync(),
                _ => await instagram.SavedIdentityAsync()
            };
            var legacySaved = service switch { "google" => google.Files.SessionSaved, "messenger" => messenger.SessionSaved, _ => instagram.HasSavedSession };
            if (key is not null && legacySaved && legacy is null)
                throw new BackendException(BackendFailure.Unsupported, service == "google"
                    ? UnknownGoogleIdentity
                    : "Reconnect the existing account before adding another account.");
            if (key is null && legacy is not null && legacy != identity)
                throw new BackendException(BackendFailure.AuthenticationRequired, "Sign in with the same account. To use another account, choose Add account.");
            if (key is not null && legacy == identity || accounts.Any(a => a.Registration.Service == service && a.Registration.Key != key
                && !a.Registration.Removed && a.Registration.Identity == identity))
                throw new BackendException(BackendFailure.AuthenticationRequired, "This account is already added. Use its Sign in again option.");
            if (account is not null && account.Registration.Identity is null)
            {
                var next = accounts.Select(a => a.Registration.Key == key ? a with { Registration = a.Registration with { Identity = identity } } : a).ToArray();
                await SaveAsync(next, token);
                Volatile.Write(ref accounts, next);
            }
        }
        finally { gate.Release(); }
    }
}
