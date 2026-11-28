using System.Text.Json;

namespace MessageLink.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend
{
    // A photo or video the helper no longer holds a reference for is re-learned from the message it came from, as the
    // search archive saved it (GoogleArchive.MediaSource): one ordinary page of that conversation, read by the helper,
    // which also bounds how often (media_relearn.go). An attachment with nothing saved asks the helper nothing.
    internal Task<bool> RelearnMediaAsync(string mediaId, CancellationToken token)
        => RelearnMediaAsync(archive, mediaId, connected && helper is not null ? (parameters, t) => ReadAsync("ui.mediaRelearn", parameters, t) : null, token);

    internal static async Task<bool> RelearnMediaAsync(GoogleArchive archive, string mediaId, Func<object, CancellationToken, Task<JsonElement>>? read, CancellationToken token)
    {
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        string Elapsed() => ((int)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var media = GoogleMessagesLog.Tag(mediaId);
        if (archive.MediaSource(mediaId) is not { } source)
        {
            GoogleMessagesLog.Write($"media relearn outcome=missing record=none archive={(archive.IsLoaded ? "loaded" : "loading")} media={media}");
            return false;
        }
        // self: anchored at the saved message itself, a guess (GoogleMediaSource.Parameters); "missed" says the page did not hold it.
        var page = source.Anchor;
        if (read is null)
        {
            GoogleMessagesLog.Write($"media relearn outcome=failed reason=not_connected record=saved media={media} chat={GoogleMessagesLog.Tag(source.ChatId)}");
            return false;
        }
        string outcome;
        try
        {
            var answer = await read(source.Parameters(mediaId), token);
            outcome = (answer.ValueKind == JsonValueKind.Object && answer.TryGetProperty("outcome", out var said) && said.ValueKind == JsonValueKind.String ? said.GetString() : null) switch
            {
                "found" => "found", "missing" => "missing", "missed" => "missed", "limited" => "limited", "known" => "known", _ => "unexpected",
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            GoogleMessagesLog.Write($"media relearn outcome=failed reason={GoogleMessagesLog.Reason(failure)} record=saved page={page} media={media} chat={GoogleMessagesLog.Tag(source.ChatId)} ms={Elapsed()}");
            return false;
        }
        GoogleMessagesLog.Write($"media relearn outcome={outcome} record=saved page={page} media={media} chat={GoogleMessagesLog.Tag(source.ChatId)} message={GoogleMessagesLog.Tag(source.MessageId)} ms={Elapsed()}");
        return outcome == "found";
    }
}
