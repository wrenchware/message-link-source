using System.Security.Cryptography;
using System.Text;
using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services;

/// <summary>
/// Names this PC keeps for people the provider does not name. One file per identity, protected for the Windows user.
/// </summary>
/// <remarks>
/// A person's name is contact detail, so unlike the pin and mute preferences beside it — which are booleans and are
/// written in the clear — this is protected. The file is named by a digest of the profile and identity, so the folder
/// listing says nothing about who is in it either.
/// </remarks>
public sealed class LocalContactNameStore(string directory, IStateProtector protector) : ILocalContactNames
{
    public static string DefaultDirectory => AppPaths.Combine("ContactNames-v1");
    public LocalContactNameStore(IStateProtector protector) : this(DefaultDirectory, protector) { }

    // Long enough for any name a person would type, short enough that the file cannot become a store of something else.
    public const int MaxNameLength = 128;

    private string PathFor(string profile, string identity) =>
        Path.Combine(directory, NotificationIdentity.Hash(profile + "\n" + identity) + ".name");

    public async Task<string?> GetAsync(string profile, string identity, CancellationToken cancellationToken = default)
    {
        var path = PathFor(profile, identity);
        if (!File.Exists(path)) return null;
        byte[] plain;
        // A name this Windows user cannot read is not a name; the conversation keeps the provider's own.
        try { plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(path, cancellationToken)); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
        try
        {
            var name = Encoding.UTF8.GetString(plain);
            return name.Length is > 0 and <= MaxNameLength ? name : null;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    // Removes the names kept for these identities and returns how many there were. The folder is listed once, so a long
    // sweep of identities that were never named touches the disk only for real files.
    public int Forget(string profile, IEnumerable<string> identities)
    {
        if (!Directory.Exists(directory)) return 0;
        var present = Directory.EnumerateFiles(directory, "*.name").Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var identity in identities)
        {
            var path = PathFor(profile, identity);
            if (!present.Contains(Path.GetFileName(path))) continue;
            try { File.Delete(path); removed++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    public async Task SetAsync(string profile, string identity, string? name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(profile, identity);
        var trimmed = name?.Trim();
        // Clearing a name is not storing an empty one: the conversation goes back to whatever the provider calls it.
        if (string.IsNullOrEmpty(trimmed))
        {
            try { File.Delete(path); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
            return;
        }
        if (trimmed.Length > MaxNameLength) throw new ArgumentException("That name is too long.", nameof(name));
        Directory.CreateDirectory(directory);
        var plain = Encoding.UTF8.GetBytes(trimmed);
        try
        {
            var protectedName = await protector.ProtectAsync(plain);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, protectedName, cancellationToken);
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
