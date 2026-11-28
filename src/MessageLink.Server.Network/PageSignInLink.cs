using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MessageLink.Protocol;

namespace MessageLink.Server.Network;

/// <summary>
/// The server page's face of a sign-in in the server's own browser (SWITCHBOARD.md 10.6): the object that holds the sign-in (<see cref="RemoteSignInDriver.Owner"/>), where its pictures go and where
/// its progress goes, all onto the page's one WebSocket. A picture is sent as the JPEG itself in a binary message, after a <c>size</c> message whenever the page's size in its own pixels changes; the
/// newest picture is the only one kept, so one the browser is too slow for is replaced, never queued. Google's emoji, the connect's stages and "signed in" go in order as small JSON messages. One
/// writer at a time. Nothing it carries is logged, kept or printed: a picture may show an address or a code.
/// </summary>
internal sealed class PageSignInLink(WebSocket socket, string service) : IRemoteSignInOutput, IProgress<string>, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0);
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly Queue<string> messages = new();
    private RemoteSignInFrame? latest;
    private int width, height;
    private bool signedIn, closed;
    private Task? pump;

    /// <summary>How many pictures reached the page, for the harness.</summary>
    public int FramesSent;

    public void Frame(RemoteSignInFrame frame)
    {
        lock (gate)
        {
            if (closed || signedIn) return;
            latest = frame;
            pump ??= Task.Run(PumpAsync);
        }
        Wake();
    }

    public void SignedIn() => Enqueue(Json(new { t = "signedIn" }), signed: true);

    // Google's emoji, or a Messenger or Instagram connect's stage (RemoteSignIn.ConnectingProgress, EncryptingProgress). Voice reports none.
    public void Report(string value)
    {
        if (value is RemoteSignIn.ConnectingProgress or RemoteSignIn.EncryptingProgress) Enqueue(Json(new { t = "stage", word = value }));
        else if (service == RemoteSignInServices.Google && value is { Length: > 0 and <= 32 }) Enqueue(Json(new { t = "emoji", text = value }));
    }

    /// <summary>The last word, after every picture and message before it: how the sign-in ended, in the page's plain words. Sent once the pump has stopped.</summary>
    public async Task EndAsync(bool ok, string words, CancellationToken cancellationToken)
    {
        await DisposeAsync().ConfigureAwait(false);
        await SendTextAsync(Json(new { t = "end", ok, say = words }), cancellationToken).ConfigureAwait(false);
    }

    private void Enqueue(string message, bool signed = false)
    {
        lock (gate)
        {
            if (closed) return;
            messages.Enqueue(message);
            if (signed)
            {
                signedIn = true;
                latest = null;
            }
            pump ??= Task.Run(PumpAsync);
        }
        Wake();
    }

    private void Wake()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    // A send that cannot be made (the page went away) has nobody to tell; the socket's own close ends the sign-in.
    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                string? message = null;
                RemoteSignInFrame? frame = null;
                lock (gate)
                {
                    if (messages.Count > 0) message = messages.Dequeue();
                    else if (latest is not null) { frame = latest; latest = null; }
                    else if (closed) return;
                }
                if (message is not null) await SendTextAsync(message, CancellationToken.None).ConfigureAwait(false);
                else if (frame is not null) await SendFrameAsync(frame).ConfigureAwait(false);
                else await wake.WaitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception) { }
    }

    private async Task SendFrameAsync(RemoteSignInFrame frame)
    {
        byte[] jpeg;
        try { jpeg = Convert.FromBase64String(frame.Jpeg); }
        catch (FormatException) { return; }
        if (frame.ViewWidth != width || frame.ViewHeight != height)
        {
            width = frame.ViewWidth;
            height = frame.ViewHeight;
            await SendTextAsync(Json(new { t = "size", w = width, h = height }), CancellationToken.None).ConfigureAwait(false);
        }
        await writing.WaitAsync().ConfigureAwait(false);
        try { await socket.SendAsync(jpeg, WebSocketMessageType.Binary, true, CancellationToken.None).ConfigureAwait(false); }
        finally { writing.Release(); }
        Interlocked.Increment(ref FramesSent);
    }

    private async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;
        await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false); }
        finally { writing.Release(); }
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    /// <summary>No more is sent but what is already queued; waits for the pump to finish that.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (gate)
        {
            if (closed && pump is null) return;
            closed = true;
            latest = null;
            pending = pump;
        }
        Wake();
        if (pending is not null) await pending.ConfigureAwait(false);
    }

    public override string ToString() => nameof(PageSignInLink);
}
