namespace MessageLink.App.Services.Instagram;

// Whether something asked of Instagram before it has connected is waiting or refused. Waiting while a helper is running
// (starting, or closed off for a restart) or something will reconnect it by itself (the launch restore, a recovery) and
// nobody has asked it to close; refused once Instagram has refused the session, whatever is still running, or once
// nothing will reconnect it. The rule a composer's "Instagram is reconnecting." already follows (GetSendPermissionAsync).
//
// Refused was the only answer: at the 0.1.363 launch (2026-10-02) a conversation opened before Instagram connected said
// to sign in to Instagram, and its photos failed with Retry, 1.5 s before it connected by itself.
internal static class InstagramWaiting
{
    internal static bool Waits(bool helperRunning, bool reconnectComing, bool closing, string? refusedWith) =>
        refusedWith is null && (helperRunning || reconnectComing && !closing);
}

// What a waiting Instagram answered, by kind, from the first answer until it connected or stopped waiting: one line when
// a spell starts and one when it ends, rather than one per picture. Counts, milliseconds and fixed words only.
internal sealed class InstagramWaitTally
{
    private readonly object gate = new();
    private int reads, media, sends, other;
    private long began;

    // The opening line when this is the first answer of a spell, otherwise null.
    internal string? Answered(string kind)
    {
        lock (gate)
        {
            var first = reads + media + sends + other == 0;
            switch (kind)
            {
                case "read": reads++; break;
                case "media": media++; break;
                case "send": sends++; break;
                default: other++; kind = "other"; break;
            }
            if (!first) return null;
            began = System.Diagnostics.Stopwatch.GetTimestamp();
            return $"instagram waiting first={kind}";
        }
    }

    // The closing line, or null when nothing waited. Then: connected, abandoned (nothing will reconnect it now), failed (a
    // start somebody asked for did not connect) or closing.
    internal string? Settle(string then)
    {
        lock (gate)
        {
            var total = reads + media + sends + other;
            if (total == 0) return null;
            var line = $"instagram waited answered={total} reads={reads} media={media} sends={sends} other={other} " +
                $"ms={(int)System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds} then={then}";
            reads = media = sends = other = 0;
            return line;
        }
    }
}
