namespace MessageLink.Presentation.Services;

// The composer a share is staged into (ConversationView in the app). It has no way to send: staging is the end of a
// share, and the user presses Send.
public interface IShareComposer
{
    // On the chosen conversation's send route and ready for text.
    bool IsReadyFor(string target);
    // The chosen conversation is still what this composer's view shows, and the view is still on screen.
    bool IsShown(string target);
    // Attachments can be staged now (the service allows them, and no other attachment is being prepared).
    bool CanAttach { get; }
    bool HasReply { get; }
    // What the composer says below itself.
    string Status { set; }
    ComposerInsertion.Staged AppendText(string text);
    // Stages the files exactly as a drop does; false when nothing was added (the composer then says why).
    Task<bool> StageFilesAsync(IReadOnlyList<string> paths);
}

public enum ShareOutcome { Staged, NotReady, Reply, NoAttachments, FilesRefused, TooLong }

public static class ShareHandOff
{
    // How long a ready composer waits for its service to allow attachments (the permission check it makes when opened)
    // before saying they are not available.
    public static readonly TimeSpan AttachWait = TimeSpan.FromSeconds(5);
    public const string ReplyBlocks = "Cancel the reply to attach a file.";
    public const string NoAttachments = "Attachments aren't available in this conversation right now.";

    // Waits for the chosen conversation's composer as Forward does (ComposerInsertion.KeepWaiting), then stages the files
    // and appends any links on a line of their own. Files first: if they cannot be staged, nothing is added at all.
    public static Task<ShareOutcome> StageAsync(IShareComposer composer, string target, ShareCheck share, TimeProvider clock, CancellationToken cancellationToken = default) =>
        StageAsync(composer, target, share.AttachmentPaths is { Count: > 0 } paths ? () => composer.StageFilesAsync(paths) : null, share.Text, clock, cancellationToken);

    // The same hand-off for anything that stages its attachments some other way: Forward with photos stages pictures it
    // has already read from a message (ForwardContent), not files on disk. stageAttachments is null when there are none.
    public static async Task<ShareOutcome> StageAsync(IShareComposer composer, string target, Func<Task<bool>>? stageAttachments, string? text,
        TimeProvider clock, CancellationToken cancellationToken = default)
    {
        var started = clock.GetTimestamp();
        TimeSpan Waited() => clock.GetElapsedTime(started);
        while (!composer.IsReadyFor(target))
        {
            if (!ComposerInsertion.KeepWaiting(Waited(), composer.IsShown(target))) return ShareOutcome.NotReady;
            await Task.Delay(TimeSpan.FromMilliseconds(Waited() > ComposerInsertion.ForwardFirstWait ? 250 : 50), clock, cancellationToken);
        }
        if (stageAttachments is not null)
        {
            if (composer.HasReply) { composer.Status = ReplyBlocks; return ShareOutcome.Reply; }
            var ready = clock.GetTimestamp();
            while (!composer.CanAttach)
            {
                if (clock.GetElapsedTime(ready) > AttachWait || !composer.IsReadyFor(target))
                { composer.Status = NoAttachments; return ShareOutcome.NoAttachments; }
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, cancellationToken);
            }
            if (!await stageAttachments()) return ShareOutcome.FilesRefused;
        }
        if (text is not null)
            switch (composer.AppendText(text))
            {
                case ComposerInsertion.Staged.TooLong: return ShareOutcome.TooLong;
                case ComposerInsertion.Staged.NotReady: return ShareOutcome.NotReady;
            }
        return ShareOutcome.Staged;
    }
}
