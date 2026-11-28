using System.Text.Json;

namespace MessageLink.App.Services.Instagram;

/// <summary>
/// The bytes of one file going to the helper and the call that sends them.
/// </summary>
/// <remarks>
/// The helper has one staging buffer, reset whenever a chunk arrives at offset zero, and ui.sendMedia sends whatever the
/// buffer holds. Two files in flight at once would therefore interleave: one could be sent holding the other's bytes, to
/// the wrong person. So a transfer takes the turn before its first chunk and gives it back only once the send has been
/// answered or has failed, and no two can overlap. The turn is held through a failure and a cancellation too; the helper
/// runs its calls in order, so whatever a cancelled transfer left queued has run or been dropped by the time the next one's
/// first chunk is answered.
/// </remarks>
internal static class InstagramMediaTransfer
{
    public static async Task<JsonElement> RunAsync(SemaphoreSlim turn, Func<string, object, CancellationToken, Task<JsonElement>> call,
        Stream source, int chunk, object sendParameters, CancellationToken cancellationToken)
    {
        await turn.WaitAsync(cancellationToken);
        try
        {
            var buffer = new byte[chunk];
            var offset = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                var staged = await call("ui.mediaStage", new { offset, data = buffer[..read] }, cancellationToken);
                // The helper says how much it holds; a disagreement means the two halves are building different files.
                var next = staged.GetProperty("next").GetInt32();
                if (next != offset + read) throw new IOException("The file was not staged in order.");
                offset = next;
            }
            if (offset == 0) throw InstagramBackend.Refused("That file is empty.");
            return await call("ui.sendMedia", sendParameters, cancellationToken);
        }
        finally { turn.Release(); }
    }
}
