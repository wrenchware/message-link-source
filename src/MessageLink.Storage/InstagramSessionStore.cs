using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageLink.Core.Services;

namespace MessageLink.Storage;

/// <summary>A saved session read back, or the fixed word naming why there was none to resume.</summary>
public sealed record SavedSessionRead(byte[]? Session, string? Problem);

/// <summary>The saved limits state: its bytes, or null with <paramref name="Present"/> telling "never written" from "written and unreadable".</summary>
public sealed record GuardRead(byte[]? Plain, bool Present);

/// <summary>
/// Everything Instagram keeps between runs: the session envelope the helper hands back, and the last conversations it
/// reported so the list is not empty while the next connection is still opening.
/// </summary>
/// <remarks>
/// There is deliberately no message archive here. Messenger keeps one because Facebook stops serving a conversation's
/// history once it encrypts it, so what has already been fetched is the only copy there is. Stage 3 found the opposite
/// on Instagram: four of five sampled threads paged back to provider exhaustion, one of them 3,707 days, with no
/// encrypted cutoff anywhere. History Instagram will serve again does not need to be kept, and keeping message bodies
/// that nothing requires is a cost paid in risk.
///
/// Both files are protected for the Windows user and live under the account the helper verified, never under a raw
/// Instagram id. Messenger had to grow <c>adoptLegacyDatabase</c> to move an earlier unpartitioned database into an
/// account folder; partitioning from the first release is why Instagram will never need that.
/// </remarks>
public sealed class InstagramSessionStore(string root, IStateProtector protector)
{
    // The helper's verified account hash: 64 lowercase hex characters and nothing else. This is the only value that
    // ever names a directory, so rejecting anything else is also what stops a provider-supplied string walking the path.
    public static bool IsAccountKey(string? key) =>
        key is { Length: 64 } && key.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    /// <summary>A file in an account's folder that says its session lives on a Messages Server now (a move). The session is neither read from nor written over such a folder.</summary>
    public const string MovedAwayFile = "moved-away.json";

    private string PointerPath => Path.Combine(root, "active.protected");
    private string AccountFolder(string accountKey) => Path.Combine(root, "accounts", accountKey);
    private string SessionPath(string accountKey) => Path.Combine(AccountFolder(accountKey), "session.protected");
    private string ChatsPath(string accountKey) => Path.Combine(AccountFolder(accountKey), "chats.protected");

    /// <summary>Whether some account is signed in, without reading or decrypting the account itself.</summary>
    public bool HasSession => Exists(root);

    /// <summary>
    /// The same question before a store exists, for the startup decisions that have to know whether Instagram is
    /// signed in before anything is constructed.
    /// </summary>
    public static bool Exists(string root) => File.Exists(Path.Combine(root, "active.protected"));

    /// <summary>The limits' file beside the pointer (<see cref="ReadGuardAsync"/>): a move carries it with the session.</summary>
    public const string GuardFile = "guard.protected";
    private string GuardPath => Path.Combine(root, GuardFile);

    /// <summary>
    /// What the safety limits remember across a restart (the hourly background-read budget and an account check Instagram asked for): a small sealed file
    /// of counts, times and one fixed code word. <see cref="GuardRead.Present"/> is false when none was ever written; present but unreadable is reported as
    /// such, and the caller treats it as the strictest state rather than as nothing.
    /// </summary>
    public async Task<GuardRead> ReadGuardAsync()
    {
        if (!File.Exists(GuardPath)) return new(null, false);
        try { return new(await protector.UnprotectAsync(await File.ReadAllBytesAsync(GuardPath)), true); }
        catch (Exception) { return new(null, true); }
    }

    /// <summary>Saves the limits' state. Throws when it cannot be written: the caller must not go on as though the limit were kept.</summary>
    public async Task SaveGuardAsync(byte[] plain)
    {
        Directory.CreateDirectory(root);
        await ReplaceAsync(GuardPath, await protector.ProtectAsync(plain));
    }

    /// <summary>Which account the app should resume, or null when there is none to resume.</summary>
    public async Task<string?> ActiveAccountAsync() => (await ReadPointerAsync()).Key;

    // The account the pointer names, or which step found nothing to resume.
    private async Task<(string? Key, string? Problem)> ReadPointerAsync()
    {
        if (!File.Exists(PointerPath)) return (null, "pointer_missing");
        byte[] plain;
        try { plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(PointerPath)); }
        // A pointer written by another Windows user, or left half-written by a power cut, is simply not a session.
        catch (Exception) { return (null, "pointer_unreadable"); }
        try
        {
            var key = Encoding.UTF8.GetString(plain);
            return IsAccountKey(key) ? (key, null) : (null, "pointer_invalid");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    /// <summary>
    /// Stores the helper's session reply and makes that account the one to resume. The account is read out of the
    /// envelope rather than passed alongside it, so a caller cannot file one account's credentials under another's name.
    /// </summary>
    public async Task SaveSessionAsync(byte[] envelope)
    {
        var accountKey = ReadAccountKey(envelope) ?? throw new ArgumentException("The session envelope names no account.", nameof(envelope));
        if (File.Exists(Path.Combine(AccountFolder(accountKey), MovedAwayFile))) throw new InvalidOperationException("moved_away");
        Directory.CreateDirectory(AccountFolder(accountKey));
        await ReplaceAsync(SessionPath(accountKey), await protector.ProtectAsync(envelope));
        var pointer = Encoding.UTF8.GetBytes(accountKey);
        try { await ReplaceAsync(PointerPath, await protector.ProtectAsync(pointer)); }
        finally { CryptographicOperations.ZeroMemory(pointer); }
    }

    /// <summary>
    /// The session to resume, or null when there is none this Windows user can read. The caller owns the returned
    /// bytes and should zero them once the helper has them: this is credential-bearing and is never a diagnostic.
    /// </summary>
    public async Task<byte[]?> LoadSessionAsync() => (await ReadSessionAsync()).Session;

    /// <summary>
    /// The session to resume, as <see cref="LoadSessionAsync"/> gives it, or — when there is none — which step found
    /// nothing: pointer_missing, pointer_unreadable, pointer_invalid, session_missing, session_unreadable, session_invalid
    /// or account_mismatch. Fixed words only, so the reason can be logged where the session and account never are.
    /// </summary>
    public async Task<SavedSessionRead> ReadSessionAsync()
    {
        var (accountKey, problem) = await ReadPointerAsync();
        if (accountKey is null) return new(null, problem);
        if (File.Exists(Path.Combine(AccountFolder(accountKey), MovedAwayFile))) return new(null, "moved_away");
        var path = SessionPath(accountKey);
        if (!File.Exists(path)) return new(null, "session_missing");
        var stored = await ReadProtectedAsync(path);
        if (stored is null) return new(null, "session_unreadable");
        // The envelope must still name the account whose folder it came out of. A file swapped between folders, or a
        // pointer left behind by an interrupted account change, must not hand the helper the wrong account's session.
        var named = ReadAccountKey(stored);
        if (named == accountKey) return new(stored, null);
        CryptographicOperations.ZeroMemory(stored);
        return new(null, named is null ? "session_invalid" : "account_mismatch");
    }

    /// <summary>The conversations last reported for this account, so the list keeps them across a restart.</summary>
    public Task SaveChatsAsync(string accountKey, byte[] chats)
    {
        RequireAccountKey(accountKey);
        return SaveChatsCoreAsync(accountKey, chats);
    }

    private async Task SaveChatsCoreAsync(string accountKey, byte[] chats)
    {
        Directory.CreateDirectory(AccountFolder(accountKey));
        await ReplaceAsync(ChatsPath(accountKey), await protector.ProtectAsync(chats));
    }

    /// <summary>
    /// The cached conversations, or null when there are none to use. A cache that cannot be read is not an error:
    /// the connection that follows reports the real list anyway.
    /// </summary>
    public Task<byte[]?> LoadChatsAsync(string accountKey)
    {
        RequireAccountKey(accountKey);
        return ReadProtectedAsync(ChatsPath(accountKey));
    }

    /// <summary>
    /// Forgets one account completely. Signing out must leave nothing behind, and an account that is no longer the
    /// active one must not keep answering for it.
    /// </summary>
    public async Task ForgetAsync(string accountKey)
    {
        RequireAccountKey(accountKey);
        if (await ActiveAccountAsync() == accountKey)
            try { File.Delete(PointerPath); } catch (Exception) { /* Removing the account's own files still matters. */ }
        try { Directory.Delete(AccountFolder(accountKey), true); }
        catch (DirectoryNotFoundException) { /* Already gone. */ }
        catch (Exception) { /* A file held open elsewhere is reported by the next read failing, not by throwing here. */ }
    }

    private static void RequireAccountKey(string accountKey)
    {
        if (!IsAccountKey(accountKey)) throw new ArgumentException("That is not a verified account key.", nameof(accountKey));
    }

    private async Task<byte[]?> ReadProtectedAsync(string path)
    {
        if (!File.Exists(path)) return null;
        try { return await protector.UnprotectAsync(await File.ReadAllBytesAsync(path)); }
        // Written by another Windows user, truncated, or from a format this build does not know: not readable state.
        catch (Exception) { return null; }
    }

    // Reads only the account name out of an envelope whose other fields are credentials. Nothing else is parsed, kept
    // or reported, and a malformed envelope is simply not one.
    private static string? ReadAccountKey(byte[] envelope)
    {
        try
        {
            using var document = JsonDocument.Parse(envelope);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("accountKey", out var value) || value.ValueKind != JsonValueKind.String) return null;
            var key = value.GetString();
            return IsAccountKey(key) ? key : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task ReplaceAsync(string path, byte[] protectedValue)
    {
        var temporary = path + ".tmp";
        await DurableFile.WriteAsync(temporary, protectedValue);
        File.Move(temporary, path, true);
    }
}
