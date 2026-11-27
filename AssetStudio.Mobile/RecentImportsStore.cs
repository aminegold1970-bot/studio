using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetStudioMobile;

internal sealed record RecentImport(string Path, string DisplayName, bool IsFolder);

/// <summary>
/// Keeps the three most recently loaded files or folders. Entries point at the
/// persistent AssetDocumentLibrary cache copies, so they survive app restarts.
/// </summary>
internal static partial class RecentImportsStore
{
    private const string PreferenceKey = "AssetStudio.RecentImports";
    private const int MaximumEntries = 3;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyList<RecentImport> Load()
    {
        try
        {
            var json = Preferences.Get(PreferenceKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
                return Array.Empty<RecentImport>();

            var entries = JsonSerializer.Deserialize<List<RecentImport>>(json, SerializerOptions)
                ?? new List<RecentImport>();
            return entries
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
                .Where(entry => entry.IsFolder ? Directory.Exists(entry.Path) : File.Exists(entry.Path))
                .Take(MaximumEntries)
                .Select(entry => entry with { DisplayName = DisplayNameFor(entry.DisplayName) })
                .ToList();
        }
        catch
        {
            return Array.Empty<RecentImport>();
        }
    }

    public static void AddFiles(
        IEnumerable<FileResult> files,
        IReadOnlyList<string>? originalNames = null)
    {
        var entries = new List<RecentImport>();
        var index = 0;
        foreach (var file in files)
        {
            var originalName = originalNames != null && index < originalNames.Count
                ? originalNames[index]
                : file?.FileName;
            index++;
            if (file == null || string.IsNullOrWhiteSpace(file.FullPath))
                continue;
            entries.Add(new RecentImport(
                file.FullPath,
                DisplayNameFor(originalName ?? file.FullPath),
                false));
        }
        Add(entries);
    }

    public static void AddFolder(string path, string? originalName = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        Add(new[] { new RecentImport(path, DisplayNameFor(originalName ?? path), true) });
    }

    public static void Remove(RecentImport entry)
    {
        var entries = Load().Where(item => !string.Equals(item.Path, entry.Path, StringComparison.Ordinal)).ToList();
        Save(entries);
    }

    private static void Add(IEnumerable<RecentImport> additions)
    {
        var paths = new HashSet<string>(additions.Select(entry => entry.Path), StringComparer.Ordinal);
        var entries = Load()
            .Where(item => !paths.Contains(item.Path))
            .ToList();
        entries.InsertRange(0, additions);
        Save(entries.Take(MaximumEntries));
    }

    private static void Save(IEnumerable<RecentImport> entries) =>
        Preferences.Set(PreferenceKey, JsonSerializer.Serialize(entries.Take(MaximumEntries).ToList()));

    private static string DisplayNameFor(string path)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) ?? string.Empty;
        return CachedNamePattern().Replace(name, string.Empty);
    }

    // Older builds displayed cache names like 20261127123456_<guid>_hero.unity
    // or <guid>_hero.unity. Strip both layouts so saved recent entries recover
    // the original file name even before the app re-adds it.
    [GeneratedRegex(@"^(?:\d{14}_[0-9a-fA-F]{32}_|[0-9a-fA-F]{32}_)")]
    private static partial Regex CachedNamePattern();
}
