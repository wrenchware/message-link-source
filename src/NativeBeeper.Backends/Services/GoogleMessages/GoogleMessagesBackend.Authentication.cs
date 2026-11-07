using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using NativeBeeper.GoogleMessages.Spike;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend
{
    // Renews the saved pairing, or with asNewDevice (or none saved) pairs this computer afresh with the phone's emoji
    // step. A pairing the phone removed cannot be renewed; only a new one brings it back. renewOnly is for the invisible
    // refresh, which must never start a pairing the user cannot see: with no saved pairing it fails instead.
    public async Task SignInAsync(IReadOnlyDictionary<string,string> cookies,IProgress<string> pairingEmoji,CancellationToken token,bool asNewDevice=false,bool renewOnly=false)
    {
        // A connect attempt holding the lock would otherwise be waited out, and every read queued behind it after that.
        BeginSignIn();
        try
        {
            await gate.WaitAsync(token);
            var renewing = false;
            try
            {
                connected = false;
                LastSignInFailure = null;
                var saved = Files.SessionSaved;
                // A QR pairing has no sign-in to renew, so a Google-account sign-in over it is a new pairing.
                var fresh = asNewDevice || !saved || qrSession;
                renewing = !fresh;
                if (renewOnly && fresh) throw new BackendException(BackendFailure.AuthenticationRequired,"There is no saved Google Messages pairing to renew.");
                GoogleMessagesLog.Write($"sign_in starting mode={(!fresh ? "renew" : saved ? "new_device" : "pair")} age_min={Files.Clock.AgeMinutes(DateTimeOffset.UtcNow)?.ToString() ?? "unknown"}");
                // Detach before disposal so the old pump's EOF callback cannot mark the
                // replacement connection disconnected or enqueue an obsolete status. What it last handed over (a token it
                // refreshed while the connection was down) is saved first, as ConnectAsync does when it replaces one.
                var previousHelper = helper;
                if (previousHelper is not null && latestSession is { } lastKnown) await PersistSessionAsync(previousHelper,lastKnown,"replaced");
                helper = null;
                if (previousHelper is not null) await previousHelper.DisposeAsync();
                while (realtime.Reader.TryRead(out _)) { }
                await using var renewal = await HelperConnection.StartAsync(HelperExecutables.PathFor("gmessages-helper"), null, token, Slot.HelperEnvironment);
                JsonElement result;
                // Whether Google issued new sign-in cookies, which is what restarts its two-hour clock (GoogleSignInClock).
                string[] minted=[];
                // The pairing a new one replaces, as it is on disk, kept in memory until the new one is safely written.
                byte[]? replaced = null;
                if(!fresh)
                {
                    var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(Files.SessionFile,token));
                    try
                    {
                        using var document = JsonDocument.Parse(plain);
                        minted = Files.Cookies.Changed(document.RootElement, cookies);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(135));
                        result = await renewal.CallAsync("reauthenticate",new { auth=document.RootElement,cookies },timeout.Token);
                    }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
                }
                else
                {
                    if (saved) replaced = await File.ReadAllBytesAsync(Files.SessionFile,token);
                    var challenge=await renewal.CallAsync("pair.start",new {cookies},token);
                    GoogleMessagesLog.Write("sign_in challenge=ready");
                    pairingEmoji.Report(challenge.GetProperty("emoji").GetString() ?? "");
                    result=await renewal.CallAsync("pair.finish",null,token);
                }
                // Only commit validated same-account credentials; failures leave the saved pairing intact. Once the helper
                // has answered, the phone and Google have accepted it: it is written whatever happens to the caller now, and
                // written durably. A pairing the phone had accepted used to be thrown away by a cancellation or a failed
                // profile copy before it. The bookkeeping for a new pairing (the old one kept beside it, local state kept
                // under the old device) comes first but cannot fail it, and is harmless if the write then fails.
                if (ValidateAccount is { } validate) await validate(AccountIdentity(result), CancellationToken.None);
                if (replaced is not null) await KeepLocalProfileAsync(result,replaced,protector);
                var updated = Encoding.UTF8.GetBytes(result.GetRawText());
                try
                {
                    var encrypted = await protector.ProtectAsync(updated);
                    await CommitSessionAsync(encrypted);
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(updated); }
                // A new pairing may be another device or account. Said here, where the session was replaced, so that whoever hosts this backend
                // answers with the new key at once whether or not its window tells it (HostIdentityPort).
                if (tokens is GoogleSessionProfile profile) profile.Invalidate();
                NoteSessionKind(result);
                Files.Cookies.Remember(cookies);
                NoteSignedIn();
                // A new pairing may be another phone; what the old one said about its default SMS app or its SIMs no longer holds.
                if (fresh) { ForgetDefaultSms(); ForgetSims(); }
                // A browser sign-in, a new pairing, or a refresh in which Google issued new cookies starts the clock again;
                // a refresh that handed back the same cookies does not, since Google still counts from the old sign-in.
                var madeNow = fresh || minted.Length > 0;
                if (madeNow) await Files.Clock.RecordAsync(DateTimeOffset.UtcNow);
                GoogleMessagesLog.Write($"sign_in saved minted={(fresh ? "new" : minted.Length > 0 ? "yes" : "no")} changed=[{string.Join(",", minted)}]");
            }
            catch (InvalidOperationException ex)
            {
                GoogleMessagesLog.Write($"sign_in failed reason={GoogleMessagesLog.Reason(ex)}");
                LastSignInFailure = ex.Message;
                // Google no longer knows the pairing this renewal tried to keep, so renewing again cannot work: the state,
                // the invisible refresh, the banner and the dialog turn to pairing as a new device.
                if (renewing && ex.Message == "pairing_lost") MarkPairingLost("renew");
                throw new BackendException(BackendFailure.AuthenticationRequired,ex.Message switch
                {
                    "wrong_account" => "Sign in with the same Google account already paired with this app.",
                    "pair_start_failed" or "pair_finish_failed" => "Your phone did not finish connecting. Open Google Messages, go to Device pairing, and try again.",
                    "authentication_required" => "Google did not accept this sign-in. Try signing in again.",
                    "pairing_lost" => "Google no longer has this computer paired with your phone. Choose Pair as a new device.",
                    "phone_unavailable" => "Your phone didn't answer, so the sign-in couldn't be checked. Open Google Messages on your phone and try again. Only if this computer is gone from Device pairing there, choose Pair as a new device.",
                    var code when GooglePairingFailure.Message(code) is { } reason => reason,
                    _ => "Couldn't finish Google sign-in. Check your phone connection and try again."
                });
            }
            catch (Exception failure) { GoogleMessagesLog.Write($"sign_in failed reason={GoogleMessagesLog.Reason(failure)}"); throw; }
            finally { gate.Release(); }
        }
        finally { signingIn = false; }
    }

    // A new pairing gives this computer a new device identity, but everything it keeps locally — pins, labels, linked
    // conversations, drafts, the saved archive — is filed under the old one. For the same Google account it stays filed
    // there; a different account starts afresh, as it should. The old pairing is kept beside the new one, not deleted.
    // Runs after the new pairing is written, from the old one's bytes read before it, and never undoes the pairing: a
    // failure here costs only this bookkeeping, which is logged.
    private async Task KeepLocalProfileAsync(JsonElement paired,byte[] replaced,IStateProtector protector)
    {
        byte[]? previous = null;
        try
        {
            await NativeBeeper.Storage.DurableFile.WriteAsync(Files.SessionFile + ".previous", replaced);
            previous = await protector.UnprotectAsync(replaced);
            using var old = JsonDocument.Parse(previous);
            var (oldDevice, oldAccount) = GooglePairingIdentity.Identity(old.RootElement);
            var (newDevice, newAccount) = GooglePairingIdentity.Identity(paired);
            var sameAccount = oldAccount is not null && oldAccount == newAccount;
            var kept = GooglePairingIdentity.KeepsOldDevice(oldDevice, oldAccount, newDevice, newAccount) && await Files.KeepAccountAsync(protector, oldDevice!, oldAccount!, CancellationToken.None);
            GoogleMessagesLog.Write($"sign_in new_device same_account={sameAccount} same_device={oldDevice == newDevice} profile_kept={kept}");
        }
        catch (Exception failure) { GoogleMessagesLog.Write($"sign_in new_device profile_kept=failed reason={GoogleMessagesLog.Reason(failure)}"); }
        finally { if (previous is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(previous); }
    }
}
