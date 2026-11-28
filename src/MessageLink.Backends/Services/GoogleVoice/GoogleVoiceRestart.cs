using MessageLink.App.Services.GoogleMessages;

namespace MessageLink.App.Services.GoogleVoice;

// When a Voice account whose helper stopped, or whose realtime channel gave up, is started again. A restart does only
// what launch does (hello, login, connect and a list read); nothing sent is ever repeated.
internal static class GoogleVoiceRestart
{
    // 15 s, 30 s, 1 and 2 minutes, then every 5 minutes for as long as the account stays signed in here: the same pacing
    // as a failed Google Messages connection.
    public static TimeSpan Pause(int attempt) => GoogleReconnect.Pause(attempt);

    // Why a restart must not go ahead, checked before each attempt; null means go ahead. An account signed out, replaced
    // or shut down is gone, and one that connected again by itself (a read that recovered, or a renewed sign-in handed
    // over, restarts realtime) must not be replaced by a fresh helper.
    public static string? Refusal(bool listed, bool disposed, bool connected) =>
        disposed || !listed ? "gone" : connected ? "connected" : null;
}

// One recovery at a time per account. A browser recovery after Google refused the session and a restart after the helper
// stopped both replace the account, so whichever begins first owns it until it ends and the other does not start.
internal sealed class RecoveryOwnership
{
    private int owned;
    public bool TryBegin() => Interlocked.CompareExchange(ref owned, 1, 0) == 0;
    public void End() => Interlocked.Exchange(ref owned, 0);
    public bool IsOwned => Volatile.Read(ref owned) != 0;
}
