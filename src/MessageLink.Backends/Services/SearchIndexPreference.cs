namespace MessageLink.App.Services;

// Whether older history downloads in the background for search. On unless the user turns it off.
internal static class SearchIndexPreference
{
    private static readonly string FilePath = MessageLink.App.Services.AppPaths.Combine("search-history-download.txt");

    public static bool Load()
    {
        try { return !File.Exists(FilePath) || File.ReadAllText(FilePath).Trim() != "false"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    public static void Save(bool enabled)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, enabled ? "true" : "false"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
