using System.Globalization;

namespace NativeBeeper.Presentation.Services;

// A file that came in a message, on its way into the user's own folders. Its name is the sender's to choose, so it is
// cleaned before it is shown or suggested, and the saved file is marked as from the internet, as a browser marks a download.
public static class ReceivedFiles
{
    // Zone 3 is the Internet zone: Windows then applies SmartScreen, Office's Protected View and the "came from another
    // computer" unblock box, as it does to a download. No address is recorded; where the file came from stays in the conversation.
    private const string InternetZone = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>Marks a saved file as from the internet (the Mark of the Web), on the file before its final move so the mark
    /// travels with it. A volume with no alternate data streams (FAT, exFAT) leaves it unmarked; the save never fails for this.</summary>
    public static void MarkFromInternet(string path)
    {
        try { File.WriteAllText(path + ":Zone.Identifier", InternetZone); }
        catch (Exception) { }
    }

    /// <summary>The name as it may be shown: without Unicode format characters (bidi overrides and isolates, zero-width
    /// characters), one of which makes "Invoice_[RLO]fdp.exe" read as "Invoice_exe.pdf".</summary>
    public static string Visible(string? name) =>
        string.IsNullOrEmpty(name) ? "" : name.Any(IsFormat) ? string.Concat(name.Where(c => !IsFormat(c))) : name;
    private static bool IsFormat(char c) => char.GetUnicodeCategory(c) == UnicodeCategory.Format;

    /// <summary>What a file with no name of its own is called: a save suggests "Attachment.zip".</summary>
    public const string Unnamed = "Attachment";
    private const int NameLimit = 100;

    /// <summary>The name a save suggests: the visible name with no folders and nothing Windows refuses in a file name, and an
    /// extension from the type when the name has none. A name that is only the service's own identifier (Google Messages
    /// names some files with a bare GUID) is no name: <paramref name="fallback"/> stands in for it, keeping its ending.</summary>
    public static string SaveName(string? name, string? mime, string fallback)
    {
        var saved = Clean(name);
        var ending = Ending(saved) is null ? "" : Path.GetExtension(saved);
        var stem = saved[..^ending.Length];
        if (IsPlaceholderStem(stem)) stem = fallback;
        // A sender's name of any length is cut to a hundred characters (never through a surrogate pair): the save dialog
        // refuses a name past MAX_PATH, and the phone keeps the same name in a file system that holds 255 bytes.
        if (stem.Length > NameLimit) stem = stem[..(char.IsHighSurrogate(stem[NameLimit - 1]) ? NameLimit - 1 : NameLimit)].TrimEnd('.', ' ');
        return ending.Length > 0 ? stem + ending : stem + (Extension(mime) ?? "");
    }

    /// <summary>Whether the sender gave no name a person wrote: none, only an ending, or one of the service's identifiers.</summary>
    public static bool IsPlaceholder(string? name)
    {
        var saved = Clean(name);
        return IsPlaceholderStem(Ending(saved) is null ? saved : saved[..^Path.GetExtension(saved).Length]);
    }
    private static bool IsPlaceholderStem(string stem) => string.IsNullOrWhiteSpace(stem) || RecordingLabel.IsIdentifier(stem.Trim());

    /// <summary>The name a file card shows: the visible name, or for a placeholder what sort of file it is ("ZIP file") by the
    /// ending its save gives it, so the card never calls a file something its saved copy is not.</summary>
    public static string Shown(string? name, string? mime) =>
        !IsPlaceholder(name) ? Visible(name) : Ending(SaveName(name, mime, Unnamed)) is { } ending ? ending.ToUpperInvariant() + " file" : Unnamed;

    /// <summary>The ending Windows opens a saved name by, lower case and without its dot: one that reads as an extension, or one
    /// Windows runs (".settingcontent-ms" among them). Null when the name has none; the save dialog then offers all files.</summary>
    public static string? Ending(string? saved) => AttachmentLabel.Extension(saved) ?? AttachmentLabel.Runnable(saved);

    // The visible name with no folders, nothing Windows refuses, and none of the trailing dots and spaces Windows drops.
    private static string Clean(string? name) =>
        string.Concat(Path.GetFileName(Visible(name)).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');

    /// <summary>A photo's name in a saved set. The set is saved without a dialog that shows each name, so an extension that is
    /// not a picture's ("IMG_2041.jpg.lnk") is replaced by the type's: a photo never lands as a shortcut or a program.</summary>
    public static string PhotoName(string? name, string? mime)
    {
        var saved = Clean(name);
        if (string.IsNullOrWhiteSpace(saved)) saved = "photo";
        if (Pictures.Contains(Path.GetExtension(saved))) return saved;
        // Only what reads as an extension is taken off ("Screenshot 2026.10.14" keeps its date); either way the type's is added.
        if (AttachmentLabel.Extension(saved) is not null) saved = saved[..^Path.GetExtension(saved).Length].TrimEnd('.', ' ');
        if (saved.Length == 0) saved = "photo";
        if (Pictures.Contains(Path.GetExtension(saved))) return saved;
        return saved + (Extension(mime) is { } typed && Pictures.Contains(typed) ? typed : ".bin");
    }

    // Every picture and clip the cards know by name, but SVG: a drawing that can carry script is not saved as a photo.
    private static readonly HashSet<string> Pictures = new(AttachmentLabel.ImageExtensions.Concat(AttachmentLabel.VideoExtensions)
        .Where(extension => extension != "svg").Select(extension => "." + extension), StringComparer.OrdinalIgnoreCase);

    // The ending a type names, for a name that has none: pictures, clips and recordings, then the documents, archives and data
    // people send. None is a type Windows runs, so a declared type never makes a file a program; a type not here
    // (application/octet-stream above all) adds nothing, and the name is saved as it came.
    private static string? Extension(string? mime) => mime?.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", "image/heic" => ".heic",
        "image/heif" => ".heif", "image/avif" => ".avif", "image/bmp" => ".bmp",
        "video/mp4" => ".mp4", "video/quicktime" => ".mov", "video/webm" => ".webm", "video/x-msvideo" => ".avi", "video/x-matroska" => ".mkv",
        "video/3gpp" => ".3gp", "video/3gpp2" => ".3g2", "video/mpeg" => ".mpg",
        "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", "audio/ogg" => ".ogg", "audio/wav" => ".wav", "audio/amr" => ".amr",
        "application/zip" or "application/x-zip-compressed" => ".zip", "application/x-7z-compressed" => ".7z",
        "application/vnd.rar" or "application/x-rar-compressed" => ".rar", "application/gzip" or "application/x-gzip" => ".gz",
        "application/x-tar" => ".tar", "application/x-bzip2" => ".bz2", "application/x-xz" => ".xz",
        "application/pdf" => ".pdf", "application/rtf" or "text/rtf" => ".rtf",
        "application/msword" => ".doc", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
        "application/vnd.ms-excel" => ".xls", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
        "application/vnd.ms-powerpoint" => ".ppt", "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
        "application/vnd.oasis.opendocument.text" => ".odt", "application/vnd.oasis.opendocument.spreadsheet" => ".ods",
        "application/vnd.oasis.opendocument.presentation" => ".odp",
        "text/plain" => ".txt", "text/csv" => ".csv", "text/markdown" => ".md", "text/html" => ".html",
        "text/calendar" => ".ics", "text/vcard" or "text/x-vcard" or "text/directory" => ".vcf",
        "application/json" => ".json", "application/xml" or "text/xml" => ".xml",
        "application/epub+zip" => ".epub", "application/vnd.android.package-archive" => ".apk",
        _ => null,
    };
}
