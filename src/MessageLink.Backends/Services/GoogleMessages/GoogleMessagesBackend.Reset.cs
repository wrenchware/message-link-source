namespace MessageLink.App.Services.GoogleMessages;

// Settings > Troubleshooting > Google Messages local data: what the backend itself holds by conversation number.
internal sealed partial class GoogleMessagesBackend
{
    // Every conversation known, live or saved, hidden ones included: what a reset has to find before anything is deleted.
    internal GoogleChat[] KnownChatsSnapshot()
    {
        var known = new Dictionary<string, GoogleChat>(StringComparer.Ordinal);
        foreach (var chat in archive.ChatsSnapshot()) known[chat.Id] = chat;
        foreach (var chat in chats.Values) known[chat.Id] = chat;
        return [.. known.Values];
    }

    // Empties the search archive and the conversations and messages held in memory, and forgets the renumbering and the
    // fingerprints it was judged by, so the next list read fills everything from the phone under its current numbers.
    // The pairing and the session are untouched.
    internal async Task ResetLocalAsync(string profile)
    {
        await StopHistoryIndexingAsync();
        await archive.ResetAsync(profile);
        await directoryGate.WaitAsync();
        // The held whole list goes too: answered over the emptied conversations, the next list read after a reset came back
        // with no Google conversation at all until a later read.
        try { chats.Clear(); messages.Clear(); lock (listedChatIds) listedChatIds.Clear(); placeholders.Clear(); ArrivalHold.Clear(); lastListed = null; lastList = null; chatWritten.Clear(); }
        finally { directoryGate.Release(); }
        await ResetIdentitiesAsync(profile);
        if (connected) StartHistoryIndexing();
    }
}
