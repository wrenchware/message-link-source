using MessageLink.Core.Services;

namespace MessageLink.App.Services.Messenger;

internal sealed partial class MessengerBackend
{
    // The app's own close, awaited and bounded by the caller. It deliberately skips the gate DisposeAsync waits on: a
    // connect holds that gate through the whole sign-in, which is exactly the case where the helper most needs closing,
    // and waiting on it would spend the whole shutdown budget without ever closing the pipe. A connect caught in the
    // middle fails with "Helper disconnected", and `closing` keeps that from looking like a helper to recover.
    internal async Task ShutdownAsync()
    {
        closing = true;
        ready = false;
        var started = Interlocked.Exchange(ref helper, null);
        if (started is null) return;
        realtime.Writer.TryWrite(new(RealtimeKind.Disconnected));
        await started.DisposeAsync();
    }
}
