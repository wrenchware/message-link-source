using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;

namespace NativeBeeper.App;

// The browser profile of the last Google Messages sign-in the app accepted, kept so a sign-in Google refuses can be
// refreshed without asking (MainWindow.GoogleRecovery). Only the profile's opaque id is written here; the profile itself
// is under GoogleMessages\WebViewProfiles, and the one it replaces is deleted.
internal sealed class GoogleAccountBrowserProfile(string dataRoot)
{
    private string FilePath => Path.Combine(dataRoot, "GoogleMessages", "browser-profile");

    // Where the pointer is, for a host that must tell "no pointer" from "a pointer it could not read" (LinuxGoogle's start-time sweep): Id answers
    // null for both.
    internal string PointerPath => FilePath;

    public string? Id
    {
        get
        {
            try
            {
                var id = File.ReadAllText(FilePath).Trim();
                return GoogleSignInProfile.TryOpen(id, out _, GoogleSignInProfile.MessagesService) ? id : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    // The pairing the profile renewed has been replaced by one with no Google-account sign-in: the pointer goes, and the profile (a full Google sign-in) with it.
    // Nothing is left to refer to a profile that is gone, and a failure to delete leaves it for the host's start-time sweep, which removes what no pointer names.
    // A host that runs a browser on the kept profile (the Linux server's Chrome) sets this to close it: awaited before the pointer changes or the profile is deleted, so a browser is
    // never left running on a folder that is about to go.
    internal Func<Task>? BeforeChange { get; set; }

    private Task StopBrowserAsync() => BeforeChange?.Invoke() ?? Task.CompletedTask;

    public async Task ForgetAsync()
    {
        var previous = Id;
        if (previous is not null) await StopBrowserAsync();
        try { File.Delete(FilePath); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            GoogleMessagesLog.Write("browser profile=not_forgotten");
            return;
        }
        if (previous is null) return;
        await GoogleSignInProfile.DeleteAsync(previous, GoogleSignInProfile.MessagesService);
        GoogleMessagesLog.Write("browser profile=forgotten");
    }

    public async Task KeepAsync(GoogleSignInResult signIn)
    {
        var previous = Id;
        if (previous is not null && previous != signIn.ProfileId) await StopBrowserAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Flushed before it replaces the old pointer: across a restart this profile is the only thing that can renew the
            // sign-in without asking, and the old profile is deleted next.
            await NativeBeeper.Storage.DurableFile.WriteAsync(FilePath + ".tmp", System.Text.Encoding.UTF8.GetBytes(signIn.ProfileId));
            File.Move(FilePath + ".tmp", FilePath, true);
            signIn.TransferProfileOwnership();
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // The sign-in stands; only a later silent refresh is lost, and the profile goes with the result.
            GoogleMessagesLog.Write("browser profile=not_kept");
            return;
        }
        GoogleMessagesLog.Write("browser profile=kept");
        if (previous is not null && previous != signIn.ProfileId)
            await GoogleSignInProfile.DeleteAsync(previous, GoogleSignInProfile.MessagesService);
    }
}

internal static class GoogleMessagesBrowserProfile
{
    internal static GoogleAccountBrowserProfile Default { get; } = new(AppPaths.DataRoot);
    internal static string PointerPath => Default.PointerPath;
    public static string? Id => Default.Id;
    internal static Func<Task>? BeforeChange { get => Default.BeforeChange; set => Default.BeforeChange = value; }
    public static Task ForgetAsync() => Default.ForgetAsync();
    public static Task KeepAsync(GoogleSignInResult signIn) => Default.KeepAsync(signIn);
}
