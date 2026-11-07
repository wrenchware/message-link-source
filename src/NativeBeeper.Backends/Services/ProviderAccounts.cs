using System.Text.Json;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Storage;

namespace NativeBeeper.App.Services;

// The original accounts remain at their original paths. Added slots are never reused, including after removal:
// a draft, scheduled send or cached conversation naming a removed slot must stay unavailable forever.
internal sealed partial class ProviderAccounts(IStateProtector protector, IAppPresence presence, IGoogleBrowser browser,
    IPrivateStateStore privateStore, MediaDiskCache mediaCache, GoogleMessagesBackend google, GoogleMediaBackend googleMedia,
    MessengerBackend messenger, InstagramBackend instagram, string? dataRoot = null) : IAsyncDisposable, IDisposable
{
    internal sealed record Registration(string Key, string Service, string? Identity = null, bool Removed = false, bool Pending = false);
    internal sealed record Account(Registration Registration, ProviderAccountSlot Slot, GoogleMessagesBackend? Google = null,
        GoogleMediaBackend? GoogleMedia = null, GoogleSignInKeeper? Keeper = null, MessengerBackend? Messenger = null,
        InstagramBackend? Instagram = null, MessageArchive? Archive = null)
    {
        internal CancellationTokenSource Lifetime { get; } = new();
    }

    private readonly SemaphoreSlim gate = new(1);
    private Account[] accounts = [];
    private bool loaded;
    internal event Action? Changed;
    internal bool RequiresAccountProtocol => Volatile.Read(ref accounts).Any(a => !a.Registration.Pending);
    internal IReadOnlyList<Account> Available => Volatile.Read(ref accounts).Where(a => !a.Registration.Removed).ToArray();
    internal IReadOnlyList<Account> Added => Available.Where(a => !a.Registration.Pending).ToArray();
    private string Root => dataRoot ?? AppPaths.DataRoot;
    private string RegistryPath => Path.Combine(Root, "ProviderAccounts", "registry.protected");

    internal async Task LoadAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            if (loaded) return;
            Registration[] rows = [];
            if (File.Exists(RegistryPath))
            {
                var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(RegistryPath, token));
                try { rows = JsonSerializer.Deserialize<Registration[]>(plain) ?? throw new InvalidDataException("Invalid account registry."); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
            }
            if (rows.Length > 256 || rows.Any(row => !AccountNamespace.ValidKey(row.Key) || !Supported(row.Service) || row.Identity is { } id && (id.Length != 64 || !id.All(char.IsAsciiHexDigitLower))) || rows.Select(r => r.Key).Distinct().Count() != rows.Length)
                throw new InvalidDataException("Invalid account registry.");
            // Recover a saved session even if the process ended before publishing it. An abandoned attempt has never reached a client.
            var recovered = rows.Where(r => !r.Pending || SessionSaved(r)).Select(r => r with { Pending = false }).ToArray();
            accounts = recovered.Select(Make).ToArray();
            if (!rows.SequenceEqual(recovered)) await SaveAsync(accounts, token);
            google.ValidateAccount = (id, ct) => BindAsync(null, "google", id, ct);
            messenger.ValidateAccount = (id, ct) => BindAsync(null, "messenger", id, ct);
            instagram.ValidateAccount = (id, ct) => BindAsync(null, "instagram", id, ct);
            loaded = true;
        }
        finally { gate.Release(); }
    }

    private static bool Supported(string service) => service is "google" or "messenger" or "instagram";
    internal async Task<Account?> SelectForSignInAsync(string service, string? key, CancellationToken token)
    {
        if (!Supported(service) || string.IsNullOrEmpty(key)) throw new ArgumentException("Choose an account on the server page.");
        await LoadAsync(token);
        if (key == "new" && service == "google" && google.Files.SessionSaved && await google.SavedIdentityAsync() is null)
            throw new BackendException(BackendFailure.Unsupported, UnknownGoogleIdentity);
        return key switch { "legacy" => null, "new" => await AddAsync(service, token, pending: true), _ => ByKey(key, service) };
    }
    internal async Task<Account> AddAsync(string service, CancellationToken token = default, bool pending = false)
    {
        if (!Supported(service)) throw new NotSupportedException();
        await LoadAsync(token);
        Account made;
        await gate.WaitAsync(token);
        try
        {
            if (accounts.Length >= 256) throw new BackendException(BackendFailure.Unsupported, "The account limit has been reached.");
            var row = new Registration(Guid.NewGuid().ToString("N"), service, Pending: pending);
            made = Make(row);
            var next = accounts.Append(made).ToArray();
            await SaveAsync(next, token);
            Volatile.Write(ref accounts, next);
            }
        finally { gate.Release(); }
        Changed?.Invoke();
        return made;
    }

    private bool SessionSaved(Registration row)
    {
        var root = Path.Combine(Root, "ProviderAccounts", row.Key);
        return row.Service switch
        {
            "google" => File.Exists(Path.Combine(root, "GoogleMessagesSpike", "session.protected")),
            "messenger" => File.Exists(Path.Combine(root, "Messenger", "session.protected")),
            _ => InstagramSessionStore.Exists(Path.Combine(root, "Instagram"))
        };
    }

    // End of both successful and cancelled attempts. Binding alone is not a session and must not disconnect older clients.
    internal async Task FinishSignInAsync(string key)
    {
        Account? abandoned = null, published = null;
        await gate.WaitAsync();
        try
        {
            var account = Available.FirstOrDefault(a => a.Registration.Key == key);
            if (account is null || !account.Registration.Pending) return;
            Account[] next;
            if (SessionSaved(account.Registration))
            {
                published = account with { Registration = account.Registration with { Pending = false } };
                next = accounts.Select(a => a == account ? published : a).ToArray();
            }
            else { abandoned = account; next = accounts.Where(a => a != account).ToArray(); }
            await SaveAsync(next, CancellationToken.None);
            Volatile.Write(ref accounts, next);
        }
        finally { gate.Release(); }
        if (abandoned is not null) await CloseAsync(abandoned);
        if (published is not null) BeginAccount(published);
        Changed?.Invoke();
    }

    private Account Make(Registration row)
    {
        var slot = new ProviderAccountSlot(row.Key, Path.Combine(Root, "ProviderAccounts", row.Key));
        if (row.Removed) return new(row, slot);
        Account made = row.Service switch
        {
            "google" => MakeGoogle(row, slot),
            "messenger" => new(row, slot, Messenger: new(protector, slot)),
            "instagram" => new(row, slot, Instagram: new(protector, presence, accountSlot: slot)),
            _ => throw new InvalidDataException("Unknown account service.")
        };
        if (made.Google is { } g) g.ValidateAccount = (id, ct) => BindAsync(row.Key, row.Service, id, ct);
        if (made.Messenger is { } m) { m.ValidateAccount = (id, ct) => BindAsync(row.Key, row.Service, id, ct); m.MayConnect = () => Available.Any(a => a.Registration.Key == row.Key); }
        if (made.Instagram is { } i) { i.ValidateAccount = (id, ct) => BindAsync(row.Key, row.Service, id, ct); i.MayStart = () => Available.Any(a => a.Registration.Key == row.Key); }
        if (made.Messenger is { } changedMessenger) changedMessenger.ChatsChanged += (_, _) => NotifyChanged();
        if (made.Instagram is { } changedInstagram) changedInstagram.ChatsChanged += (_, _) => NotifyChanged();
        return made;
    }
    private Account MakeGoogle(Registration row, ProviderAccountSlot slot)
    {
        var tokens = new GoogleSessionProfile(protector, slot);
        var archive = new MessageArchive(slot.PathFor("archive.db"), protector);
        var backend = new GoogleMessagesBackend(new GoogleArchive(archive), tokens, new PrivateState(privateStore, tokens), protector, slot);
        return new(row, slot, backend, new GoogleMediaBackend(backend, tokens, mediaCache), new GoogleSignInKeeper(backend, browser, tokens), Archive: archive);
    }
    private async Task SaveAsync(Account[] next, CancellationToken token)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(next.Select(a => a.Registration).ToArray());
        try
        {
            var bytes = await protector.ProtectAsync(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
            await DurableFile.WriteAsync(RegistryPath + ".tmp", bytes, token);
            File.Move(RegistryPath + ".tmp", RegistryPath, true);
            if (next.Length == 0) File.Delete(RegistryPath);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }

    internal Account ByKey(string key, string? service = null) => Available.FirstOrDefault(a => a.Registration.Key == key && (service is null || a.Registration.Service == service))
        ?? throw new BackendException(BackendFailure.Unavailable, "This account is no longer connected. Open the server page to manage accounts.");
    private Account Published(string key, string service)
    {
        var account = ByKey(key, service);
        if (account.Registration.Pending) throw new BackendException(BackendFailure.Unavailable, "This account has not finished signing in.");
        return account;
    }
    internal GoogleMessagesBackend Google(string id) => AccountNamespace.KeyOf(id) is { } key ? Published(key, "google").Google! : google;
    internal GoogleMediaBackend GoogleMedia(string id) => AccountNamespace.KeyOf(id) is { } key ? Published(key, "google").GoogleMedia! : googleMedia;
    internal MessengerBackend Messenger(string id) => AccountNamespace.KeyOf(id) is { } key ? Published(key, "messenger").Messenger! : messenger;
    internal InstagramBackend Instagram(string id) => AccountNamespace.KeyOf(id) is { } key ? Published(key, "instagram").Instagram! : instagram;
    internal IEnumerable<GoogleMessagesBackend> Googles => new[] { google }.Concat(Added.Where(a => a.Google is not null).Select(a => a.Google!));
    internal IEnumerable<MessengerBackend> Messengers => new[] { messenger }.Concat(Added.Where(a => a.Messenger is not null).Select(a => a.Messenger!));
    internal IEnumerable<InstagramBackend> Instagrams => new[] { instagram }.Concat(Added.Where(a => a.Instagram is not null).Select(a => a.Instagram!));

    private readonly HashSet<string> authenticating = [];
    internal async Task<IDisposable> HoldSignInAsync(string key, string service, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            _ = ByKey(key, service);
            if (!authenticating.Add(key)) throw new BackendException(BackendFailure.Unavailable, "Sign-in is already open for this account.");
            return new SignInLease(this, key);
        }
        finally { gate.Release(); }
    }
    private sealed class SignInLease(ProviderAccounts owner, string key) : IDisposable
    {
        private int ended;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) != 0) return;
            owner.gate.Wait();
            try { owner.authenticating.Remove(key); } finally { owner.gate.Release(); }
        }
    }

    internal async Task RemoveAsync(string key, CancellationToken token = default)
    {
        Account account;
        await gate.WaitAsync(token);
        try
        {
            account = ByKey(key);
            if (authenticating.Contains(key)) throw new BackendException(BackendFailure.Unavailable, "Close sign-in before removing this account.");
            // Fence routing durably before closing the connection. An in-flight mutation is never resent.
            var next = accounts.Select(a => a == account ? a with { Registration = a.Registration with { Removed = true }, Google = null, GoogleMedia = null, Keeper = null, Messenger = null, Instagram = null, Archive = null } : a).ToArray();
            await SaveAsync(next, token);
            Volatile.Write(ref accounts, next);
        }
        finally { gate.Release(); }
        try
        {
            if (account.Messenger is { } m) await m.SignOutAsync();
            if (account.Instagram is { } i) await i.SignOutAsync();
            await CloseAsync(account);
            if (account.Google is { } g && File.Exists(g.Files.SessionFile)) File.Delete(g.Files.SessionFile);
            }
        finally { Changed?.Invoke(); }
    }
    private static async Task CloseAsync(Account account)
    {
        await account.Lifetime.CancelAsync();
        account.Keeper?.Dispose();
        account.Google?.Dispose();
        if (account.Messenger is { } messenger) await messenger.DisposeAsync();
        if (account.Instagram is { } instagram) await instagram.DisposeAsync();
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        foreach (var account in accounts) await CloseAsync(account);
    }
}
