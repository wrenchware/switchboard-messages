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

    /// <summary>The name a save suggests: the visible name with no folders and nothing Windows refuses in a file name, and an
    /// extension from the type when the name has none.</summary>
    public static string SaveName(string? name, string? mime, string fallback)
    {
        var saved = string.Concat(Path.GetFileName(Visible(name)).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(saved)) saved = fallback;
        return string.IsNullOrEmpty(Path.GetExtension(saved)) ? saved + (Extension(mime) ?? "") : saved;
    }

    /// <summary>A photo's name in a saved set. The set is saved without a dialog that shows each name, so an extension that is
    /// not a picture's ("IMG_2041.jpg.lnk") is replaced by the type's: a photo never lands as a shortcut or a program.</summary>
    public static string PhotoName(string? name, string? mime)
    {
        var saved = SaveName(name, null, "photo");
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

    private static string? Extension(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif", "image/heic" => ".heic",
        "image/heif" => ".heif", "image/avif" => ".avif", "image/bmp" => ".bmp",
        "video/mp4" => ".mp4", "video/quicktime" => ".mov", "video/webm" => ".webm", "video/x-msvideo" => ".avi", "video/x-matroska" => ".mkv",
        "video/3gpp" => ".3gp", "video/3gpp2" => ".3g2", "video/mpeg" => ".mpg",
        "audio/mpeg" => ".mp3", "audio/mp4" => ".m4a", "audio/ogg" => ".ogg", "audio/wav" => ".wav", "audio/amr" => ".amr",
        _ => null,
    };
}
