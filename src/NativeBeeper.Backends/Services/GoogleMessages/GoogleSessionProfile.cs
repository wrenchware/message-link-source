using System.Security.Cryptography;
using System.Text.Json;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

// Supplies an isolated cache identity, not a Beeper bearer token. Never used in HTTP. Which identity: GoogleLocalProfile.
internal sealed class GoogleSessionProfile(IStateProtector protector, ProviderAccountSlot? accountSlot = null) : IAccessTokenStore
{
    internal ProviderAccountSlot Slot { get; } = accountSlot ?? ProviderAccountSlot.Legacy;
    internal GoogleAccountClock Clock { get; } = accountSlot is null || accountSlot.Key is null ? GoogleSignInClock.Default : new(accountSlot.Root);
    internal GoogleAccountCookies Cookies { get; } = accountSlot is null || accountSlot.Key is null ? GoogleSignInCookies.Default : new();
    internal NativeBeeper.App.GoogleAccountBrowserProfile BrowserProfile { get; } = accountSlot is null || accountSlot.Key is null ? NativeBeeper.App.GoogleMessagesBrowserProfile.Default : new(accountSlot.Root);
    internal string SessionFile => Slot.PathFor("GoogleMessagesSpike", "session.protected");
    internal bool SessionSaved => File.Exists(SessionFile);
    public static string SessionPath => NativeBeeper.App.Services.AppPaths.Combine("GoogleMessagesSpike", "session.protected");
    // Which device this account's local state is filed under, once a new pairing has given it another (KeepAsync).
    private string KeptPath => Slot.PathFor("GoogleMessagesSpike", "profile.protected");
    // Left the first time local state is filed under the unpaired identity, so a first pairing later knows to keep it there
    // (GoogleLocalProfile.Paired). Empty: that it exists is all it says.
    private string UnpairedUsedPath => Slot.PathFor("GoogleMessagesSpike", "unpaired-profile.marker");
    public static bool HasSavedSession => File.Exists(SessionPath);
    private Task<string?>? profile;
    // The last answer a read gave, served when a later read fails without anything having said the identity changed (Refresh): a
    // transient fault must not take the local key away from the pins and drafts filed under it.
    private sealed record Good(string? Key);
    private Good? good;
    // A sign-in changed what the key is derived from: nothing from before it is served, even when the next read fails.
    internal void Invalidate() { profile = null; good = null; }
    // The files the key is read from changed and it may follow them: read again, keeping the last answer for a read that fails.
    internal void Refresh() => profile = null;
    // The inputs of the key that can change while the app runs, as one comparable value: what a host that does not hear of every
    // sign-in (HostIdentityPort) watches to know the identity may have changed. The session file is not stamped by time, because the
    // backend rewrites it with every update; the device and account the key comes from change only with a sign-in, and
    // GoogleMessagesBackend.SignInAsync invalidates for that itself. Whether the file exists is stamped.
    internal static string IdentityStamp() => string.Join('|', HasSavedSession, Stamp(AppPaths.Combine("GoogleMessagesSpike", "profile.protected")), File.Exists(AppPaths.Combine("GoogleMessagesSpike", "unpaired-profile.marker")), ServiceSetup.OtherThanGoogle);
    private static string Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : "-";
    // Nothing paired and no other service signed in: no identity, and that answer is not kept, so the first sign-in of any
    // service files local state from then on however it happened (a new sign-in, a restore at launch, a backup put back),
    // without each of those having to say so. Only a few file checks, and nothing asks often while nothing is signed in.
    public async ValueTask<string?> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profile is null && !SessionSaved && (Slot.Key is not null || !ServiceSetup.OtherThanGoogle)) return null;
        var read = Reading();
        try
        {
            var key = await read.WaitAsync(cancellationToken);
            if (ReferenceEquals(profile, read)) good = new(key);
            return key;
        }
        // A failed read is not an answer and is never kept, so the next call reads again instead of failing until the files change.
        catch (Exception) when (read.IsFaulted)
        {
            _ = Interlocked.CompareExchange(ref profile, null, read);
            if (good is { } last) return last.Key;
            throw;
        }
    }
    private Task<string?> Reading() => profile ??= ReadAsync();
    // Google Messages was never paired here, but another service is signed in. Local state (pins, drafts, read progress)
    // still needs an identity; this one is fixed rather than tied to an account, and pairing Google Messages later keeps
    // it (GoogleLocalProfile.Paired).
    internal const string UnpairedProfile = GoogleLocalProfile.Unpaired;
    private async Task<string?> ReadAsync()
    {
        if (!File.Exists(SessionFile))
        {
            var unpaired = GoogleLocalProfile.WithoutPairing(Slot.Key is null && ServiceSetup.OtherThanGoogle);
            if (unpaired is not null) NoteUnpairedUsed();
            AppLog.Write($"local profile identity={(unpaired is null ? "none" : "unpaired")}");
            return unpaired;
        }
        var bytes = await protector.UnprotectAsync(await ReadSharedAsync(SessionFile));
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var (device, account) = GooglePairingIdentity.Identity(json.RootElement);
            if (device is null) return null;
            var (chosen, adopt) = GoogleLocalProfile.Paired(device, account, await ReadKeptAsync(protector), File.Exists(UnpairedUsedPath));
            if (adopt) await AdoptUnpairedAsync(account!);
            else if (chosen == UnpairedProfile) AppLog.Write("local profile identity=unpaired paired=true");
            return chosen;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    // Best effort: without the marker a first pairing files local state afresh, which is how it always was.
    private void NoteUnpairedUsed()
    {
        try
        {
            if (File.Exists(UnpairedUsedPath)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(UnpairedUsedPath)!);
            File.WriteAllBytes(UnpairedUsedPath, []);
        }
        catch (Exception failure) { AppLog.Write($"local profile marker=failed error={failure.GetType().Name}"); }
    }

    // This account's first pairing found state filed under the unpaired identity: recorded as kept, so the next new pairing
    // of the same account keeps it too (KeepAsync finds this account's record and leaves it). If the record cannot be
    // written the identity is still the unpaired one, and the next launch tries again.
    private async Task AdoptUnpairedAsync(string account)
    {
        try
        {
            var kept = await KeepAccountAsync(protector, UnpairedProfile, account, CancellationToken.None);
            if (kept) { try { File.Delete(UnpairedUsedPath); } catch (Exception) { } }
            AppLog.Write($"local profile identity=unpaired adopted=first_pairing kept={kept}");
        }
        catch (Exception failure) { AppLog.Write($"local profile identity=unpaired adopted=first_pairing kept=failed error={failure.GetType().Name}"); }
    }

    // Records the device this account's local state lives under, unless one is already recorded for this account: after
    // a second new pairing the state is still under the first.
    internal async Task<bool> KeepAccountAsync(IStateProtector protector, string device, string account, CancellationToken token)
    {
        if (await ReadKeptAsync(protector) is { } existing && existing.Account == account) return true;
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Kept(device, account));
        try
        {
            var sealedBytes = await protector.ProtectAsync(plain);
            // Flushed before it replaces the old one, as the session itself is: a crash here would file local state afresh.
            await NativeBeeper.Storage.DurableFile.WriteAsync(KeptPath + ".tmp", sealedBytes, token);
            File.Move(KeptPath + ".tmp", KeptPath, true);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<(string Device, string Account)?> ReadKeptAsync(IStateProtector protector)
    {
        if (!File.Exists(KeptPath)) return null;
        byte[] sealedBytes;
        // A file that cannot be opened (the sign-in is replacing it) says nothing about what it holds, so it is not "none": that would
        // file local state under the paired device for as long as the answer is kept. The read fails instead, and is made again.
        try { sealedBytes = await ReadSharedAsync(KeptPath); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try
        {
            var bytes = await protector.UnprotectAsync(sealedBytes);
            try { return JsonSerializer.Deserialize<Kept>(bytes) is { Device: { Length: > 0 } device, Account: { Length: > 0 } account } ? (device, account) : null; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        // Unreadable (another Windows user's, damaged) means none: the profile falls back to the paired device rather than failing.
        catch (Exception) { return null; }
    }

    // The backend replaces these files with File.Move, which Windows refuses while a reader has one open without sharing deletion, so the
    // read allows it. A file caught mid-replacement is tried again briefly; one that stays unreadable fails the read.
    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                var bytes = new byte[file.Length];
                await file.ReadExactlyAsync(bytes);
                return bytes;
            }
            catch (Exception failure) when (attempt < 2 && failure is IOException and not (FileNotFoundException or DirectoryNotFoundException) or UnauthorizedAccessException)
            {
                await Task.Delay(50);
            }
        }
    }
    internal static Task<bool> KeepAsync(IStateProtector protector, string device, string account, CancellationToken token) => new GoogleSessionProfile(protector).KeepAccountAsync(protector, device, account, token);
    private sealed record Kept(string Device, string Account);
}
