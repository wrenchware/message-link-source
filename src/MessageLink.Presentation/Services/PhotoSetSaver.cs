using MessageLink.Core.Messaging;

namespace MessageLink.Presentation.Services;

public static class PhotoSetSaver
{
    public static async Task<(int Saved, int Failed)> SaveAsync(IReadOnlyList<MessageAttachment> photos, string folder,
        Func<MessageAttachment, Stream, CancellationToken, Task> copy, CancellationToken token = default)
    {
        var saved = 0; var failed = 0;
        for (var index = 0; index < photos.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var temporary = Path.Combine(folder, ".messages-" + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                var photo = photos[index];
                var name = ReceivedFiles.PhotoName(photo.Name, photo.MimeType);
                await using (var destination = await Task.Run(() => new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous), token))
                { await copy(photo, destination, token); await destination.FlushAsync(token); }
                await Task.Run(() =>
                {
                    // On the partial file, so the photo never stands in the folder without it.
                    ReceivedFiles.MarkFromInternet(temporary);
                    for (var suffix = 0; ; suffix++)
                    {
                        token.ThrowIfCancellationRequested();
                        var target = Path.Combine(folder, $"{index + 1:D2}-" + (suffix == 0 ? name : Path.GetFileNameWithoutExtension(name) + $" ({suffix})" + Path.GetExtension(name)));
                        try { File.Move(temporary, target, false); break; }
                        catch (IOException) when (File.Exists(target)) { }
                    }
                }, token);
                saved++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { failed++; }
            finally
            {
                try { await Task.Run(() => File.Delete(temporary)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return (saved, failed);
    }
}
