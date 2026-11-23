using AssetStudio;
using System.Reflection;

namespace AssetStudioMobile;

/// <summary>Connects persistent mobile files to the original AssetStudio parser.</summary>
public sealed class AssetStudioMobileLoader : IDisposable
{
    private readonly AssetsManager _manager = new();
    private bool _disposed;

    public async Task LoadFilesAsync(
        IReadOnlyList<FileResult> selected,
        IProgress<MainPage.AssetLoadProgress> progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(selected);
        var paths = selected
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.FullPath) && File.Exists(file.FullPath))
            .Select(file => file.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length == 0)
            throw new FileNotFoundException("No readable files were selected from the AssetStudio library.");

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new MainPage.AssetLoadProgress(5, $"Preparing {paths.Length} file(s)…"));

        // AssetsManager is synchronous and performs CPU/disk-heavy parsing. Keep it off the UI thread.
        var rows = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _manager.Clear();
            _manager.LoadFiles(paths);
            cancellationToken.ThrowIfCancellationRequested();

            var parsed = _manager.assetsFileList
                .SelectMany(file => file.Objects.Select(asset => ToAssetRow(asset, file.fileName)))
                .ToList();

            progress?.Report(new MainPage.AssetLoadProgress(95, $"Parsed {parsed.Count} Unity asset(s)…"));
            return parsed;
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        MainThread.BeginInvokeOnMainThread(() => MainPageHost?.Invoke(rows));
        progress?.Report(new MainPage.AssetLoadProgress(100, $"Loaded {rows.Count} Unity asset(s)"));
    }

    // MainPage installs this callback so this service does not own UI controls.
    public Action<IEnumerable<MainPage.AssetRow>>? MainPageHost { get; set; }

    private static MainPage.AssetRow ToAssetRow(AssetStudio.Object asset, string sourceFile)
    {
        var typeName = asset.type.ToString();
        var name = ReadAssetName(asset) ?? typeName;
        return MainPage.CreateAssetRow(
            asset,
            name,
            sourceFile,
            typeName,
            asset.m_PathID,
            asset.byteSize,
            Convert.ToInt32(asset.type),
            sourceFile);
    }

    private static string? ReadAssetName(AssetStudio.Object asset)
    {
        // Most Unity named objects expose a public m_Name field. Use reflection
        // so all other object classes can still be listed without special cases.
        for (Type? type = asset.GetType(); type != null; type = type.BaseType)
        {
            var field = type.GetField("m_Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field?.GetValue(asset) is string fieldValue && !string.IsNullOrWhiteSpace(fieldValue))
                return fieldValue;
            var property = type.GetProperty("m_Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (property?.GetValue(asset) is string propertyValue && !string.IsNullOrWhiteSpace(propertyValue))
                return propertyValue;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _manager.Clear();
        _disposed = true;
    }
}
