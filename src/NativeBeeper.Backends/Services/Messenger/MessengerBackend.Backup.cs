using System.Text.Json;

namespace NativeBeeper.App.Services.Messenger;

/// <summary>
/// Reading Messenger's retained history out of the helper's store and writing it back, a page at a time.
/// </summary>
/// <remarks>
/// That store is the only copy of end-to-end encrypted conversations, and of the history Facebook stops serving once
/// it encrypts a conversation. Both directions stay inside the helper: they need it signed in, never reach Facebook,
/// and never involve the device keys, so a restore cannot register this PC or clone its device. Pages go back in the
/// order they came out. Failures arrive as the helper's codes: login_required, backup_unavailable, backup_expired
/// (start the export again), backup_other_account, backup_unsupported and backup_failed.
/// </remarks>
internal sealed partial class MessengerBackend
{
    // A null cursor starts a new export, abandoning any other in progress; otherwise pass the previous page's Next.
    public async Task<MessengerHistoryPage> ExportHistoryPageAsync(string? cursor, CancellationToken cancellationToken = default)
    {
        if (helper is not { } started) throw new InvalidOperationException("login_required");
        var result = await started.CallAsync("backup.export", new { cursor = cursor ?? "" }, cancellationToken);
        var page = MessengerHistoryPage.Read(result) ?? throw new InvalidOperationException("backup_unsupported");
        if (page.IsLast) MessengerLog.Write($"history export finished pages={page.Number + 1}");
        return page;
    }

    // Lets the helper drop its snapshot now, for an export that will not be finished, instead of when it goes idle.
    public async Task EndHistoryExportAsync(CancellationToken cancellationToken = default)
    {
        if (helper is { } started) await started.CallAsync("backup.exportEnd", null, cancellationToken);
    }

    // The helper's key for the signed-in account's store — the first 16 bytes of SHA-256("messenger:" + Facebook id), in
    // hex — worked out from the same single self identity LocalAccountScope trusts. Null when that is not known, in
    // which case the helper still refuses another account's pages itself.
    internal string? HistoryAccountKey => SelfId is { } self ? AccountKey(self) : null;

    // The signed-in Facebook id, from the one self identity the conversation list names (the saved list included, so it
    // is known before the helper connects). Null when the list names none, or more than one.
    // Who this account is, for the server's page, from the conversations already loaded.
    internal string? OwnName => NativeBeeper.Core.Messaging.AccountIdentity.Of(chats.SelectMany(c => c.Participants).Where(p => p.Self).Select(p => ((string?)p.Id, (string?)p.Name)));

    internal string? SelfId
    {
        get
        {
            var identities = chats.SelectMany(c => c.Participants).Where(p => p.Self && !string.IsNullOrEmpty(p.Id))
                .Select(p => p.Id).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            return identities is [var self] && self.All(char.IsAsciiDigit) ? self : null;
        }
    }

    internal static string AccountKey(string facebookId) => MessengerKeptAccount.StoreKey(facebookId);

    // After a restore the conversation list is read again, so imported conversations appear and the saved list follows.
    internal void HistoryImported()
    {
        chatsReadAt = default;
        ChatsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<MessengerHistoryImport> ImportHistoryPageAsync(JsonElement page, CancellationToken cancellationToken = default)
    {
        if (MessengerHistoryPage.Read(page) is null) throw new InvalidOperationException("backup_unsupported");
        if (helper is not { } started) throw new InvalidOperationException("login_required");
        var result = MessengerHistoryImport.Read(await started.CallAsync("backup.import", page, cancellationToken));
        MessengerLog.Write($"history import page={result.Page} added={result.Added} skipped={result.Skipped} " +
            $"refused={result.Refused} removed={result.Removed} cutoffs={result.Cutoffs}");
        return result;
    }
}
