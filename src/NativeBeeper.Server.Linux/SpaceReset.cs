using Microsoft.Data.Sqlite;
using NativeBeeper.Host;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// <c>messages-server --reset-space</c> (SWITCHBOARD.md stage 6 slice 13d, chosen as a fresh start on moving day): removes the server's shared state, so a
/// PC can move its own choices into an empty space and the server adopts that PC's key. It removes every row of <c>Server/shared.db</c> (a new file with a new epoch and
/// no rows takes its place, its <c>-wal</c> and <c>-shm</c> go) and <c>Server/space.protected</c> together, and nothing else: no session, no key, no paired device, no helper's
/// data, no log. The shared state is a copy of what the person chose, which the PC (the in-process stores, never deleted) holds the other way round, so this is the one place a
/// server's choices are thrown away, and it asks for it. The one thing the new file keeps is the <c>retired</c> table: a device whose answer to a scheduled create was lost
/// replays it later, and a space that had forgotten the message had gone would send it a second time.
/// </summary>
/// <remarks>
/// Only while the data folder's server is not running (it takes the same <see cref="HostLease"/> the server holds, so a running server, or another reset, refuses it),
/// and only with a person typing: standard input must be a terminal and the word <see cref="Confirmation"/> typed exactly. Anything else changes nothing. The new database
/// is made beside and moved into place whole, and the key goes last, so a run that stops half way leaves the old database or the new one with its record (and perhaps a key
/// over no rows, which the next reset finishes), never rows under no key or a list of retired ids lost. It prints plain
/// sentences, and a log line of fixed words and counts. Nothing is logged of what was in the files.
/// </remarks>
internal static class SpaceReset
{
    public const string Argument = "--reset-space";
    public const string Confirmation = "RESET";

    /// <summary>The fixed words a run ends with; the log line and the tests name them.</summary>
    public const string Done = "done", Refused = "refused", Failed = "failed";

    internal sealed record Outcome(int ExitCode, string Word, string Reason, int Removed = 0);

    /// <summary>The files this removes or replaces, relative to the data folder, in the order it takes them. Nothing else is ever touched.</summary>
    public static IReadOnlyList<string> Files { get; } =
    [
        Path.Combine("Server", SharedStateStore.FileName + "-wal"),
        Path.Combine("Server", SharedStateStore.FileName + "-shm"),
        Path.Combine("Server", SharedStateStore.FileName),
        Path.Combine("Server", SharedSpace.FileName),
    ];

    /// <param name="acquire">The server's own lease on the data folder (<see cref="HostLease.TryAcquire"/>); null means a server is running.</param>
    /// <param name="interactive">Whether standard input is a terminal with a person at it.</param>
    /// <param name="liveSends">How many scheduled messages and unsettled sends the shared state holds (<see cref="SharedStateStore.LiveSends"/>), null when it cannot be told.</param>
    /// <param name="retiredOf">The retired ids to carry over (<see cref="SharedStateStore.RetiredRows"/>), null when they cannot be told.</param>
    public static Outcome Run(string root, Func<string, HostLease?> acquire, bool interactive, TextReader input, TextWriter output, Func<string, (int Scheduled, int Unsettled)?>? liveSends = null,
        Func<string, IReadOnlyList<(string Id, long At)>?>? retiredOf = null)
    {
        HostLease? lease;
        try { lease = acquire(root); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            output.WriteLine("The data folder could not be locked, so nothing was changed.");
            return new(3, Refused, "lease");
        }
        if (lease is null)
        {
            output.WriteLine("The Messages Server for this data folder is running, so nothing was changed. Stop the server first, then run this again.");
            return new(3, Refused, "server_running");
        }
        using (lease)
        {
            if (!interactive)
            {
                output.WriteLine("This needs a person at a terminal to confirm it, so nothing was changed. Run it with an interactive terminal (docker run -it).");
                return new(3, Refused, "not_interactive");
            }
            // A message the server was asked to send later is not a choice to throw away: it goes, or is cancelled, from Messages first.
            if ((liveSends ?? SharedStateStore.LiveSends)(root) is not { } waiting || (retiredOf ?? SharedStateStore.RetiredRows)(root) is not { } retired)
            {
                output.WriteLine("The server's shared state could not be read, to check for scheduled messages and to keep the record of those already gone, so nothing was changed. If you are sure it holds none, move shared.db away by hand and run this again.");
                return new(3, Refused, "unreadable");
            }
            if (waiting.Scheduled + waiting.Unsettled > 0)
            {
                var parts = new List<string>();
                if (waiting.Scheduled > 0) parts.Add(waiting.Scheduled == 1 ? "1 scheduled message" : $"{waiting.Scheduled} scheduled messages");
                if (waiting.Unsettled > 0) parts.Add(waiting.Unsettled == 1 ? "1 send that may not have gone through" : $"{waiting.Unsettled} sends that may not have gone through");
                output.WriteLine($"The server still holds {string.Join(" and ", parts)}, so nothing was changed. Send or cancel the scheduled messages and dismiss the others from Messages first, then run this again.");
                return new(3, Refused, "sends_waiting");
            }
            output.WriteLine("This removes everything this server keeps of your choices: merges, labels, drafts, names, pins, saved messages and the other shared settings.");
            output.WriteLine("Sign-ins, keys, paired devices and the other devices' own data are not touched. Your PC keeps its own copy and can move it in again.");
            output.WriteLine("Only a list of scheduled messages that have already gone is kept, so that none of them can be sent a second time.");
            output.Write($"Type {Confirmation} to remove them, or anything else to stop: ");
            output.Flush();
            var typed = input.ReadLine();
            if (typed is null || !string.Equals(typed.Trim(), Confirmation, StringComparison.Ordinal))
            {
                output.WriteLine("Nothing was changed.");
                return new(3, Refused, "not_confirmed");
            }
            var removed = 0;
            var stopped = false;
            var database = Path.Combine(root, "Server", SharedStateStore.FileName);
            var beside = database + ".new";
            // The new file is whole before anything of the old one goes, so a stop at any point leaves the old database (whose -wal the retired read emptied into it)
            // or the new one with the record.
            try { if (File.Exists(database)) SharedStateStore.CreateFresh(beside, retired); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SqliteException)
            {
                try { File.Delete(beside); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                output.WriteLine("The server's shared state could not be made anew, so nothing was changed. Check the disk, then run this again.");
                return new(3, Failed, "create");
            }
            foreach (var file in Files)
            {
                var path = Path.Combine(root, file);
                try
                {
                    // The database is not deleted: the new one holding only the retired ids takes its place in one move, so there is no moment with rows but no
                    // retired record, or with the record gone. It goes after its -wal and -shm (a stale -wal beside a new file could be replayed into it) and before the key.
                    if (path == database)
                    {
                        if (!File.Exists(path)) continue;
                        File.Move(beside, path, overwrite: true);
                    }
                    else
                    {
                        if (!File.Exists(path)) continue;
                        File.Delete(path);
                    }
                    removed++;
                }
                // At the first file that stays: the key goes last, so what is left is never rows under no key.
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or SqliteException) { stopped = true; break; }
            }
            if (stopped)
            {
                try { File.Delete(beside); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
                output.WriteLine("Some files could not be removed, so this is not finished. Run it again once the server is stopped.");
                return new(3, Failed, "files", removed);
            }
            output.WriteLine(removed == 0 ? "There was nothing to remove: the server's shared state is already empty." : "Done. The server's shared state is empty. Start the server, then move your PC's choices in.");
            return new(0, Done, "none", removed);
        }
    }

    public static string LogLine(Outcome outcome) => $"space reset outcome={outcome.Word} reason={outcome.Reason} files={outcome.Removed}";
}
