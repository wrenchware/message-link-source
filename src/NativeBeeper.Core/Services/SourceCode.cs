using System.Text.RegularExpressions;

namespace NativeBeeper.Core.Services;

// Where the source of a shipped build is published (the AGPL offer for the helpers and the server, docs/release.md
// "Source offer"). tools\Publish-Source.ps1 tags every publish: v<version> for a Windows release, src-<12 hex> for the
// commit a server image was built from. A build that has neither tag yet (a development copy) still gets the repository.
public static partial class SourceCode
{
    public const string Repository = "https://github.com/wrenchware/message-link-source";

    public static string For(string? version = null, string? revision = null)
    {
        if (revision is { } commit && CommitWord().IsMatch(commit)) return Repository + "/tree/src-" + commit[..12];
        if (version is { } release && VersionWord().IsMatch(release)) return Repository + "/tree/v" + release;
        return Repository;
    }

    [GeneratedRegex(@"^[0-9a-f]{12,40}\z")]
    private static partial Regex CommitWord();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+\z")]
    private static partial Regex VersionWord();
}
