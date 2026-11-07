using System.Text.Json;

namespace NativeBeeper.App.Services.GoogleMessages;

// Which SIM a conversation sends from on a dual-SIM phone (GoogleSims), for the composer's chip. Read-only: taken from
// what the phone already sends, and never used to choose a SIM.
internal sealed partial class GoogleMessagesBackend
{
    private readonly GoogleSims sims = new();

    /// <summary>Raised off the interface thread when the phone's SIMs change, so open composers can redraw their chip.</summary>
    internal event EventHandler? SimsChanged;

    /// <summary>The SIM a Google Messages conversation sends from, or null for a single-SIM phone or anything else.</summary>
    internal GoogleSim? SimFor(string? chatId)
    {
        if (chatId is null || !Models.Owns(chatId) || sims.Count == 0) return null;
        var native = Models.Native(chatId);
        var chat = chats.TryGetValue(native, out var known) ? known : archive.Chat(native);
        return sims.For(chat?.OutgoingId);
    }

    private void NoteSims(JsonElement value)
    {
        if (sims.Apply(value)) SimsUpdated("settings");
    }

    private void ForgetSims()
    {
        if (sims.Forget()) SimsUpdated("pairing");
    }

    private void SimsUpdated(string source)
    {
        // How many, never which: a SIM's carrier is not logged.
        GoogleMessagesLog.Write($"settings sims={sims.Count} source={source}");
        SimsChanged?.Invoke(this, EventArgs.Empty);
    }
}
