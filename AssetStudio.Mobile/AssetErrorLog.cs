using Microsoft.Maui.Storage;

namespace AssetStudioMobile;

/// <summary>
/// Persistent on-device log for errors that the Debug -> Show error message
/// option would display. Errors are written even when popups are turned off so
/// the log is complete, and the folder survives app restarts.
/// </summary>
public static class AssetErrorLog
{
    private const long MaxLogBytes = 1_000_000;
    private static readonly object Sync = new();

    private static string DocumentsRoot
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return string.IsNullOrWhiteSpace(documents) ? FileSystem.AppDataDirectory : documents;
        }
    }

    public static string ErrorsPath { get; } = Path.Combine(DocumentsRoot, "AssetStudio", "Errors");

    public static string LogFilePath => Path.Combine(ErrorsPath, "error-log.txt");

    public static void Write(string title, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(ErrorsPath);
                var header = string.IsNullOrWhiteSpace(title) ? "AssetStudio" : title.Trim();
                var text = message.Trim();
                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {header}: {text}{Environment.NewLine}";

                if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > MaxLogBytes)
                {
                    // Keep the newest entries instead of letting the log grow forever.
                    var lines = File.ReadAllLines(LogFilePath);
                    var keep = lines.Skip(Math.Max(0, lines.Length - 200)).ToList();
                    File.WriteAllLines(LogFilePath, keep);
                }

                File.AppendAllText(LogFilePath, entry);
            }
        }
        catch
        {
            // Logging must never break the operation that produced the error.
        }
    }
}
