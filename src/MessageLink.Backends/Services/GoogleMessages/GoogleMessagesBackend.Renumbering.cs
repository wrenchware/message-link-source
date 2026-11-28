using MessageLink.Core.Services;
using MessageLink.Presentation.Services;

namespace MessageLink.App.Services.GoogleMessages;

// Notices the phone renumbering its conversations (GoogleRenumbering) and refuses Google sends until this PC's Google
// data is reset: a merged or labelled conversation that still points at the old number would send to someone else.
// Kept per profile in the protected store: each conversation number with a one-way fingerprint of who is in it, and
// whether a renumbering was seen. No number or name is stored.
internal sealed partial class GoogleMessagesBackend
{
    internal const string RenumberedNotice = IGoogleMessagesAccount.RenumberedNotice;
    private const string IdentityCategory = "google-identity", IdentityId = "book";
    private sealed record IdentityBook(Dictionary<string, string> Chats, bool Renumbered);
    private readonly SemaphoreSlim identityGate = new(1);
    private string? identityProfile;
    private IdentityBook? identityBook;
    private volatile bool renumbered;
    internal bool Renumbered => renumbered;
    // Raised on any thread when the renumbered state is first read or changes.
    internal event EventHandler<bool>? RenumberedChanged;

    private void SetRenumbered(bool value)
    {
        renumbered = value;
        RenumberedChanged?.Invoke(this, value);
    }

    // Caller holds identityGate. Read once per profile; an unreadable book starts empty rather than blocking sends.
    private async Task<IdentityBook> IdentityBookAsync(string profile)
    {
        if (identityProfile == profile && identityBook is not null) return identityBook;
        IdentityBook? saved = null;
        try { saved = await privateState.ReadAsync<IdentityBook>(profile, IdentityCategory, IdentityId); }
        catch (Exception) { saved = null; }
        identityBook = saved is { Chats: not null } ? saved : new(new(StringComparer.Ordinal), false);
        identityProfile = profile;
        SetRenumbered(identityBook.Renumbered);
        return identityBook;
    }

    // Reads the saved state so the window can say so at launch, before any list read or send.
    internal async Task LoadRenumberedAsync()
    {
        try
        {
            if (await NotificationIdentity.ProfileAsync(tokens) is not { } profile) return;
            await identityGate.WaitAsync();
            try { await IdentityBookAsync(profile); }
            finally { identityGate.Release(); }
        }
        catch (Exception) { /* The next list read or send reads it again. */ }
    }

    // Every fresh conversation list: compared with what was saved, then saved. Once renumbered, nothing more is learned
    // until the reset; the first list after it seeds the book again.
    private async Task ObserveIdentitiesAsync(IReadOnlyList<GoogleChat> list)
    {
        try
        {
            if (await NotificationIdentity.ProfileAsync(tokens) is not { } profile) return;
            var fresh = GoogleRenumbering.Fingerprints(profile, list);
            if (fresh.Count == 0) return;
            await identityGate.WaitAsync();
            try
            {
                var book = await IdentityBookAsync(profile);
                if (book.Renumbered) return;
                var (isRenumbered, changed, compared) = GoogleRenumbering.Compare(book.Chats, fresh);
                if (isRenumbered)
                {
                    var flagged = book with { Renumbered = true };
                    await privateState.WriteAsync(profile, IdentityCategory, IdentityId, flagged);
                    identityBook = flagged;
                    MessageLink.App.Services.AppLog.Write($"google renumbered changed={changed} compared={compared}");
                    SetRenumbered(true);
                    return;
                }
                if (fresh.All(pair => book.Chats.TryGetValue(pair.Key, out var print) && print == pair.Value)) return;
                var next = new Dictionary<string, string>(book.Chats, StringComparer.Ordinal);
                foreach (var (id, print) in fresh) next[id] = print;
                var updated = book with { Chats = next };
                await privateState.WriteAsync(profile, IdentityCategory, IdentityId, updated);
                identityBook = updated;
            }
            finally { identityGate.Release(); }
        }
        catch (Exception) { /* A fingerprint not kept only delays noticing. */ }
    }

    // Definite and never retried: nothing was sent. Read for the current profile every time, so a flag seen under another
    // pairing never blocks this one.
    internal async Task ThrowIfRenumberedAsync()
    {
        if (await NotificationIdentity.ProfileAsync(tokens) is not { } profile) return;
        await identityGate.WaitAsync();
        try { if ((await IdentityBookAsync(profile)).Renumbered) throw new SendException(false, RenumberedNotice); }
        finally { identityGate.Release(); }
    }

    // Part of the reset: forgets the fingerprints and the flag. The next list read seeds them from the current numbers.
    private async Task ResetIdentitiesAsync(string profile)
    {
        await identityGate.WaitAsync();
        try
        {
            await privateState.RemoveAsync(profile, IdentityCategory, IdentityId);
            identityBook = new(new(StringComparer.Ordinal), false);
            identityProfile = profile;
            SetRenumbered(false);
        }
        finally { identityGate.Release(); }
    }
}
