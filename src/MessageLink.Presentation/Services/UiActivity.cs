using System.Diagnostics;

namespace MessageLink.Presentation.Services;

// What the interface thread has been asked to do, and how long parts of it took, so a stall is attributed to the work
// that caused it rather than guessed at. Counts and durations only: nothing here records a conversation, a person or
// anything that was typed. Everything is written on the interface thread and read by the responsiveness monitor on the
// same thread, resetting as it is read, so plain fields are enough and no measurement costs a lock.
public static class UiActivity
{
    private static int keystrokes, chatRows, avatars, syncApplies, historyUpdates, emojiWarms, frames, painted, rebuilds;
    private static long composerTicks, typingTicks, draftTicks, emojiTicks, paintTicks, worstPaintTicks, pendingKeystroke, syncTicks, avatarTicks, rebuildTicks, historyTicks;
    // A keystroke is not finished when the code handling it returns; it is finished when a frame carrying it reaches
    // the screen. That wait is what a person feels as lag, and a thread that never stalls can still be late to paint.
    public static void Keystroke()
    {
        keystrokes++;
        if (pendingKeystroke == 0) pendingKeystroke = Stopwatch.GetTimestamp();
    }
    public static void Frame()
    {
        frames++;
        if (pendingKeystroke == 0) return;
        var waited = Stopwatch.GetTimestamp() - pendingKeystroke;
        pendingKeystroke = 0;
        painted++;
        paintTicks += waited;
        if (waited > worstPaintTicks) worstPaintTicks = waited;
    }
    public static void ChatRow() => chatRows++;
    public static void Avatar() => avatars++;
    public static void AvatarRead(long start) => avatarTicks += Stopwatch.GetTimestamp() - start;
    public static void SyncApply() => syncApplies++;
    // Applying a batch of realtime events. It runs on the interface thread and waits on the network while it does, so
    // this is elapsed time rather than time the thread was held; a stall recorded inside one says which half it was.
    public static void Sync(long start) => syncTicks += Stopwatch.GetTimestamp() - start;
    public static void HistoryUpdate() => historyUpdates++;
    // Recomputing a conversation. Held time, like Rebuild: every render walks the whole loaded history, builds a row
    // view model for each of up to a thousand messages to diff against the rows already on screen, and allocates two
    // lookups besides. Sending one message used to run four to six of them.
    public static void History(long start) { historyTicks += Stopwatch.GetTimestamp() - start; }
    // Recomputing the whole conversation list. Held time, not elapsed: it is all synchronous work on this thread, and
    // a rebuild that finds the collection already correct raises no CollectionChanged at all, so ChatRow cannot see it.
    // That is what an unattributed stall used to be.
    public static void Rebuild(long start) { rebuilds++; rebuildTicks += Stopwatch.GetTimestamp() - start; }
    public static long Start() => Stopwatch.GetTimestamp();
    // The composer's own reaction to a keystroke: the send button, the notices and the length hint.
    public static void Composer(long start) => composerTicks += Stopwatch.GetTimestamp() - start;
    // The typing path: the emoticon replacement and deciding whether to tell the other side anything.
    public static void Typing(long start) => typingTicks += Stopwatch.GetTimestamp() - start;
    // Scheduling the delayed draft save, which cancels and replaces the previous one on every keystroke.
    public static void Draft(long start) => draftTicks += Stopwatch.GetTimestamp() - start;
    // Drawing one emoji category ahead of the picker being opened on it. A whole category is drawn in one turn, and a
    // turn that has started cannot yield, so this is time a keystroke waits even though the work is low priority.
    public static long EmojiWarm(long start)
    {
        var spent = Stopwatch.GetTimestamp() - start;
        emojiWarms++;
        emojiTicks += spent;
        return (long)Stopwatch.GetElapsedTime(0, spent).TotalMilliseconds;
    }
    public readonly record struct Counts(int Keystrokes, int ChatRows, int Avatars, int SyncApplies, int HistoryUpdates,
        int EmojiWarms, long ComposerMs, long TypingMs, long DraftMs, long EmojiMs,
        int Frames, int Painted, long PaintMs, long WorstPaintMs, long SyncMs, long AvatarMs,
        int Rebuilds, long RebuildMs, long HistoryMs)
    {
        public bool Idle => Keystrokes == 0 && ChatRows == 0 && Avatars == 0 && SyncApplies == 0 && HistoryUpdates == 0
            && EmojiWarms == 0 && Rebuilds == 0;
        public long AveragePaintMs => Painted == 0 ? 0 : PaintMs / Painted;
        // Everything the thread was asked to do, in the order a stall line prints it. One place to add a counter, so a
        // new kind of work cannot be instrumented and then left out of the line that would have shown it.
        public string Report => $"keys={Keystrokes} rows={ChatRows} avatars={Avatars} sync={SyncApplies} " +
            $"history={HistoryUpdates} emoji={EmojiWarms} rebuilds={Rebuilds} composer_ms={ComposerMs} " +
            $"typing_ms={TypingMs} draft_ms={DraftMs} emoji_ms={EmojiMs} rebuild_ms={RebuildMs} " +
            $"history_ms={HistoryMs} sync_ms={SyncMs} avatar_ms={AvatarMs}";
        public Counts Plus(Counts other) => new(Keystrokes + other.Keystrokes, ChatRows + other.ChatRows,
            Avatars + other.Avatars, SyncApplies + other.SyncApplies, HistoryUpdates + other.HistoryUpdates,
            EmojiWarms + other.EmojiWarms, ComposerMs + other.ComposerMs, TypingMs + other.TypingMs,
            DraftMs + other.DraftMs, EmojiMs + other.EmojiMs, Frames + other.Frames, Painted + other.Painted,
            PaintMs + other.PaintMs, Math.Max(WorstPaintMs, other.WorstPaintMs), SyncMs + other.SyncMs, AvatarMs + other.AvatarMs,
            Rebuilds + other.Rebuilds, RebuildMs + other.RebuildMs, HistoryMs + other.HistoryMs);
    }
    public static Counts Take()
    {
        static long Ms(long ticks) => (long)Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds;
        var counts = new Counts(keystrokes, chatRows, avatars, syncApplies, historyUpdates, emojiWarms,
            Ms(composerTicks), Ms(typingTicks), Ms(draftTicks), Ms(emojiTicks),
            frames, painted, Ms(paintTicks), Ms(worstPaintTicks), Ms(syncTicks), Ms(avatarTicks),
            rebuilds, Ms(rebuildTicks), Ms(historyTicks));
        keystrokes = chatRows = avatars = syncApplies = historyUpdates = emojiWarms = frames = painted = rebuilds = 0;
        composerTicks = typingTicks = draftTicks = emojiTicks = paintTicks = worstPaintTicks = syncTicks = avatarTicks = rebuildTicks = historyTicks = 0;
        return counts;
    }
}
