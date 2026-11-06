using NativeBeeper.Presentation.ViewModels;

namespace NativeBeeper.Presentation.Services;

// Undo send at quit (2026-10-04). A message still in its Undo send wait when Messages quits is not sent: it goes back to
// its conversation's saved draft, as Undo would have put it back in the box. Before, quitting sent every held message at
// once, so quitting within the wait was the one way to have no chance to take it back. Every quit path does this first,
// before its drafts are written and its windows closed (MainWindow.ReturnHeldSendsAsync): the tray's Quit and the close
// button through their confirmation, MessageLink.exe --quit (which the silent uninstaller uses), and the end of a Windows
// session or Setup's Restart Manager through the session flush; App's shutdown asks once more for anything those missed.
// Held sends are text only (a composer's text or reply), so there are never attachments to put back.
public static class HeldSends
{
    public sealed record Outcome(int Returned, int Saved);

    // Each one goes to the composer showing its conversation now; else the composer that sent it, if it is still open;
    // else the first one given, the main window's, which reads the conversation's saved draft and adds to it.
    public static async Task<Outcome> ReturnToDraftsAsync(Outbox outbox, IReadOnlyList<ComposerViewModel> composers)
    {
        // Held sends, and words queued behind photos still uploading (Enter during an upload): neither has reached the
        // provider. Both are taken before the first await, so nothing lets one go meanwhile.
        IReadOnlyList<Outbox.UndoneSend> taken = [.. outbox.TakeHeld().Concat(outbox.TakeQueued()).OrderBy(undone => undone.Message.CreatedAt)];
        var saved = 0;
        foreach (var held in taken)
        {
            var composer = composers.FirstOrDefault(open => open.ChatId == held.Message.ChatId)
                ?? composers.FirstOrDefault(open => ReferenceEquals(open, held.Owner)) ?? composers.FirstOrDefault();
            if (composer is null) continue;
            // Words that waited in their saved draft (ComposerViewModel.CaptureDraft) are already where they are going. Added to it by a composer that isn't the one they
            // waited in, they would be there twice.
            var owner = held.Owner as ComposerViewModel;
            // Nor does another account's box take words its own draft holds, whichever composer it is.
            var otherAccount = owner?.WaitingAsOtherAccount(held.Message) == true;
            if (owner?.ForgetWaiting(held.Message) == true && (otherAccount || !ReferenceEquals(owner, composer))) { saved++; continue; }
            // One draft that will not write must not keep the rest from theirs.
            try { if (await composer.ReturnHeldAsync(held.Message)) saved++; }
            catch (Exception) { }
        }
        return new(taken.Count, saved);
    }
}
