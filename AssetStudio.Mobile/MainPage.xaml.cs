using AssetStudio;
using System.Text;

namespace AssetStudioMobile;

public partial class MainPage : ContentPage
{
    private readonly List<AssetListItem> _assets = new();
    private readonly List<AssetListItem> _visibleAssets = new();
    private readonly List<HierarchyListItem> _hierarchy = new();
    private readonly List<ClassListItem> _classes = new();
    private readonly List<DocumentFileItem> _documentFiles = new();
    private readonly List<string> _extractablePaths = new();
    private AssetListItem? _selectedAsset;
    private bool _isLoading;
    private string _searchText = string.Empty;
    private string _selectedType = "All";
    private string _unityVersion = string.Empty;
    private bool _showAllAssets = true;
    private bool _showAssetInfo = true;
    private bool _previewEnabled = true;

    public MainPage()
    {
        InitializeComponent();
        AssetList.ItemsSource = _assets;
        HierarchyList.ItemsSource = _hierarchy;
        ClassList.ItemsSource = _classes;
        DocumentsList.ItemsSource = _documentFiles;
        SetActiveSection("Files");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            RefreshDocuments();
        }
        catch (Exception exception)
        {
            StatusLabel.Text = "Unable to read the app's Documents folder.";
            await DisplayAlertAsync("Documents unavailable", exception.Message, "OK");
        }
    }

    private async void OnRefreshDocumentsClicked(object? sender, EventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            RefreshDocuments();
            StatusLabel.Text = _documentFiles.Count == 0
                ? "No files found in AssetStudio Documents. Copy files there using the iPhone Files app."
                : $"Found {_documentFiles.Count:N0} file(s) in AssetStudio Documents.";
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Documents unavailable", exception.Message, "OK");
            StatusLabel.Text = "Unable to read the app's Documents folder.";
        }
    }

    private async void OnFileMenuClicked(object? sender, EventArgs e)
    {
        var refresh = "Refresh Documents";
        var showFiles = "Show Documents";
        var unpack = "Unpack loaded bundle";
        var action = await DisplayActionSheetAsync("File", "Cancel", null, refresh, showFiles, unpack);
        if (action == refresh)
        {
            OnRefreshDocumentsClicked(sender, e);
        }
        else if (action == showFiles)
        {
            SetActiveSection("Files");
        }
        else if (action == unpack)
        {
            OnExtractClicked(sender, e);
        }
    }

    private async void OnOptionsMenuClicked(object? sender, EventArgs e)
    {
        const string toggleAll = "Toggle display all assets";
        const string toggleInfo = "Toggle asset information";
        const string togglePreview = "Toggle preview";
        const string setVersion = "Specify Unity version";
        var action = await DisplayActionSheetAsync(
            "Options",
            "Cancel",
            null,
            toggleAll,
            toggleInfo,
            togglePreview,
            setVersion);
        if (action == toggleAll)
        {
            _showAllAssets = !_showAllAssets;
            ApplyFilters();
            StatusLabel.Text = _showAllAssets
                ? "Showing all loaded asset types."
                : "Showing assets with a mobile conversion.";
        }
        else if (action == toggleInfo)
        {
            _showAssetInfo = !_showAssetInfo;
            ApplyFilters();
            StatusLabel.Text = _showAssetInfo
                ? "Asset source and size details are visible."
                : "Asset source and size details are hidden.";
        }
        else if (action == togglePreview)
        {
            _previewEnabled = !_previewEnabled;
            if (!_previewEnabled && PreviewPanel.IsVisible)
            {
                SetActiveSection("Assets");
            }
            UpdateSelection();
            StatusLabel.Text = _previewEnabled ? "Asset previews are enabled." : "Asset previews are disabled.";
        }
        else if (action == setVersion)
        {
            var version = await DisplayPromptAsync(
                "Unity version",
                "Enter the version to use when loading files, or leave empty for auto-detection.",
                "Save",
                "Cancel",
                "Example: 2022.3.0f1",
                40,
                Keyboard.Default,
                _unityVersion);
            if (version is not null)
            {
                _unityVersion = version.Trim();
                StatusLabel.Text = string.IsNullOrEmpty(_unityVersion)
                    ? "Unity version override cleared; auto-detection will be used on the next load."
                    : $"Unity version override set to {_unityVersion}; reload a file to apply it.";
            }
        }
    }

    private async void OnModelMenuClicked(object? sender, EventArgs e)
    {
        const string preview = "Preview selected model";
        const string exportSelected = "Export selected mesh as OBJ";
        const string exportVisible = "Export visible meshes as OBJ";
        const string fbx = "FBX / animation export";
        var action = await DisplayActionSheetAsync("Model", "Cancel", null, preview, exportSelected, exportVisible, fbx);
        if (action == preview)
        {
            OnPreviewClicked(sender, e);
        }
        else if (action == exportSelected)
        {
            if (_selectedAsset?.Asset is Mesh)
            {
                await ShareAssetAsync(_selectedAsset, MobileExportKind.Converted);
            }
            else
            {
                await DisplayAlertAsync("No mesh selected", "Select a Mesh asset first. OBJ export is available for meshes; FBX and animation export are not supported on iOS.", "OK");
            }
        }
        else if (action == exportVisible)
        {
            await ShareAssetArchiveAsync(
                _visibleAssets.Where(asset => asset.Asset is Mesh),
                MobileExportKind.Converted,
                "visible mesh");
        }
        else if (action == fbx)
        {
            await DisplayAlertAsync(
                "FBX / animation export unavailable",
                "The desktop exporter depends on Windows-native Autodesk/FBX components. This iOS build supports mesh preview and OBJ export instead.",
                "OK");
        }
    }

    private async void OnExportMenuClicked(object? sender, EventArgs e)
    {
        const string selectedConverted = "Export selected (converted)";
        const string selectedRaw = "Export selected (raw)";
        const string selectedDump = "Dump selected";
        const string visibleConverted = "Export filtered assets (converted)";
        const string visibleRaw = "Export filtered assets (raw)";
        const string visibleDump = "Dump filtered assets";
        const string allConverted = "Export all assets (converted)";
        const string allRaw = "Export all assets (raw)";
        const string allDump = "Dump all assets";
        var action = await DisplayActionSheetAsync(
            "Export",
            "Cancel",
            null,
            selectedConverted,
            selectedRaw,
            selectedDump,
            visibleConverted,
            visibleRaw,
            visibleDump,
            allConverted,
            allRaw,
            allDump);
        switch (action)
        {
            case selectedConverted:
                if (_selectedAsset is not null)
                {
                    await ShareAssetAsync(_selectedAsset, MobileExportKind.Converted);
                }
                else
                {
                    await DisplayAlertAsync("No asset selected", "Select an asset first.", "OK");
                }
                break;
            case selectedRaw:
                if (_selectedAsset is not null)
                {
                    await ShareAssetAsync(_selectedAsset, MobileExportKind.Raw);
                }
                else
                {
                    await DisplayAlertAsync("No asset selected", "Select an asset first.", "OK");
                }
                break;
            case selectedDump:
                if (_selectedAsset is not null)
                {
                    await ShareAssetAsync(_selectedAsset, MobileExportKind.Dump);
                }
                else
                {
                    await DisplayAlertAsync("No asset selected", "Select an asset first.", "OK");
                }
                break;
            case visibleConverted:
                await ShareAssetArchiveAsync(_visibleAssets, MobileExportKind.Converted, "filtered asset");
                break;
            case visibleRaw:
                await ShareAssetArchiveAsync(_visibleAssets, MobileExportKind.Raw, "filtered asset");
                break;
            case visibleDump:
                await ShareAssetArchiveAsync(_visibleAssets, MobileExportKind.Dump, "filtered asset");
                break;
            case allConverted:
                await ShareAssetArchiveAsync(_assets, MobileExportKind.Converted, "asset");
                break;
            case allRaw:
                await ShareAssetArchiveAsync(_assets, MobileExportKind.Raw, "asset");
                break;
            case allDump:
                await ShareAssetArchiveAsync(_assets, MobileExportKind.Dump, "asset");
                break;
        }
    }

    private async void OnFilterMenuClicked(object? sender, EventArgs e)
    {
        var types = TypeFilter.ItemsSource?.Cast<string>().ToArray() ?? new[] { "All" };
        var action = await DisplayActionSheetAsync("Filter Type", "Cancel", null, types);
        if (action is not null && action != "Cancel")
        {
            TypeFilter.SelectedItem = action;
            SetActiveSection("Assets");
        }
    }

    private async void OnDebugMenuClicked(object? sender, EventArgs e)
    {
        const string exportClasses = "Export class structures";
        const string dumpSelected = "Export selected asset dump";
        var action = await DisplayActionSheetAsync("Debug", "Cancel", null, exportClasses, dumpSelected);
        if (action == exportClasses)
        {
            if (_classes.Count == 0)
            {
                await DisplayAlertAsync("No class structures", "Load a Unity file containing serialized type information first.", "OK");
                return;
            }

            var content = string.Join(
                Environment.NewLine + Environment.NewLine,
                _classes.Select(item => $"// {item.Name} • {item.Version} • class ID {item.TypeId}{Environment.NewLine}{item.Definition}"));
            await ShareTextAsync("AssetStudioClasses.txt", content);
        }
        else if (action == dumpSelected)
        {
            if (_selectedAsset is null)
            {
                await DisplayAlertAsync("No asset selected", "Select an asset first.", "OK");
                return;
            }
            await ShareAssetAsync(_selectedAsset, MobileExportKind.Dump);
        }
    }

    private async void OnDocumentSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || e.CurrentSelection.FirstOrDefault() is not DocumentFileItem file)
        {
            return;
        }

        DocumentsList.SelectedItem = null;
        await LoadDocumentAsync(file.Path);
    }

    private async Task LoadDocumentAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                RefreshDocuments();
                throw new FileNotFoundException("The selected file no longer exists.", path);
            }

            SetLoading(true, $"Loading {Path.GetFileName(path)}…");
            var manager = new AssetsManager();
            manager.SpecifyUnityVersion = _unityVersion;
            var logger = new MobileLogger();
            var previousLogger = Logger.Default;
            try
            {
                await Task.Run(() =>
                {
                    Logger.Default = logger;
                    try
                    {
                        manager.LoadFiles(new[] { path });
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

            _assets.Clear();
            _assets.AddRange(items);
            BuildHierarchy(manager, items);
            BuildClassList(manager);
            TypeFilter.ItemsSource = new[] { "All" }
                .Concat(_assets.Select(asset => asset.Type).Distinct().OrderBy(type => type))
                .ToList();
            TypeFilter.SelectedIndex = 0;

            _extractablePaths.Clear();
            if (IsExtractableArchive(path))
            {
                _extractablePaths.Add(path);
            }
            _selectedAsset = null;
            SearchBar.Text = string.Empty;
            UpdateSelection();
            ApplyFilters();
            SetActiveSection(items.Count > 0 ? "Assets" : "Files");

            StatusLabel.Text = items.Count > 0
                ? $"Loaded {items.Count:N0} assets from {manager.assetsFileList.Count:N0} serialized file(s)."
                : GetLoadFailureMessage(logger);
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Import failed", exception.Message, "OK");
            StatusLabel.Text = "Import failed. Choose a Unity asset file or AssetBundle.";
        }
        finally
        {
            SetLoading(false);
        }
    }

    private void RefreshDocuments()
    {
        var documentsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documentsDirectory))
        {
            throw new InvalidOperationException("The app's Documents folder could not be located.");
        }

        Directory.CreateDirectory(documentsDirectory);
        _documentFiles.Clear();
        _documentFiles.AddRange(Directory.EnumerateFiles(documentsDirectory)
            .Select(path => new DocumentFileItem(path))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase));
        DocumentsList.ItemsSource = _documentFiles.ToList();
        DocumentsHelp.Text = _documentFiles.Count == 0
            ? "No files are in AssetStudio Documents. In Files, copy a Unity file to On My iPhone > AssetStudio, then tap Refresh."
            : "Tap a file to load it. Files are read from On My iPhone > AssetStudio.";
        ApplyFilters();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchText = e.NewTextValue?.Trim() ?? string.Empty;
        ApplyFilters();
    }

    private void OnTypeFilterChanged(object? sender, EventArgs e)
    {
        _selectedType = TypeFilter.SelectedItem as string ?? "All";
        ApplyFilters();
    }

    private async void OnSectionClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not string section)
        {
            return;
        }

        SetActiveSection(section);
        if (section == "Dump" && _selectedAsset is { } selected)
        {
            DumpText.Text = "Building asset dump…";
            try
            {
                var dump = await Task.Run(() => selected.Asset.Dump());
                if (ReferenceEquals(_selectedAsset, selected))
                {
                    DumpText.Text = dump ?? "No serialized dump is available for this asset.";
                }
            }
            catch (Exception exception)
            {
                DumpText.Text = $"Unable to build asset dump: {exception.Message}";
            }
        }
    }

    private void SetActiveSection(string section)
    {
        DocumentsPanel.IsVisible = section == "Files";
        AssetList.IsVisible = section == "Assets";
        HierarchyList.IsVisible = section == "Scene";
        ClassList.IsVisible = section == "Classes";
        PreviewPanel.IsVisible = section == "Preview";
        DumpPanel.IsVisible = section == "Dump";
        TypeFilter.IsVisible = section == "Assets";
        SearchBar.Placeholder = section switch
        {
            "Files" => "Search documents",
            "Scene" => "Search GameObjects",
            "Classes" => "Search class structures",
            _ => "Search assets"
        };
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var query = _searchText;
        var visibleAssets = _assets.Where(asset =>
            (_showAllAssets || asset.IsExportable)
            && (_selectedType == "All" || asset.Type == _selectedType)
            && (string.IsNullOrEmpty(query)
                || asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || asset.Type.Contains(query, StringComparison.OrdinalIgnoreCase)
                || asset.SourceFile.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        _visibleAssets.Clear();
        _visibleAssets.AddRange(visibleAssets);
        foreach (var asset in _assets)
        {
            asset.MetadataVisible = _showAssetInfo;
        }
        AssetList.ItemsSource = _visibleAssets.ToList();

        DocumentsList.ItemsSource = _documentFiles.Where(item =>
            string.IsNullOrEmpty(query)
            || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Metadata.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        HierarchyList.ItemsSource = _hierarchy.Where(item =>
            string.IsNullOrEmpty(query)
            || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Type.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        ClassList.ItemsSource = _classes.Where(item =>
            string.IsNullOrEmpty(query)
            || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Metadata.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void OnAssetSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selectedAsset = e.CurrentSelection.FirstOrDefault() as AssetListItem;
        UpdateSelection();
    }

    private void OnHierarchySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is HierarchyListItem { Asset: { } asset })
        {
            _selectedAsset = asset;
            UpdateSelection();
        }
    }

    private void OnClassSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is ClassListItem selected)
        {
            DumpText.Text = selected.Definition;
            SelectionLabel.Text = $"{selected.Name} • {selected.Metadata}";
        }
    }

    private void UpdateSelection()
    {
        ExportButton.IsEnabled = _selectedAsset is not null && !_isLoading;
        PreviewButton.IsEnabled = _previewEnabled && _selectedAsset is not null && !_isLoading;
        PreviewPanelButton.IsEnabled = _previewEnabled && !_isLoading && (_selectedAsset?.Asset is Mesh or Texture2D);
        SelectionLabel.Text = _selectedAsset is null
            ? "No asset selected."
            : $"{_selectedAsset.Name} • {_selectedAsset.Type} • path ID {_selectedAsset.PathId}";
        PreviewSummary.Text = _selectedAsset is null
            ? "Select an asset to inspect or preview it."
            : $"{_selectedAsset.Name}\n{_selectedAsset.Type} • {_selectedAsset.Metadata}";
        PreviewText.IsVisible = false;
        DumpText.Text = _selectedAsset is null
            ? "Select an asset to view its serialized dump."
            : "Open the Dump tab to inspect this asset.";
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
                SetActiveSection("Preview");
                await Navigation.PushModalAsync(new NavigationPage(new MeshPreviewPage(mesh)));
            }
            else if (selected.Asset is Texture2D texture)
            {
                SetActiveSection("Preview");
                SetLoading(true, "Preparing texture preview…");
                var imagePath = await Task.Run(() => MobileAssetExporter.Export(texture));
                await Navigation.PushModalAsync(new NavigationPage(new TexturePreviewPage(texture.m_Name, imagePath)));
            }
            else
            {
                SetActiveSection("Preview");
                PreviewText.Text = selected.Asset switch
                {
                    TextAsset textAsset => textAsset.m_Script is { Length: > 0 } script
                        ? Encoding.UTF8.GetString(script)
                        : string.Empty,
                    AudioClip => "Audio playback is not available in this build. Use Export to share the stored audio data.",
                    VideoClip => "Video playback is not available in this build. Use Export to share the stored video data.",
                    _ => selected.Asset.Dump() ?? "No preview is available for this asset."
                };
                PreviewText.IsVisible = true;
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
        if (_selectedAsset is null || _isLoading)
        {
            return;
        }

        await ShareAssetAsync(_selectedAsset, MobileExportKind.Converted);
    }

    private async Task ShareAssetAsync(AssetListItem selected, MobileExportKind kind)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            SetLoading(true, "Preparing asset…");
            var filePath = await Task.Run(() => kind switch
            {
                MobileExportKind.Raw => MobileAssetExporter.ExportRaw(selected.Asset),
                MobileExportKind.Dump => MobileAssetExporter.ExportDump(selected.Asset),
                _ => MobileAssetExporter.Export(selected.Asset)
            });
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

    private async Task ShareAssetArchiveAsync(
        IEnumerable<AssetListItem> source,
        MobileExportKind kind,
        string description)
    {
        var assets = source.ToList();
        if (assets.Count == 0)
        {
            await DisplayAlertAsync("Nothing to export", "The current selection contains no assets.", "OK");
            return;
        }
        if (_isLoading)
        {
            return;
        }

        try
        {
            SetLoading(true, $"Preparing {assets.Count:N0} {description}(s)…");
            var filePath = await Task.Run(() => MobileAssetExporter.ExportArchive(
                assets.Select(item => item.Asset).ToList(),
                kind));
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

    private async Task ShareTextAsync(string fileName, string content)
    {
        if (_isLoading)
        {
            return;
        }

        try
        {
            SetLoading(true, "Preparing export…");
            var filePath = Path.Combine(FileSystem.CacheDirectory, $"{Guid.NewGuid():N}_{fileName}");
            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8);
            await Share.Default.RequestAsync(new ShareFileRequest(
                $"Share {fileName}",
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
        DocumentsList.IsEnabled = !isLoading;
        ExportButton.IsEnabled = !isLoading && _selectedAsset is not null;
        ExtractButton.IsEnabled = !isLoading && _extractablePaths.Count > 0;
        PreviewButton.IsEnabled = _previewEnabled && !isLoading && _selectedAsset is not null;
        PreviewPanelButton.IsEnabled = _previewEnabled && !isLoading && (_selectedAsset?.Asset is Mesh or Texture2D);
        if (status is not null)
        {
            StatusLabel.Text = status;
        }
    }

    private void BuildHierarchy(AssetsManager manager, List<AssetListItem> items)
    {
        _hierarchy.Clear();
        foreach (var file in manager.assetsFileList)
        {
            var fileItems = items
                .Where(item => ReferenceEquals(item.Asset.assetsFile, file) && item.Asset is GameObject)
                .ToList();
            if (fileItems.Count == 0)
            {
                continue;
            }

            var nodes = fileItems.ToDictionary(item => (GameObject)item.Asset);
            var children = new Dictionary<GameObject, List<GameObject>>();
            var roots = new List<GameObject>();
            foreach (var gameObject in nodes.Keys)
            {
                if (gameObject.m_Transform is not null
                    && gameObject.m_Transform.m_Father.TryGet(out var parentTransform)
                    && parentTransform.m_GameObject.TryGet(out var parent)
                    && nodes.ContainsKey(parent))
                {
                    if (!children.TryGetValue(parent, out var childList))
                    {
                        childList = new List<GameObject>();
                        children.Add(parent, childList);
                    }
                    childList.Add(gameObject);
                }
                else
                {
                    roots.Add(gameObject);
                }
            }

            _hierarchy.Add(HierarchyListItem.File(file.fileName));
            var visited = new HashSet<GameObject>();
            void AddTree(GameObject gameObject, int depth)
            {
                if (!visited.Add(gameObject) || !nodes.TryGetValue(gameObject, out var asset))
                {
                    return;
                }

                _hierarchy.Add(new HierarchyListItem(asset, depth));
                if (children.TryGetValue(gameObject, out var childList))
                {
                    foreach (var child in childList)
                    {
                        AddTree(child, depth + 1);
                    }
                }
            }

            foreach (var root in roots)
            {
                AddTree(root, 1);
            }
            foreach (var gameObject in nodes.Keys)
            {
                AddTree(gameObject, 1);
            }
        }
        HierarchyList.ItemsSource = _hierarchy.ToList();
    }

    private void BuildClassList(AssetsManager manager)
    {
        var classItems = new Dictionary<(string Version, int TypeId), ClassListItem>();
        foreach (var file in manager.assetsFileList)
        {
            foreach (var type in file.m_Types.Where(type => type.m_Type is not null))
            {
                var typeId = type.m_ScriptTypeIndex >= 0
                    ? -1 - type.m_ScriptTypeIndex
                    : type.classID;
                var nodes = type.m_Type!.m_Nodes;
                var root = nodes.FirstOrDefault();
                var name = root is null ? $"Class {typeId}" : $"{root.m_Type} {root.m_Name}";
                var definition = string.Join(
                    Environment.NewLine,
                    nodes.Select(node =>
                        $"{new string('\t', node.m_Level)}{node.m_Type} {node.m_Name} {node.m_ByteSize} {(node.m_MetaFlag & 0x4000) != 0}"));
                classItems[(file.unityVersion, typeId)] = new ClassListItem(
                    file.unityVersion,
                    typeId,
                    name,
                    definition);
            }
        }

        _classes.Clear();
        _classes.AddRange(classItems.Values
            .OrderBy(item => item.Version, StringComparer.Ordinal)
            .ThenBy(item => item.TypeId));
        ClassList.ItemsSource = _classes.ToList();
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

    private sealed class DocumentFileItem
    {
        public DocumentFileItem(string path)
        {
            Path = path;
            var file = new FileInfo(path);
            Name = file.Name;
            Metadata = $"{file.Extension.ToUpperInvariant()} • {file.Length:N0} bytes";
        }

        public string Name { get; }
        public string Metadata { get; }
        public string Path { get; }
    }

    private sealed class AssetListItem
    {
        public AssetListItem(AssetStudio.Object asset)
        {
            Asset = asset;
            Type = asset.type.ToString();
            PathId = asset.m_PathID.ToString();
            SourceFile = asset.assetsFile.fileName;
            Size = asset.byteSize;
            Name = GetAssetName(asset, Type, PathId);
        }

        public AssetStudio.Object Asset { get; }
        public string Name { get; }
        public string Type { get; }
        public string PathId { get; }
        public string SourceFile { get; }
        public long Size { get; }
        public string Metadata => $"{SourceFile} • ID {PathId} • {Size:N0} bytes";
        public bool MetadataVisible { get; set; } = true;
        public bool IsExportable => Asset is Texture2D or TextAsset or AudioClip or VideoClip or MonoBehaviour or Mesh;

        private static string GetAssetName(AssetStudio.Object asset, string type, string pathId)
        {
            var name = asset switch
            {
                GameObject gameObject => gameObject.m_Name,
                Texture2D texture => texture.m_Name,
                AudioClip audio => audio.m_Name,
                VideoClip video => video.m_Name,
                Shader shader => shader.m_ParsedForm?.m_Name ?? shader.m_Name,
                NamedObject namedObject => namedObject.m_Name,
                _ => string.Empty
            };
            return string.IsNullOrWhiteSpace(name) ? $"{type} #{pathId}" : name;
        }
    }

    private sealed class HierarchyListItem
    {
        public HierarchyListItem(AssetListItem asset, int depth)
        {
            Asset = asset;
            Name = asset.Name;
            Type = asset.Type;
            Indent = new Thickness(depth * 16, 0, 0, 0);
        }

        private HierarchyListItem(string name)
        {
            Name = name;
            Type = "File";
            Indent = new Thickness(0);
        }

        public AssetListItem? Asset { get; }
        public string Name { get; }
        public string Type { get; }
        public Thickness Indent { get; }

        public static HierarchyListItem File(string name) => new(name);
    }

    private sealed class ClassListItem
    {
        public ClassListItem(string version, int typeId, string name, string definition)
        {
            Version = version;
            TypeId = typeId;
            Name = name;
            Definition = definition;
            Metadata = $"{version} • class ID {typeId}";
        }

        public string Version { get; }
        public int TypeId { get; }
        public string Name { get; }
        public string Metadata { get; }
        public string Definition { get; }
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
