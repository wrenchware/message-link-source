namespace MessageLink.Server.Backup;

/// <summary>The steps a server backup goes through, as the page names them.</summary>
public static class ServerBackupSteps
{
    public const string Preparing = "preparing";
    public const string History = "history";
    public const string Archive = "archive";
    public const string Photos = "photos";
    public const string Messenger = "messenger";
    public const string Finishing = "finishing";
    public const string Copying = "copying";
}

/// <summary>
/// How far a running backup is, for the server page's progress bar. Each step weighs what it reads: the history, search archive and saved photos by the size of
/// their databases on disk, which their exported payloads roughly match; Messenger's history (paged from its helper, size unknown until the last page) a quarter
/// of those, and filling toward its end without reaching it; the sealing and, for a network share, the copy a small fixed part. Read across threads.
/// </summary>
internal sealed class ServerBackupProgress
{
    // Messenger's pages fill half their step by about this many bytes, then slow; never the whole of it.
    private const double MessengerHalf = 20 * 1024 * 1024;
    private readonly List<(string Step, double Weight, long Expected)> plan = [];
    private readonly double total;
    private volatile int at;
    private long bytes;

    public ServerBackupProgress(long history, long archive, long photos, bool messenger, bool copy)
    {
        var data = Math.Max(1d, history + archive + photos);
        plan.Add((ServerBackupSteps.Preparing, data * 0.03, 0));
        plan.Add((ServerBackupSteps.History, history, history));
        plan.Add((ServerBackupSteps.Archive, archive, archive));
        plan.Add((ServerBackupSteps.Photos, photos, photos));
        if (messenger) plan.Add((ServerBackupSteps.Messenger, data * 0.25, 0));
        plan.Add((ServerBackupSteps.Finishing, data * 0.04, 0));
        if (copy) plan.Add((ServerBackupSteps.Copying, data * 0.12, 0));
        total = plan.Sum(step => step.Weight);
    }

    /// <summary>The database sizes a plan weighs its steps by; a file that can't be read weighs nothing.</summary>
    public static long SizeOf(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>Starts a step; one the plan leaves out (Messenger's, when it isn't backed up) is ignored.</summary>
    public void Enter(string step)
    {
        var index = plan.FindIndex(entry => entry.Step == step);
        if (index < 0) return;
        Interlocked.Exchange(ref bytes, 0);
        at = index;
    }

    /// <summary>Bytes the current step has written.</summary>
    public void Moved(long count) => Interlocked.Add(ref bytes, count);

    public string Step => plan[at].Step;

    /// <summary>From 0 to 1; a step never shows done until the next one starts.</summary>
    public double Done
    {
        get
        {
            var index = at;
            var (_, weight, expected) = plan[index];
            var moved = (double)Interlocked.Read(ref bytes);
            var within = expected > 0 ? Math.Min(moved / expected, 0.97) : weight > 0 && plan[index].Step == ServerBackupSteps.Messenger ? 0.9 * moved / (moved + MessengerHalf) : 0;
            var before = 0d;
            for (var i = 0; i < index; i++) before += plan[i].Weight;
            return Math.Clamp((before + weight * within) / total, 0, 1);
        }
    }
}
