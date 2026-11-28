namespace MessageLink.Presentation.Services;

public static class AttachmentLabel
{
    // A name with an extension this knows, or one Windows runs, says what it is by that extension: it is what Windows opens
    // the file by, where the type is only what the sender declared. An .exe that calls itself application/pdf says EXE,
    // never PDF. Any other extension ("Jane.Doe") leaves it to the type, as before.
    public static string Kind(string? name, string? mime)
    {
        if (Runnable(name) is { } runs) return runs.ToUpperInvariant();
        var extension = Extension(name);
        if (extension is not null && ByName(extension) is var known && known != FileCategory.Other)
            return extension == "pdf" ? "PDF" : known == FileCategory.Audio ? "Audio" : extension.ToUpperInvariant();
        if(mime?.Equals("application/pdf",StringComparison.OrdinalIgnoreCase)==true) return "PDF";
        if(mime?.StartsWith("audio/",StringComparison.OrdinalIgnoreCase)==true) return "Audio";
        return extension?.ToUpperInvariant() ?? "File";
    }

    // What Windows runs, installs, mounts or follows when the file is opened: programs, scripts, shortcuts, disk images, help
    // files. Read as Windows reads it, so the longer ones and those with a dash, which Extension passes over, count too.
    private static readonly HashSet<string> RunnableExtensions = new(StringComparer.Ordinal)
    {
        "exe", "scr", "com", "bat", "cmd", "ps1", "vbs", "vbe", "js", "jse", "wsf", "wsh", "hta", "msi", "msix", "appx", "appinstaller",
        "lnk", "url", "pif", "cpl", "reg", "jar", "iso", "img", "vhd", "vhdx", "dll", "sys", "chm", "scf", "application", "gadget", "msc",
        "ws", "psc1", "settingcontent-ms", "library-ms", "search-ms",
    };
    internal static string? Runnable(string? name)
    {
        var extension = Path.GetExtension((name ?? "").TrimEnd('.', ' ')).TrimStart('.').ToLowerInvariant();
        return RunnableExtensions.Contains(extension) ? extension : null;
    }

    // The extension Windows will open a file by, lower case, when the name has one worth the name: up to ten letters and
    // digits, at least one of them a letter, after the trailing dots and spaces Windows drops. "J. Smith" and
    // "Screenshot 2026.10.14" have none.
    internal static string? Extension(string? name)
    {
        var extension = Path.GetExtension((name ?? "").TrimEnd('.', ' ')).TrimStart('.');
        return extension.Length is > 0 and <= 10 && extension.All(char.IsAsciiLetterOrDigit) && extension.Any(char.IsAsciiLetter)
            ? extension.ToLowerInvariant() : null;
    }
    public static string Size(long? bytes) => bytes is null or <= 0 ? "" : bytes >= 1024*1024 ? $"{bytes/1048576d:0.#} MB" : bytes >= 1024 ? $"{bytes/1024d:0.#} KB" : $"{bytes} B";
    public static string Detail(string? name,string? mime,long? bytes) => string.Join(" · ",new[]{Kind(name,mime),Size(bytes)}.Where(s=>s.Length>0));

    // What sort of file this is, for the colour and symbol on its card. The extension decides first, because phones
    // often send documents as application/octet-stream. One Windows runs decides too, whatever it is, so "statement.scr"
    // that calls itself application/pdf never draws a PDF tile. The MIME type fills in for any other name: none, or an
    // extension this does not know, so "Jane.Doe" sent as text/x-vcard is still a contact card.
    public static FileCategory Category(string? name, string? mime)
    {
        var byName = Extension(name) is { } extension ? ByName(extension) : FileCategory.Other;
        if (byName != FileCategory.Other || Runnable(name) is not null) return byName;
        var type = (mime ?? "").ToLowerInvariant();
        return type switch
        {
            "application/pdf" => FileCategory.Pdf,
            "text/vcard" or "text/x-vcard" or "text/directory" => FileCategory.Contact,
            "text/calendar" => FileCategory.Calendar,
            "application/zip" or "application/x-zip-compressed" or "application/x-7z-compressed" or "application/vnd.rar" or "application/gzip" => FileCategory.Archive,
            "application/msword" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => FileCategory.Document,
            "application/vnd.ms-excel" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => FileCategory.Spreadsheet,
            "application/vnd.ms-powerpoint" or "application/vnd.openxmlformats-officedocument.presentationml.presentation" => FileCategory.Presentation,
            "application/vnd.android.package-archive" => FileCategory.App,
            _ when type.StartsWith("audio/", StringComparison.Ordinal) => FileCategory.Audio,
            _ when type.StartsWith("video/", StringComparison.Ordinal) => FileCategory.Video,
            _ when type.StartsWith("image/", StringComparison.Ordinal) => FileCategory.Image,
            _ when type.StartsWith("text/", StringComparison.Ordinal) => FileCategory.Text,
            _ => FileCategory.Other,
        };
    }

    // The pictures and clips this knows by name; ReceivedFiles keeps them when saving a set of photos.
    internal static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.Ordinal)
    { "jpg", "jpeg", "jpe", "jfif", "png", "gif", "heic", "heif", "webp", "bmp", "tif", "tiff", "svg", "avif", "ico", "raw", "dng" };
    internal static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.Ordinal)
    { "mp4", "m4v", "mov", "avi", "mkv", "webm", "3gp", "3g2", "wmv", "mpg", "mpeg", "m2ts", "mts" };

    private static FileCategory ByName(string extension) =>
        extension switch
        {
            "pdf" => FileCategory.Pdf,
            "doc" or "docx" or "docm" or "dot" or "dotx" or "odt" or "rtf" or "pages" or "wpd" => FileCategory.Document,
            "xls" or "xlsx" or "xlsm" or "xlsb" or "csv" or "tsv" or "ods" or "numbers" => FileCategory.Spreadsheet,
            "ppt" or "pptx" or "pptm" or "pps" or "ppsx" or "odp" or "key" => FileCategory.Presentation,
            "zip" or "rar" or "7z" or "tar" or "gz" or "tgz" or "bz2" or "xz" or "zst" or "cab" => FileCategory.Archive,
            "mp3" or "m4a" or "aac" or "wav" or "ogg" or "oga" or "opus" or "flac" or "amr" or "wma" or "aiff" or "mid" or "midi" => FileCategory.Audio,
            _ when VideoExtensions.Contains(extension) => FileCategory.Video,
            _ when ImageExtensions.Contains(extension) => FileCategory.Image,
            "txt" or "md" or "log" or "text" => FileCategory.Text,
            "json" or "xml" or "html" or "htm" or "css" or "js" or "ts" or "cs" or "py" or "java" or "c" or "cpp" or "h" or "sh" or "ps1"
                or "bat" or "yaml" or "yml" or "ini" or "toml" or "sql" or "go" or "rs" or "kt" or "swift" or "php" or "rb" => FileCategory.Code,
            "vcf" or "vcard" => FileCategory.Contact,
            "ics" or "ical" or "ifb" or "vcs" => FileCategory.Calendar,
            "apk" or "aab" or "exe" or "msi" or "msix" or "appx" or "dmg" or "pkg" or "ipa" => FileCategory.App,
            _ => FileCategory.Other,
        };
}

public enum FileCategory { Other, Pdf, Document, Spreadsheet, Presentation, Archive, Audio, Video, Image, Text, Code, Contact, Calendar, App }
