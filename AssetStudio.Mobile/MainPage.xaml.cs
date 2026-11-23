using AssetStudio;

namespace AssetStudioMobile;

public partial class MainPage : ContentPage
{
    private readonly List<AssetListItem> _assets = new();
    private readonly List<string> _extractablePaths = new();
    private AssetListItem? _selectedAsset;
    private bool _isLoading;

    public MainPage()
    {
        InitializeComponent();
        AssetList.ItemsSource = _assets;
    }

    private async void OnImportClicked(object? sender, EventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Select a Unity asset file or an AssetBundle"
            });

            if (file is null)
            {
                return;
            }

            SetLoading(true, "Copying selected files…");
            var importDirectory = Path.Combine(FileSystem.CacheDirectory, "imports", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(importDirectory);

            var paths = new List<string>();
            var fileName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidOperationException("The selected file has no name.");
            }

            var destination = Path.Combine(importDirectory, fileName);
            await using (var source = await file.OpenReadAsync())
            await using (var target = File.Create(destination))
            {
                await source.CopyToAsync(target);
            }
            paths.Add(destination);

            SetLoading(true, "Loading assets…");
            var manager = new AssetsManager();
            var logger = new MobileLogger();
            var previousLogger = Logger.Default;
            try
            {
                await Task.Run(() =>
                {
                    Logger.Default = logger;
                    try
                    {
                        manager.LoadFiles(paths.ToArray());
                    }
                    finally
                    {
                        Logger.Default = previousLogger;
                    }
                });
            }
            catch
            {
                Logger.Default = previousLogger;
                throw;
            }

            var items = manager.assetsFileList
                .SelectMany(file => file.Objects)
                .Select(asset => new AssetListItem(asset))
                .ToList();

            _extractablePaths.Clear();
            _extractablePaths.AddRange(paths.Where(IsExtractableArchive));
            _selectedAsset = null;
            ExportButton.IsEnabled = false;
            PreviewButton.IsEnabled = false;
            SelectionLabel.Text = "Select an asset to share its raw data.";
            SearchBar.Text = string.Empty;
            _assets.Clear();
            _assets.AddRange(items);
            AssetList.ItemsSource = null;
            AssetList.ItemsSource = _assets;

            StatusLabel.Text = items.Count > 0
                ? $"Loaded {items.Count:N0} assets from {manager.assetsFileList.Count:N0} serialized file(s)."
                : GetLoadFailureMessage(logger);
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Import failed", exception.Message, "OK");
            StatusLabel.Text = "Import failed.";
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = e.NewTextValue?.Trim() ?? string.Empty;
        var filtered = string.IsNullOrEmpty(query)
            ? _assets
            : _assets.Where(asset =>
                asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || asset.Type.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        AssetList.ItemsSource = filtered;
    }

    private void OnAssetSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selectedAsset = e.CurrentSelection.FirstOrDefault() as AssetListItem;
        ExportButton.IsEnabled = _selectedAsset is not null && !_isLoading;
        PreviewButton.IsEnabled = _selectedAsset?.Asset is Mesh or Texture2D && !_isLoading;
        SelectionLabel.Text = _selectedAsset is null
            ? "Select an asset to share its raw data."
            : $"{_selectedAsset.Type} • path ID {_selectedAsset.PathId}";
    }

    private async void OnPreviewClicked(object? sender, EventArgs e)
    {
        var selected = _selectedAsset;
        if (selected is null || _isLoading)
        {
            return;
        }

        try
        {
            if (selected.Asset is Mesh mesh)
            {
                await Navigation.PushModalAsync(new NavigationPage(new MeshPreviewPage(mesh)));
            }
            else if (selected.Asset is Texture2D texture)
            {
                SetLoading(true, "Preparing texture preview…");
                var imagePath = await Task.Run(() => MobileAssetExporter.Export(texture));
                await Navigation.PushModalAsync(new NavigationPage(new TexturePreviewPage(texture.m_Name, imagePath)));
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Preview failed", exception.Message, "OK");
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async void OnExtractClicked(object? sender, EventArgs e)
    {
        if (_isLoading || _extractablePaths.Count == 0)
        {
            return;
        }

        try
        {
            SetLoading(true, "Unpacking selected bundles…");
            var archivePath = await Task.Run(() => MobileBundleExtractor.CreateArchive(_extractablePaths));
            await Share.Default.RequestAsync(new ShareFileRequest(
                $"Share {Path.GetFileName(archivePath)}",
                new ShareFile(archivePath)));
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Unpack failed", exception.Message, "OK");
        }
        finally
        {
            SetLoading(false);
        }
    }

    private async void OnExportClicked(object? sender, EventArgs e)
    {
        var selected = _selectedAsset;
        if (selected is null || _isLoading)
        {
            return;
        }

        try
        {
            SetLoading(true, "Preparing asset…");
            var filePath = await Task.Run(() => MobileAssetExporter.Export(selected.Asset));

            await Share.Default.RequestAsync(new ShareFileRequest(
                $"Share {Path.GetFileName(filePath)}",
                new ShareFile(filePath)));
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Export failed", exception.Message, "OK");
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void SetLoading(bool isLoading, string? status = null)
    {
        _isLoading = isLoading;
        ImportButton.IsEnabled = !isLoading;
        ExportButton.IsEnabled = !isLoading && _selectedAsset is not null;
        ExtractButton.IsEnabled = !isLoading && _extractablePaths.Count > 0;
        PreviewButton.IsEnabled = !isLoading && _selectedAsset?.Asset is Mesh or Texture2D;
        if (status is not null)
        {
            StatusLabel.Text = status;
        }
    }

    private static string GetLoadFailureMessage(MobileLogger logger)
    {
        var lastError = logger.Errors.LastOrDefault();
        return lastError is null
            ? "No supported Unity assets were found in the selected files."
            : $"No assets loaded. {lastError}";
    }

    private static bool IsExtractableArchive(string path)
    {
        using var reader = new FileReader(path);
        return reader.FileType is FileType.BundleFile or FileType.WebFile or FileType.ZipFile;
    }

    private sealed class AssetListItem
    {
        public AssetListItem(AssetStudio.Object asset)
        {
            Asset = asset;
            Type = asset.type.ToString();
            PathId = asset.m_PathID.ToString();
            Name = asset is NamedObject namedObject && !string.IsNullOrWhiteSpace(namedObject.m_Name)
                ? namedObject.m_Name
                : $"{Type} #{PathId}";
        }

        public AssetStudio.Object Asset { get; }
        public string Name { get; }
        public string Type { get; }
        public string PathId { get; }
    }

    private sealed class MobileLogger : ILogger
    {
        public List<string> Errors { get; } = new();

        public void Log(LoggerEvent loggerEvent, string message)
        {
            if (loggerEvent == LoggerEvent.Error)
            {
                Errors.Add(message);
            }
        }
    }
}
