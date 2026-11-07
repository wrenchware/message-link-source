using System.Text;
using System.Text.Json;
using NativeBeeper.GoogleMessages.Spike;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend
{
    private readonly SemaphoreSlim sessionWrites = new(1);
    private sealed record SessionSnapshot(JsonElement Data);
    private volatile SessionSnapshot? latestSession;
    // The trigger is a fixed log word: what handed the session over (update, connect, replaced, stop).
    private async Task PersistSessionAsync(HelperConnection owner, SessionSnapshot session, string trigger)
    {
        await sessionWrites.WaitAsync();
        string? temporary = null;
        try
        {
            if (!ReferenceEquals(owner,helper) || !ReferenceEquals(session,latestSession)) return;
            var plain=Encoding.UTF8.GetBytes(session.Data.GetRawText());
            byte[] encrypted;
            try { encrypted=await protector.ProtectAsync(plain); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
            if (!ReferenceEquals(owner,helper) || !ReferenceEquals(session,latestSession)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Files.SessionFile)!);
            temporary=Files.SessionFile+"."+Guid.NewGuid().ToString("N")+".tmp";
            await NativeBeeper.Storage.DurableFile.WriteAsync(temporary,encrypted);
            if (!ReferenceEquals(owner,helper) || !ReferenceEquals(session,latestSession)) return;
            File.Move(temporary,Files.SessionFile,true);
            GoogleMessagesLog.Write($"session saved trigger={trigger}");
        }
        // Keep the last valid protected session if disk storage is unavailable; say so, since a refusal at the next launch
        // could otherwise not be told from one Google made.
        catch (Exception failure) { GoogleMessagesLog.Write($"session save=failed trigger={trigger} reason={GoogleMessagesLog.Reason(failure)}"); }
        finally
        {
            if (temporary is not null) try { File.Delete(temporary); } catch { }
            sessionWrites.Release();
        }
    }

    // A renewal or a new pairing, written as every other session save is: to a file of its own, flushed to the disk, then
    // moved over the old one. It used a fixed ".tmp" name and no flush, the one session write that did.
    private async Task CommitSessionAsync(byte[] encrypted)
    {
        await sessionWrites.WaitAsync();
        var temporary=Files.SessionFile+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Files.SessionFile)!);
            await NativeBeeper.Storage.DurableFile.WriteAsync(temporary,encrypted);
            File.Move(temporary,Files.SessionFile,true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
            sessionWrites.Release();
        }
    }

    // Hands fresh cookies from the kept browser profile to the running helper, before Google refuses the ones it uses
    // (cookie_replace.go): the helper checks them with Google and uses them from its next request, and nothing stops or
    // reconnects. The renewal after a refusal ended the helper and started another, ten seconds with nothing arriving
    // every two hours. The outcome is a fixed word: adopted, or the helper's code for why not; the cookies in use stay then.
    internal async Task<string> AdoptCookiesAsync(IReadOnlyDictionary<string,string> cookies, CancellationToken token)
    {
        if (!connected || helper is not { } connection || signIn.SignInExpired || signIn.PairingLost) return "not_connected";
        try { await connection.CallAsync("cookies.replace", new { cookies }, token); }
        // The helper says signed_out before it answers, so a refusal of the sign-in in use meanwhile is already known here:
        // that is the running cookies expiring, not Google turning the new ones down, and says nothing about the next try.
        catch (Exception) when (signIn.SignInExpired || signIn.PairingLost) { return "refused_meanwhile"; }
        catch (Exception failure) { return GoogleMessagesLog.Reason(failure); }
        Files.Cookies.Remember(cookies);
        await Files.Clock.RecordAsync(DateTimeOffset.UtcNow);
        return "adopted";
    }

    // The same comparison against the session on disk, for a refresh made before Google refused anything: whether loading
    // the kept profile early gets a new sign-in at all is what decides if an expiry can be prevented rather than repaired.
    internal async Task<string[]> ChangedFromSavedAsync(IReadOnlyDictionary<string,string> cookies)
    {
        var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(Files.SessionFile));
        try
        {
            using var document = JsonDocument.Parse(plain);
            return Files.Cookies.Changed(document.RootElement, cookies);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }
}
