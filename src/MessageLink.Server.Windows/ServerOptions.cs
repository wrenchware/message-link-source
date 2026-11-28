namespace MessageLink.Server.Windows;

/// <summary>
/// What MessageLinkServer.exe was started with. <c>--restore</c> is what lets the providers connect: without it the server holds its lease and its
/// window and serves nothing from any account, so it can be run on any data root, real or scratch, with no network effect.
/// </summary>
/// <param name="Quit">Ask the server of this data root to quit, and wait for it.</param>
/// <param name="Background">
/// The PC's own server, headless: started by a window of a root that runs its own server (it starts with Message Link only, owner 2026-11-22), or by mlink. It connects the accounts as
/// <c>--restore</c> does, paced by the restore ledger, and is nobody's to stop but <c>--quit</c> (the tray's "Quit Message Link and server"), its own icon's Quit Message Link Server and the end of the session.
/// </param>
/// <param name="Restore">Reconnect the saved accounts the way a launch of the app does.</param>
/// <param name="OwnerPid">The app that started this server and decides when it stops; recorded only.</param>
internal sealed record ServerOptions(bool Quit, bool Background, bool Restore, int? OwnerPid)
{
    /// <summary>Whether this start connects the accounts: <c>--restore</c>, or <c>--background</c>, which is the PC's own server.</summary>
    public bool Connects => Restore || Background;

    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        bool quit = false, background = false, restore = false;
        int? owner = null;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--quit": quit = true; break;
                case "--background": background = true; break;
                case "--restore": restore = true; break;
                case "--owner" when index + 1 < args.Count && int.TryParse(args[index + 1], out var pid) && pid > 0: owner = pid; index++; break;
            }
        }
        return new(quit, background, restore, owner);
    }
}
