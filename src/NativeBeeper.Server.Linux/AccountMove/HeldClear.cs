using System.Text.Json;
using NativeBeeper.Host;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// <c>messages-server --clear-held</c> (SWITCHBOARD.md stage 6 slice 13, decision 3): after a move back the server keeps a hold on the accounts that moved away, and refuses to
/// restore, connect, give encryption to or sign in Messenger and Instagram until a new move arrives or this is run. It forgets the hold and nothing else; no account, key, session or
/// message is touched, and it removes the marker files the hold left in the accounts' folders. After it the server will sign in whatever sessions it still holds, so it is for the one
/// case where the PC no longer runs those accounts. Only while the server is stopped (the lease the server holds), only with a person typing, and only after the word
/// <see cref="Confirmation"/> is typed exactly.
/// </summary>
internal static class HeldClear
{
    public const string Argument = "--clear-held";
    public const string Confirmation = "CLEAR";
    public const string Done = "done", Refused = "refused", Failed = "failed";

    internal sealed record Outcome(int ExitCode, string Word, string Reason, int Cleared = 0);

    public static Outcome Run(string root, string keysRoot, Func<string, HostLease?> acquire, bool interactive, TextReader input, TextWriter output)
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
            var paths = new AccountMovePaths(root, keysRoot);
            HeldFileContent? content;
            try
            {
                content = File.Exists(paths.HeldFile) ? JsonSerializer.Deserialize<HeldFileContent>(File.ReadAllBytes(paths.HeldFile), new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException)
            {
                output.WriteLine("The record of the accounts that moved away could not be read, so nothing was changed. If you are sure they should run here again, delete Server/account-held.json in the data folder by hand.");
                return new(3, Refused, "unreadable");
            }
            if (content?.Held is not { Count: > 0 } held)
            {
                output.WriteLine("No account is held. Nothing was changed.");
                return new(0, Done, "none");
            }
            if (!interactive)
            {
                output.WriteLine("This needs a person at a terminal to confirm it, so nothing was changed. Run it with an interactive terminal (docker run -it).");
                return new(3, Refused, "not_interactive");
            }
            output.WriteLine($"The server is holding {(held.Count == 1 ? "1 account" : $"{held.Count} accounts")} that moved back to your PC ({string.Join(" and ", held.Keys.Order(StringComparer.Ordinal))}).");
            output.WriteLine("While they are held the server will not sign them in, so that your PC and the server are never both signed in to the same account.");
            output.WriteLine("Clearing the hold lets the server sign in whatever it still holds of them the next time it starts. Only do this if your PC no longer runs them.");
            output.Write($"Type {Confirmation} to clear the hold, or anything else to stop: ");
            output.Flush();
            var typed = input.ReadLine();
            if (typed is null || !string.Equals(typed.Trim(), Confirmation, StringComparison.Ordinal))
            {
                output.WriteLine("Nothing was changed.");
                return new(3, Refused, "not_confirmed");
            }
            try
            {
                File.Delete(paths.HeldFile);
                foreach (var folder in AccountFolders(paths))
                {
                    var marker = Path.Combine(folder, AccountMovePaths.SentinelFile);
                    if (File.Exists(marker)) File.Delete(marker);
                }
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                output.WriteLine("The hold could not be removed, so this is not finished. Check the data folder, then run it again.");
                return new(3, Failed, "files");
            }
            if (!Directory.Exists(keysRoot))
                output.WriteLine("The keys folder was not mounted, so the marker files in it were not removed. If Messenger will not sign in afterwards, run this again with the keys volume mounted (-v messages-server-keys:/keys).");
            output.WriteLine("Done. The hold is cleared. Start the server.");
            return new(0, Done, "none", held.Count);
        }
    }

    private static IEnumerable<string> AccountFolders(AccountMovePaths paths)
    {
        foreach (var accounts in new[] { Path.Combine(paths.LiveMessengerPrivateRoot, "accounts"), Path.Combine(paths.LiveInstagramRoot, "accounts") })
            if (Directory.Exists(accounts))
                foreach (var folder in Directory.EnumerateDirectories(accounts)) yield return folder;
    }

    public static string LogLine(Outcome outcome) => $"held clear outcome={outcome.Word} reason={outcome.Reason} accounts={outcome.Cleared}";
}
