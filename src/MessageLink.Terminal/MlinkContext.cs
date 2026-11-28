using MessageLink.Core.Services;

namespace MessageLink.Terminal;

/// <summary>
/// What every part of mlink is given: its own folder (preferences, the saved server, the replica of the server's shared rows), the protector its files
/// are sealed with (DPAPI for this Windows user; a key file only this user can read elsewhere), its log, its version, and plain text in and out for
/// what is said before the full terminal starts (joining, a server that cannot be reached).
/// </summary>
/// <param name="Folder">mlink's own folder (<see cref="Connection.MlinkPaths"/>); made, and private to this user, before this is built.</param>
/// <param name="Log">mlink.log: fixed words, counts and one-way tags only. Never a token, a message, a name or a number.</param>
/// <param name="Ask">Asks a question on a line of its own and returns the answer, or null at end of input. Used before the full terminal starts.</param>
public sealed record MlinkContext(string Folder, IStateProtector Protector, Action<string> Log, string Version, TextWriter Out, Func<string, string?> Ask)
{
    /// <summary>The client word this program says in its hello: the server's page names the device by it.</summary>
    public const string ClientWord = "terminal";
}
