using Microsoft.Maui.Storage;
using System.Text.RegularExpressions;

namespace AssetStudioMobile;

/// <summary>
/// Persistent on-device log for errors that the Debug -&gt; Show error message
/// option would display. Each source file gets its own log in Documents/Errors,
/// so an error from a bundle can be traced back to that bundle. Errors are
/// written even when popups are turned off so the log is complete, and the
/// folder survives app restarts.
/// </summary>
public static class AssetErrorLog
{
    private const long MaxLogBytes = 1_000_000;
    private const int MaxFileNameLength = 180;
    private static readonly object Sync = new();
    private static bool _layoutChecked;

    private static string DocumentsRoot
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(documents) ? FileSystem.AppDataDirectory : documents;
        }
    }

    public static string ErrorsPath { get; } = Path.Combine(DocumentsRoot, "Errors");

    private static string LegacyRoot => Path.Combine(DocumentsRoot, "AssetStudio");

    public static void Write(string title, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        try
        {
            lock (Sync)
            {
                EnsureLayout();
                var logPath = GetLogPath(title, message);
                WriteEntry(logPath, title, message);
            }
        }
        catch
        {
            // Logging must never break the operation that produced the error.
        }
    }

    private static void EnsureLayout()
    {
        if (_layoutChecked) return;

        Directory.CreateDirectory(ErrorsPath);
        MigrateLegacyLogs();
        _layoutChecked = true;
    }

    private static void MigrateLegacyLogs()
    {
        var legacyLog = Path.Combine(LegacyRoot, "Errors", "error-log.txt");
        if (File.Exists(legacyLog))
        {
            var text = File.ReadAllText(legacyLog);
            File.AppendAllText(
                Path.Combine(ErrorsPath, "AssetStudio.txt"),
                text.EndsWith(Environment.NewLine, StringComparison.Ordinal)
                    ? text
                    : text + Environment.NewLine);
        }

        // Older builds kept the log one level deeper. Remove that folder now
        // so the app has only a top-level Errors folder under Documents.
        try
        {
            if (Directory.Exists(LegacyRoot))
                Directory.Delete(LegacyRoot, recursive: true);
        }
        catch
        {
            // The legacy folder may be open in Files.app; it will be removed
            // on a later launch.
        }
    }

    private static string GetLogPath(string title, string message)
    {
        var sourceName = ExtractSourceName(message) ?? SanitizeFileName(title);
        if (string.IsNullOrWhiteSpace(sourceName))
            sourceName = "AssetStudio";

        var fileName = sourceName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? sourceName
            : sourceName + ".txt";
        return Path.Combine(ErrorsPath, fileName);
    }

    private static string? ExtractSourceName(string message)
    {
        // AssetsManager reports the bundle path on a "Path ..." line. Older
        // entries may only have the serialized file name on an "Assets ..." line.
        var match = Regex.Match(message, @"(?m)^Path[ \t]+(?:""?)(.+?)(?:""?)[ \t]*\r?$");
        if (!match.Success)
            match = Regex.Match(message, @"(?m)^Assets[ \t]+(?:""?)(.+?)(?:""?)[ \t]*\r?$");
        if (!match.Success) return null;

        var candidate = match.Groups[1].Value.Trim();
        var name = Path.GetFileName(candidate);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string SanitizeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var chars = value.Trim().ToCharArray();
        var invalid = Path.GetInvalidFileNameChars();
        for (var index = 0; index < chars.Length; index++)
        {
            if (Array.IndexOf(invalid, chars[index]) >= 0
                || chars[index] is '\r' or '\n' or '\t')
            {
                chars[index] = '_';
            }
        }

        var result = new string(chars).Trim().TrimEnd('.', ' ');
        const int maxNameLength = MaxFileNameLength - 4; // leave room for ".txt"
        return result.Length > maxNameLength ? result[..maxNameLength] : result;
    }

    private static void WriteEntry(string logPath, string title, string message)
    {
        var header = string.IsNullOrWhiteSpace(title) ? "AssetStudio" : title.Trim();
        var text = message.Trim();
        var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {header}: {text}{Environment.NewLine}";

        if (File.Exists(logPath) && new FileInfo(logPath).Length > MaxLogBytes)
        {
            // Keep the newest entries instead of letting the log grow forever.
            var lines = File.ReadAllLines(logPath);
            var keep = lines.Skip(Math.Max(0, lines.Length - 200)).ToList();
            File.WriteAllLines(logPath, keep);
        }

        File.AppendAllText(logPath, entry);
    }
}
