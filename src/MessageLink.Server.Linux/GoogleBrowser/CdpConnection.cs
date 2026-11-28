using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace MessageLink.Server.Linux;

/// <summary>A DevTools event as it arrived: its method, the page session it belongs to (null for the browser's own), and its parameters.</summary>
internal sealed record CdpEvent(string Method, string? SessionId, JsonElement Params);

/// <summary>A DevTools command the browser answered with an error. Only its numeric code is kept: the browser's text can name the page.</summary>
internal sealed class CdpException(int code) : Exception("cdp_error")
{
    public int Code { get; } = code;
}

/// <summary>
/// The DevTools protocol over Chromium's <c>--remote-debugging-pipe</c>: two plain streams, each message one JSON object ended by a NUL byte. Nothing
/// listens on a port, so no other process, container or machine can reach the browser; only the process that started it holds the two ends
/// (the server foundation's broker published DevTools on an unauthenticated TCP port, which this replaces).
/// <para>
/// One reader takes every message in order: answers complete their command, events go to <see cref="Events"/>, which the caller drains on its own, so a
/// handler that sends a command never waits on the reader that would deliver its answer. Nothing read here is logged.
/// </para>
/// </summary>
internal sealed class CdpConnection : IAsyncDisposable
{
    /// <summary>Larger than any answer this browser is asked for (a page's cookies, its frame tree); anything bigger ends the connection.</summary>
    public const int MaxMessage = 8 * 1024 * 1024;

    // How messages come and go: NUL-ended on the pipe's two streams, or one message each on a WebSocket (the sign-in's loopback port, OverWebSocket).
    private readonly Func<Func<ReadOnlyMemory<byte>, Task>, Task> pump;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> write;
    private readonly Func<ValueTask> release;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    // Unbounded: a reader that waited for room would hold up the answers too, and the close at the end of a run is a command whose answer must
    // still come. A run lasts seconds and hears only page and document events, so what can queue is small.
    private readonly Channel<CdpEvent> events = Channel.CreateUnbounded<CdpEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task reader = Task.CompletedTask;
    private int nextId;

    public CdpConnection(Stream fromBrowser, Stream toBrowser)
        : this(onMessage => PumpPipeAsync(fromBrowser, onMessage), (message, cancellationToken) => WritePipeAsync(toBrowser, message, cancellationToken), async () =>
        {
            try { await toBrowser.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            try { await fromBrowser.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
        })
    {
    }

    private CdpConnection(Func<Func<ReadOnlyMemory<byte>, Task>, Task> pump, Func<ReadOnlyMemory<byte>, CancellationToken, Task> write, Func<ValueTask> release)
    {
        this.pump = pump;
        this.write = write;
        this.release = release;
    }

    /// <summary>
    /// DevTools over the WebSocket of a browser started with a loopback debugging port (the sign-in, <see cref="ChromiumLauncher.LaunchSignIn"/>): the same commands and events, one message
    /// to a WebSocket message. Closing it drops the socket; the caller ends the browser.
    /// </summary>
    public static CdpConnection OverWebSocket(WebSocket socket) => new(onMessage => PumpWebSocketAsync(socket, onMessage), (message, cancellationToken) => socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken).AsTask(),
        () =>
        {
            try { socket.Abort(); } catch (Exception) { }
            try { socket.Dispose(); } catch (Exception) { }
            return ValueTask.CompletedTask;
        });

    /// <summary>Events in arrival order; completed when the browser's end closes.</summary>
    public ChannelReader<CdpEvent> Events => events.Reader;

    /// <summary>Completes when the browser's end has closed (it exited, crashed, or the connection was disposed).</summary>
    public Task Closed => closed.Task;

    public void Start() => reader = Task.Run(ReadAsync);

    /// <summary>Sends one command and returns its <c>result</c>. A closed connection is an <see cref="IOException"/>; an error answer a <see cref="CdpException"/>.</summary>
    public async Task<JsonElement> SendAsync(string method, object? parameters = null, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        if (closed.Task.IsCompleted) throw new IOException("cdp_closed");
        var id = Interlocked.Increment(ref nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = answer;
        try
        {
            var message = sessionId is null
                ? JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { } })
                : JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters ?? new { }, sessionId });
            await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await write(message, cancellationToken).ConfigureAwait(false); }
            finally { writing.Release(); }
            // Closed meanwhile: the reader may have failed the pending set before this command joined it.
            if (closed.Task.IsCompleted) answer.TrySetException(new IOException("cdp_closed"));
            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ObjectDisposedException or NotSupportedException or IOException or WebSocketException)
        {
            throw new IOException("cdp_closed");
        }
        finally { pending.TryRemove(id, out _); }
    }

    private static async Task WritePipeAsync(Stream toBrowser, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        await toBrowser.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await toBrowser.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);
        await toBrowser.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PumpPipeAsync(Stream fromBrowser, Func<ReadOnlyMemory<byte>, Task> onMessage)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var read = await fromBrowser.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            var start = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != 0) continue;
                message.Write(buffer, start, index - start);
                start = index + 1;
                if (message.Length > 0) await onMessage(message.GetBuffer().AsMemory(0, (int)message.Length)).ConfigureAwait(false);
                message.SetLength(0);
            }
            message.Write(buffer, start, read - start);
            if (message.Length > MaxMessage) break;
        }
    }

    // One WebSocket message is one DevTools message, however the browser fragmented it; one over the cap ends the connection, as on the pipe.
    private static async Task PumpWebSocketAsync(WebSocket socket, Func<ReadOnlyMemory<byte>, Task> onMessage)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == buffer.Length)
                {
                    if (length >= MaxMessage) return;
                    Array.Resize(ref buffer, Math.Min(buffer.Length * 2, MaxMessage));
                }
                result = await socket.ReceiveAsync(buffer.AsMemory(length), CancellationToken.None).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                length += result.Count;
            } while (!result.EndOfMessage);
            if (length > 0) await onMessage(buffer.AsMemory(0, length)).ConfigureAwait(false);
        }
    }

    private async Task ReadAsync()
    {
        try { await pump(DispatchAsync).ConfigureAwait(false); }
        // However the browser's end went away (it exited, or this connection was disposed), everything waiting is told the same.
        catch (Exception failure) when (failure is IOException or ObjectDisposedException or JsonException or OperationCanceledException or InvalidOperationException or WebSocketException) { }
        finally
        {
            closed.TrySetResult();
            events.Writer.TryComplete();
            foreach (var waiting in pending.Values) waiting.TrySetException(new IOException("cdp_closed"));
        }
    }

    private async Task DispatchAsync(ReadOnlyMemory<byte> json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
        {
            if (!pending.TryGetValue(id, out var answer)) return;
            if (root.TryGetProperty("error", out var error))
                answer.TrySetException(new CdpException(error.TryGetProperty("code", out var code) && code.TryGetInt32(out var number) ? number : 0));
            else answer.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            return;
        }
        if (root.TryGetProperty("method", out var method) && method.GetString() is { } name)
        {
            var session = root.TryGetProperty("sessionId", out var sessionId) ? sessionId.GetString() : null;
            var parameters = root.TryGetProperty("params", out var given) ? given.Clone() : default;
            await events.Writer.WriteAsync(new CdpEvent(name, session, parameters)).ConfigureAwait(false);
        }
    }

    /// <summary>Closes this end; the reader ends when the browser's does (the caller ends the process).</summary>
    public async ValueTask DisposeAsync()
    {
        await release().ConfigureAwait(false);
        try { await reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        closed.TrySetResult();
        events.Writer.TryComplete();
        foreach (var waiting in pending.Values) waiting.TrySetException(new IOException("cdp_closed"));
        writing.Dispose();
    }
}
