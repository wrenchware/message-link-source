using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using NativeBeeper.GoogleMessages.Spike;

namespace NativeBeeper.App.Services.GoogleMessages;

// A saved session made by a QR pairing, which this app no longer starts: it holds no Google-account sign-in, so there is nothing to expire or renew.
internal sealed partial class GoogleMessagesBackend
{
    // Whether the saved pairing is a QR pairing: no Google-account sign-in to expire, renew or time. Read from the session when it is connected with or written, and
    // never guessed: a missing or unreadable session leaves it as it was.
    private volatile bool qrSession;
    public bool QrSession => qrSession;
    // The one commit, in the order SignInAsync's is: the connected helper is let go, the old pairing's bookkeeping is made, the new one is written, and what belonged to the
    // old sign-in is cleared. A failure to write leaves the old pairing on disk as it was (the write is a move of a finished file over it).
    private async Task CommitQrSessionAsync(JsonElement session)
    {
        BeginSignIn();
        try
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                connected = false;
                LastSignInFailure = null;
                var saved = GoogleSessionProfile.HasSavedSession;
                byte[]? replaced = saved ? await File.ReadAllBytesAsync(GoogleSessionProfile.SessionPath) : null;
                // Detached before it is disposed, so the old helper's end cannot mark the new connection down or write its session over the new one.
                var previous = helper;
                helper = null;
                latestSession = null;
                if (previous is not null) await previous.DisposeAsync();
                while (realtime.Reader.TryRead(out _)) { }
                if (replaced is not null) await KeepLocalProfileAsync(session, replaced, protector);
                var plain = Encoding.UTF8.GetBytes(session.GetRawText());
                try { await CommitSessionAsync(await protector.ProtectAsync(plain)); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
                qrSession = true;
                if (tokens is GoogleSessionProfile profile) profile.Invalidate();
                // What belonged to the Google-account sign-in: its age (nothing renews or times a QR pairing), the cookies it handed over last, and the browser profile that
                // renewed it - a full Google sign-in left on the disk of a server that has no use for it.
                GoogleSignInCookies.Forget();
                await GoogleSignInClock.ClearAsync();
                await GoogleMessagesBrowserProfile.ForgetAsync();
                NoteSignedIn();
                ForgetDefaultSms(); ForgetSims();
                GoogleMessagesLog.Write($"sign_in saved minted=none mode=qr replaced={(saved ? "yes" : "no")}");
            }
            finally { gate.Release(); }
        }
        finally { signingIn = false; }
    }

    // Read where the session is read (ConnectAsync) and where it is written; a session that is not an object leaves the answer as it was.
    private void NoteSessionKind(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object) return;
        var qr = GoogleSessionKind.IsQr(session);
        if (qr != qrSession) GoogleMessagesLog.Write($"session kind={(qr ? "qr" : "google_account")}");
        qrSession = qr;
    }
}

// What kind of pairing a saved session is. A Google-account pairing (Gaia) names the phone it was made with (dest_reg_id) and carries the account's cookies; a QR pairing has
// neither, which is how libgm itself tells them apart (AuthData.IsGoogleAccount, HasCookies).
internal static class GoogleSessionKind
{
    public static bool IsQr(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object) return false;
        if (!session.TryGetProperty("browser", out var browser) || browser.ValueKind != JsonValueKind.Object) return false;
        if (session.TryGetProperty("dest_reg_id", out var destination) && destination.ValueKind == JsonValueKind.String
            && destination.GetString() is { Length: > 0 } id && id.Any(character => character is not ('0' or '-'))) return false;
        if (session.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Object && cookies.EnumerateObject().Any()) return false;
        return true;
    }
}
