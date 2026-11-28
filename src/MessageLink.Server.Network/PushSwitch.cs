namespace MessageLink.Server.Network;

/// <summary>
/// What became of two files the owner gave the server's page: pushes on (and whether Google's check could be made), or why nothing was changed: not the files,
/// two projects, no app for Messages, a key Google refused, Cloud Messaging's API turned off, a key whose account may not send, or not kept.
/// </summary>
internal enum PushInstall { On, OnUnchecked, NotAppSettings, NotServiceAccount, ProjectsDiffer, NoApp, KeyRefused, ApiDisabled, NotAllowed, NotSaved }

/// <summary>
/// The server's pushes as they are now: set up at the start from the push folder, or turned on later from the server's page without a restart
/// (<see cref="InstallAsync"/>), which keeps the two files in <see cref="Folder"/> for the next start. Every push goes through it, so one sent after the files are
/// replaced goes with the new project. Each install is announced (<see cref="Installed"/>), so open phones sign up at once; others ask on their next connection.
/// Logged: <c>push setup outcome=on|on_unchecked apps=</c> and <c>push setup outcome=refused reason=</c>; never a key, a project or a token.
/// </summary>
internal sealed class PushSwitch : IPushTransport
{
    /// <summary>
    /// The most either file may be: a service account key is about 2.4 KB, google-services.json less with a few apps. 30 KiB each keeps the whole form within
    /// ASP.NET's 64 KiB memory buffer, so no part of a key is ever written to a temporary file.
    /// </summary>
    public const int MaxFile = 30 * 1024;

    private readonly Action<string> log;
    private readonly HttpMessageHandler? handler;
    private readonly SemaphoreSlim installing = new(1, 1);
    private PushSetup? current;

    /// <param name="folder">Where the page keeps the files it is given; null when the page cannot set pushes up (a server with no place for them).</param>
    /// <param name="handler">What the transports it makes send through (the harness's stand-in for Google); null for the network.</param>
    public PushSwitch(PushSetup? setup, string? folder, Action<string> log, HttpMessageHandler? handler = null)
    {
        current = setup; Folder = folder; this.log = log; this.handler = handler;
    }

    /// <summary>The setup pushes go with now; null while they are off.</summary>
    public PushSetup? Current => Volatile.Read(ref current);

    /// <summary>Where the files are kept; null when they cannot be set up from the page.</summary>
    public string? Folder { get; }

    /// <summary>Once, the first time pushes go from off to on (the server starts its notifier then).</summary>
    public event Action? TurnedOn;

    /// <summary>The files were replaced with another Firebase project's: every phone's address belongs to the old one (the host forgets those made before the given moment; each phone signs up again).</summary>
    public event Action<DateTimeOffset>? ProjectChanged;

    /// <summary>After every install that turned pushes on or replaced the files (after <see cref="TurnedOn"/> and <see cref="ProjectChanged"/>): the server tells open phones.</summary>
    public event Action? Installed;

    public Task<PushOutcome> SendAsync(string token, string sealedPush, bool urgent, CancellationToken cancellationToken = default) =>
        Current?.Transport is { } transport ? transport.SendAsync(token, sealedPush, urgent, cancellationToken) : Task.FromResult(PushOutcome.Failed);

    // Passed through as a sync, or Firebase never sees the collapse key and a long run of sends queues one hint each (found 2026-10-19).
    public Task<PushOutcome> SendSyncAsync(string token, string sealedPush, bool urgent = false, CancellationToken cancellationToken = default) =>
        Current?.Transport is { } transport ? transport.SendSyncAsync(token, sealedPush, urgent, cancellationToken) : Task.FromResult(PushOutcome.Failed);

    public Task<PushResult> SendWithStatusAsync(string token, string sealedPush, bool urgent, bool sync, CancellationToken cancellationToken = default) =>
        Current?.Transport is { } transport ? transport.SendWithStatusAsync(token, sealedPush, urgent, sync, cancellationToken) : Task.FromResult(new PushResult(PushOutcome.Failed));

    /// <summary>
    /// Takes the text of <c>google-services.json</c> and of a service account key: checks they are one project's with an app for Messages, asks Google whether it
    /// takes the key, keeps both files and turns pushes on with them. A key Google refuses, and anything that cannot be kept, changes nothing. One at a time.
    /// </summary>
    public async Task<PushInstall> InstallAsync(string settings, string account, CancellationToken cancellationToken = default)
    {
        if (Folder is not { } folder) return Refused(PushInstall.NotSaved, "no_folder");
        await installing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var read = FirebaseTransport.Read(settings, account, log, handler is null ? null : new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) });
            switch (read.Problem)
            {
                case PushProblem.NotAppSettings: return Refused(PushInstall.NotAppSettings, "not_settings");
                case PushProblem.NotServiceAccount: return Refused(PushInstall.NotServiceAccount, "not_account");
                case PushProblem.ProjectsDiffer: return Refused(PushInstall.ProjectsDiffer, "projects_differ");
            }
            var setup = read.Setup!;
            var transport = (FirebaseTransport)setup.Transport;
            var kept = false;
            try
            {
                if (setup.Offer.Apps?.Any(app => FirebaseTransport.Packages.Contains(app.Package)) != true) return Refused(PushInstall.NoApp, "no_app");
                // Unreachable is no answer about the key: kept, so a moment without the internet does not turn the owner away, and said so on the page.
                var check = await transport.CheckAsync(cancellationToken).ConfigureAwait(false);
                if (check == PushKeyCheck.Refused) return Refused(PushInstall.KeyRefused, "key_refused");
                // Kept, these would turn pushes "on" with every one of them refused: said with the fix instead, and nothing changed.
                if (check == PushKeyCheck.ApiDisabled) return Refused(PushInstall.ApiDisabled, "api_disabled");
                if (check == PushKeyCheck.NotAllowed) return Refused(PushInstall.NotAllowed, "not_allowed");
                try { Keep(folder, settings, account); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return Refused(PushInstall.NotSaved, $"not_saved type={failure.GetType().Name}"); }
                kept = true;
                // Addresses made before this moment are the old project's; one a phone gives from now on is the new one's.
                var changedAt = DateTimeOffset.UtcNow;
                // The transport replaced is left to go out of use rather than disposed: a push may be on its way through it.
                var was = Interlocked.Exchange(ref current, setup);
                var outcome = check == PushKeyCheck.Accepted ? PushInstall.On : PushInstall.OnUnchecked;
                var changed = was is not null && was.Offer.ProjectId != setup.Offer.ProjectId;
                log($"push setup outcome={(outcome == PushInstall.On ? "on" : "on_unchecked")} apps={setup.Offer.Apps?.Count ?? 0} replaced={(was is null ? "no" : changed ? "other_project" : "yes")}");
                if (was is null) TurnedOn?.Invoke();
                if (changed) ProjectChanged?.Invoke(changedAt);
                Installed?.Invoke();
                return outcome;
            }
            finally
            {
                if (!kept) transport.Dispose();
            }
        }
        finally { installing.Release(); }
    }

    private PushInstall Refused(PushInstall outcome, string reason)
    {
        log($"push setup outcome=refused reason={reason}");
        return outcome;
    }

    // Both written whole under names of their own first, owner-only from their creation, then moved into place: a start never reads half a file. The folder is the
    // owner's alone too, since the key is in it.
    private static void Keep(string folder, string settings, string account)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(folder);
        else Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var written = new List<(string Temporary, string Path)>();
        // The settings as they were, put back if the key cannot follow them: a start never finds one project's settings beside another's key.
        var settingsPath = Path.Combine(folder, FirebaseTransport.AppSettingsFile);
        var before = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            foreach (var (name, text) in new[] { (FirebaseTransport.AppSettingsFile, settings), (FirebaseTransport.ServiceAccountFile, account) })
                written.Add((Written(Path.Combine(folder, name), System.Text.Encoding.UTF8.GetBytes(text)), Path.Combine(folder, name)));
            File.Move(written[0].Temporary, written[0].Path, overwrite: true);
            try { File.Move(written[1].Temporary, written[1].Path, overwrite: true); }
            catch (Exception) when (before is not null)
            {
                File.Move(Written(settingsPath, before), settingsPath, overwrite: true);
                throw;
            }
            catch (Exception)
            {
                File.Delete(settingsPath);
                throw;
            }
        }
        finally
        {
            foreach (var (temporary, _) in written) try { File.Delete(temporary); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
    }

    // `bytes` under a name of its own beside `path`, owner-only from its creation and on the disk before it is moved into place.
    private static string Written(string path, byte[] bytes)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(temporary, options);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return temporary;
    }
}
