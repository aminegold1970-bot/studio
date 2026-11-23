using Microsoft.Maui.ApplicationModel.DataTransfer;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AssetStudioMobile;

// UI-only reconstruction. The host application must populate these collections
// from its existing AssetStudio services; no sample/fake asset data is inserted.
public partial class MainPage : ContentPage
{
    // The native audio layer subscribes to these events and performs actual playback.
    public event EventHandler? AudioPlayRequested;
    public event EventHandler? AudioPauseRequested;
    public event EventHandler? AudioStopRequested;
    public event Action<bool>? AudioLoopChanged;
    public event Action<double>? AudioSeekRequested;
    public event Action<double>? AudioVolumeChanged;
    // Host integration events: the UI selects inputs; the AssetStudio host performs parsing/extraction.
    public event Func<IReadOnlyList<FileResult>, IProgress<AssetLoadProgress>, CancellationToken, Task>? AssetFilesSelected;
    private CancellationTokenSource? _loadCancellation;
    // Folder selection is host-provided because MAUI has no universal native folder picker.
    public event Func<Task<string?>>? AssetFolderPickerRequested;
    public event Func<string, IProgress<AssetLoadProgress>, CancellationToken, Task>? AssetFolderSelected;
    // Extraction remains host-executed; the page gathers source/destination paths and forwards real progress.
    public event Func<IReadOnlyList<FileResult>, string, IProgress<AssetLoadProgress>, CancellationToken, Task<int>>? ExtractFilesRequested;
    public event Func<string, string, IProgress<AssetLoadProgress>, CancellationToken, Task<int>>? ExtractFolderRequested;
    // Export is executed by the host AssetStudio exporter; this page resolves the exact scope.
    public event Func<string, IReadOnlyList<AssetRow>, string, IProgress<AssetLoadProgress>, CancellationToken, Task<int>>? ExportAssetsRequested;
    // Separate contract for the original Studio.ExportAssetsList(..., XML) operation.
    public event Func<IReadOnlyList<AssetListExportRow>, string, IProgress<AssetLoadProgress>, CancellationToken, Task<int>>? ExportAssetListXmlRequested;
    public event Action<string, IReadOnlyList<AssetRow>>? AssetContextActionRequested;
    private bool _audioLoopEnabled;
    private bool _updatingAudioPosition;
    private readonly ObservableCollection<AssetRow> _allAssets = new();
    private readonly ObservableCollection<AssetRow> _visibleAssets = new();
    // _hierarchy contains only rows currently visible in the expanded tree.
    private readonly ObservableCollection<HierarchyRow> _hierarchy = new();
    private readonly List<HierarchyRow> _hierarchyRoots = new();
    private readonly Dictionary<string, HierarchyRow> _hierarchyById = new(StringComparer.Ordinal);
    private readonly ObservableCollection<ClassRow> _classes = new();
    private readonly ObservableCollection<MetadataRow> _metadataRows = new();
    private readonly ObservableCollection<ClassRow> _visibleClasses = new();
    private string _browserTab = "Asset List";
    private bool _showAllAssets;
    private int _sortColumn = -1;
    private bool _reverseSort;
    private bool _updatingHierarchyChecks;
    private readonly List<HierarchyRow> _hierarchySearchResults = new();
    private int _nextHierarchySearchResult;
    private AssetRow? _selectedAsset;
    private readonly List<AssetRow> _selectedAssets = new();
    private bool _showingDump;
    private bool _showingEmptyPreview = true;
    private readonly AssetDocumentLibrary _documentLibrary = new();
    private readonly AssetStudioMobileLoader _assetLoader = new();
    private bool _dropBridgeAttached;
    private bool? _compactWorkspace;
    private bool _showingCompactPreview;

    public MainPage()
    {
        InitializeComponent();
        _assetLoader.MainPageHost = SetAssets;
        AssetFilesSelected += _assetLoader.LoadFilesAsync;
        BindableLayout.SetItemsSource(MetadataHost, _metadataRows);
        BindableLayout.SetItemTemplate(MetadataHost, new DataTemplate(() =>
        {
            var key = new Label { TextColor = Color.FromArgb("#777777"), FontSize = 11 };
            key.SetBinding(Label.TextProperty, nameof(MetadataRow.Key));
            var value = new Label { TextColor = Color.FromArgb("#202020"), FontSize = 11, HorizontalTextAlignment = TextAlignment.End };
            value.SetBinding(Label.TextProperty, nameof(MetadataRow.Value));
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(1.2, GridUnitType.Star)) }, Padding = new Thickness(2, 5), ColumnSpacing = 8 };
            row.Add(key); row.Add(value, 1);
            return row;
        }));
        AssetCollection.ItemsSource = _visibleAssets;
        HierarchyCollection.ItemsSource = _hierarchy;
        ClassesCollection.ItemsSource = _visibleClasses;
        RefreshBrowserTab();
        DroppedFilesReceived += OnDroppedFilesReceived;
    }



    private void OnWorkspaceSizeChanged(object? sender, EventArgs e)
    {
        if (WorkspaceGrid.Width <= 0 || WorkspaceGrid.Height <= 0)
            return;

        var compact = WorkspaceGrid.Width < 960;
        if (_compactWorkspace == compact)
            return;

        _compactWorkspace = compact;
        CompactWorkspaceTabs.IsVisible = compact;
        AssetColumnHeaders.IsVisible = !compact && _browserTab == "Asset List";
        if (compact)
        {
            _showingCompactPreview = false;
            WorkspaceGrid.ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition(GridLength.Star) };
            WorkspaceGrid.RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            };
            Grid.SetRow(CompactWorkspaceTabs, 0);
            Grid.SetColumn(CompactWorkspaceTabs, 0);
            Grid.SetRow(BrowserPane, 1);
            Grid.SetColumn(BrowserPane, 0);
            Grid.SetRow(WorkspaceDivider, 1);
            Grid.SetColumn(WorkspaceDivider, 0);
            Grid.SetRow(PreviewPane, 1);
            Grid.SetColumn(PreviewPane, 0);
            WorkspaceDivider.WidthRequest = -1;
            WorkspaceDivider.HeightRequest = 1;
            WorkspaceDivider.IsVisible = false;
            RefreshCompactWorkspacePane();
        }
        else
        {
            WorkspaceGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition(new GridLength(38, GridUnitType.Star)),
                new ColumnDefinition(new GridLength(1)),
                new ColumnDefinition(new GridLength(62, GridUnitType.Star))
            };
            WorkspaceGrid.RowDefinitions = new RowDefinitionCollection { new RowDefinition(GridLength.Star) };
            Grid.SetRow(BrowserPane, 0);
            Grid.SetColumn(BrowserPane, 0);
            Grid.SetRow(WorkspaceDivider, 0);
            Grid.SetColumn(WorkspaceDivider, 1);
            Grid.SetRow(PreviewPane, 0);
            Grid.SetColumn(PreviewPane, 2);
            WorkspaceDivider.WidthRequest = 1;
            WorkspaceDivider.HeightRequest = -1;
            WorkspaceDivider.IsVisible = true;
            BrowserPane.IsVisible = true;
            PreviewPane.IsVisible = true;
        }

        foreach (var asset in _allAssets)
            asset.IsCompactLayout = compact;
    }

    private void OnCompactWorkspaceTabClicked(object? sender, EventArgs e)
    {
        if (sender is not Button button)
            return;

        _showingCompactPreview = Equals(button.CommandParameter, "Preview");
        RefreshCompactWorkspacePane();
    }

    private void RefreshCompactWorkspacePane()
    {
        if (_compactWorkspace != true)
            return;

        BrowserPane.IsVisible = !_showingCompactPreview;
        PreviewPane.IsVisible = _showingCompactPreview;
        CompactAssetsButton.BackgroundColor = _showingCompactPreview
            ? Color.FromArgb("#E2E2E2")
            : Color.FromArgb("#FFFFFF");
        CompactPreviewButton.BackgroundColor = _showingCompactPreview
            ? Color.FromArgb("#FFFFFF")
            : Color.FromArgb("#E2E2E2");
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
#if IOS
        if (!_dropBridgeAttached && Handler?.PlatformView is UIKit.UIViewController controller)
        {
            AssetStudioDropBridge.Attach(controller.View);
            _dropBridgeAttached = true;
        }
#endif
    }

    // Native iOS UIDropInteraction sends temporary file paths here. They are
    // immediately copied into the app's Documents library before the provider URL expires.
    public static event Action<IReadOnlyList<string>>? DroppedFilesReceived;
    public static void PublishDroppedFiles(IReadOnlyList<string> paths) => DroppedFilesReceived?.Invoke(paths);

    private async void OnDroppedFilesReceived(IReadOnlyList<string> paths)
    {
        try
        {
            var imported = new List<FileResult>();
            foreach (var path in paths)
            {
                if (File.Exists(path))
                    imported.Add(await _documentLibrary.ImportPathAsync(path));
            }
            if (imported.Count == 0) return;
            await LoadPersistentFilesAsync(imported, "Dropped files");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Drop import failed: {ex.Message}";
            if (_showErrorMessages) await DisplayAlert("Drop import failed", ex.Message, "OK");
        }
    }

    private async void OnLibraryClicked(object sender, EventArgs e)
    {
        var entries = _documentLibrary.GetEntries();
        if (entries.Count == 0)
        {
            await DisplayAlert("Asset Library", "No saved source files yet. Drop files here or use File → Load file; imported files are kept in Documents/AssetStudio/Library.", "OK");
            return;
        }
        await Navigation.PushModalAsync(CreateModalNavigationPage(new AssetLibraryPage(_documentLibrary,
            files => LoadPersistentFilesAsync(files, "Saved library"))));
    }

    private async Task LoadPersistentFilesAsync(IReadOnlyList<FileResult> selected, string sourceLabel)
    {
        if (AssetFilesSelected == null)
        {
            StatusLabel.Text = "Files are safely saved, but the AssetStudio Unity loader is not connected";
            await DisplayAlert("Loader not connected", "The files are now stored in this app's Documents library. The host still needs to connect AssetFilesSelected to the AssetStudio parser before they can be displayed as Unity assets.", "OK");
            return;
        }
        ResetLoadedWorkspace();
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        SetBusy(true, $"Loading {selected.Count} saved file(s)…");
        var progress = new Progress<AssetLoadProgress>(update => SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), update.Message));
        try
        {
            await AssetFilesSelected.Invoke(selected, progress, cancellation.Token);
            SetBusy(false, $"{sourceLabel} loaded · {_allAssets.Count} assets");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetBusy(false, "Loading cancelled");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Load failed: {ex.Message}");
            await DisplayAlert("Asset loading failed", ex.Message, "OK");
        }
        finally { _loadCancellation = null; }
    }

    // Integration points for the real mobile asset-loading layer.
    public void SetAssets(IEnumerable<AssetRow> assets)
    {
        var incoming = assets?.ToList() ?? new List<AssetRow>();
        void Apply()
        {
            _allAssets.Clear();
            foreach (var asset in incoming)
            {
                asset.IsCompactLayout = _compactWorkspace == true;
                _allAssets.Add(asset);
            }
            ApplyAssetFilter();
            StatusLabel.Text = $"{_allAssets.Count} assets";
        }
        if (MainThread.IsMainThread) Apply();
        else MainThread.BeginInvokeOnMainThread(Apply);
    }

    // Supply the original GameObject tree as a flat sequence with stable IDs and
    // ParentId references. Roots have ParentId == null or an empty ParentId.
    public void SetHierarchy(IEnumerable<HierarchyRow> nodes)
    {
        var incoming = nodes?.ToList() ?? new List<HierarchyRow>();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _hierarchy.Clear();
            _hierarchyRoots.Clear();
            _hierarchyById.Clear();

            foreach (var node in incoming)
            {
                node.Children.Clear();
                node.Parent = null;
                node.IsExpanded = false;
                node.Depth = 0;
                _hierarchyById[node.Id] = node;
            }

            foreach (var node in incoming)
            {
                if (!string.IsNullOrEmpty(node.ParentId) &&
                    _hierarchyById.TryGetValue(node.ParentId, out var parent) &&
                    !ReferenceEquals(parent, node))
                {
                    node.Parent = parent;
                    parent.Children.Add(node);
                }
                else
                {
                    _hierarchyRoots.Add(node);
                }
            }

            foreach (var root in _hierarchyRoots) AddVisibleHierarchyNode(root, 0);
            _hierarchySearchResults.Clear();
            _nextHierarchySearchResult = 0;
            StatusLabel.Text = $"Scene Hierarchy · {incoming.Count} objects";
        });
    }

    private void AddVisibleHierarchyNode(HierarchyRow node, int depth)
    {
        node.Depth = depth;
        _hierarchy.Add(node);
        if (node.IsExpanded)
            foreach (var child in node.Children) AddVisibleHierarchyNode(child, depth + 1);
    }

    private void RebuildVisibleHierarchy()
    {
        _hierarchy.Clear();
        foreach (var root in _hierarchyRoots) AddVisibleHierarchyNode(root, 0);
    }

    private void OnHierarchyExpandClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not HierarchyRow node || !node.HasChildren)
            return;
        node.IsExpanded = !node.IsExpanded;
        RebuildVisibleHierarchy();
    }

    private void OnHierarchyExpandAllClicked(object sender, EventArgs e)
    {
        SetHierarchyExpansionRecursive(_hierarchyRoots, true);
        RebuildVisibleHierarchy();
        StatusLabel.Text = $"Expanded all scene objects · {_hierarchyById.Count} objects";
    }

    private void OnHierarchyCollapseAllClicked(object sender, EventArgs e)
    {
        SetHierarchyExpansionRecursive(_hierarchyRoots, false);
        RebuildVisibleHierarchy();
        StatusLabel.Text = $"Collapsed scene hierarchy · {_hierarchyRoots.Count} root objects";
    }

    private static void SetHierarchyExpansionRecursive(IEnumerable<HierarchyRow> nodes, bool expanded)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = expanded && node.HasChildren;
            SetHierarchyExpansionRecursive(node.Children, expanded);
        }
    }

    public void SetClasses(IEnumerable<ClassRow> classes)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _classes.Clear();
            foreach (var item in classes ?? Enumerable.Empty<ClassRow>()) _classes.Add(item);
            ApplyClassFilter();
            StatusLabel.Text = $"Asset Classes · {_classes.Count} classes";
        });
    }

    // Structured key/value metadata mirrors the original assetInfoLabel while
    // keeping values supplied by the real decoder (never inferred or fabricated).
    public void SetAssetMetadata(IEnumerable<MetadataRow> rows)
    {
        var incoming = rows?.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Key)).ToList()
                       ?? new List<MetadataRow>();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _metadataRows.Clear();
            foreach (var row in incoming) _metadataRows.Add(row);
            MetadataFrame.IsVisible = _metadataRows.Count > 0 && _displayAssetInformation;
        });
    }

    public void SetTextureMetadata(int width, int height, string format, string filterMode,
        int anisotropicLevel, float mipMapBias, string wrapMode, string channels)
    {
        SetAssetMetadata(new[]
        {
            new MetadataRow("Width", width.ToString()),
            new MetadataRow("Height", height.ToString()),
            new MetadataRow("Format", format),
            new MetadataRow("Filter Mode", filterMode),
            new MetadataRow("Anisotropic level", anisotropicLevel.ToString()),
            new MetadataRow("Mip map bias", mipMapBias.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new MetadataRow("Wrap mode", wrapMode),
            new MetadataRow("Channels", channels)
        });
    }

    public sealed class MetadataRow
    {
        public MetadataRow(string key, string value) { Key = key ?? string.Empty; Value = value ?? string.Empty; }
        public string Key { get; }
        public string Value { get; }
    }

    public void SetSelectedAssetDetails(string title, string details, string dump)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            PreviewTitle.Text = title;
            PreviewKind.Text = "Asset information";
            PreviewDetails.Text = details;
            DumpText.Text = dump;
            RefreshPreviewSurfaceVisibility();
        });
    }

    // GUI adapter methods. The existing asset service should call the matching
    // method after decoding the selected Unity object. These methods deliberately
    // accept decoded display data rather than depending on Windows Forms types.
    public void ShowTexturePreview(string title, ImageSource image, int width, int height,
        string format, string additionalInfo = "")
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            PreviewTitle.Text = title;
            PreviewKind.Text = "Texture / Sprite";
            ImagePreviewFrame.IsVisible = image != null;
            AssetImagePreview.Source = image;
            SetAssetMetadata(new[] { new MetadataRow("Width", width.ToString()),
                new MetadataRow("Height", height.ToString()), new MetadataRow("Format", format) });
            PreviewDetails.Text = additionalInfo ?? string.Empty;
            RefreshPreviewSurfaceVisibility();
        });
    }

    public void ShowTextPreview(string title, string kind, string content, string details = "")
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            PreviewTitle.Text = title;
            PreviewKind.Text = kind;
            TextPreviewFrame.IsVisible = true;
            AssetTextPreview.Text = content ?? string.Empty;
            PreviewDetails.Text = details;
            RefreshPreviewSurfaceVisibility();
        });
    }

    public void ShowFontPreview(string title, string details = "")
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            PreviewTitle.Text = title;
            PreviewKind.Text = "Font";
            FontPreviewFrame.IsVisible = true;
            PreviewDetails.Text = details;
            RefreshPreviewSurfaceVisibility();
        });
    }

    public void ShowAudioPreview(string title, string codec, string details = "")
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            PreviewTitle.Text = title;
            PreviewKind.Text = "AudioClip";
            AudioPreviewFrame.IsVisible = true;
            AudioPreviewInfo.Text = string.IsNullOrWhiteSpace(codec) ? "Compression format: Unknown" : $"Compression format: {codec}";
            AudioPlaybackStatus.Text = "Stopped";
            AudioFrequency.Text = "— Hz";
            AudioSeekSlider.Minimum = 0;
            AudioSeekSlider.Maximum = 1;
            AudioSeekSlider.Value = 0;
            AudioCurrentTime.Text = "0:00";
            AudioDuration.Text = "0:00";
            _audioLoopEnabled = false;
            AudioLoopButton.Text = "Loop: Off";
            AudioVolumeSlider.Value = 1;
            PreviewDetails.Text = details;
            RefreshPreviewSurfaceVisibility();
        });
    }

    public void ShowModelPreview(string title, string details)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            PreviewTitle.Text = title;
            PreviewKind.Text = "Mesh / Model";
            ModelPreviewFrame.IsVisible = true;
            ModelPreviewInfo.Text = string.IsNullOrWhiteSpace(details)
                ? "A wireframe preview is available for individual Mesh assets." : details;
            OpenMeshPreviewButton.IsVisible = _selectedAsset?.SourceAsset is AssetStudio.Mesh;
            PreviewDetails.Text = "";
            RefreshPreviewSurfaceVisibility();
        });
    }

    public void ShowUnsupportedPreview(string title, string type, string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            PreviewTitle.Text = title;
            PreviewKind.Text = type;
            PreviewDetails.Text = message;
            RefreshPreviewSurfaceVisibility();
        });
    }

    private void ResetPreviewSurface()
    {
        ImagePreviewFrame.IsVisible = false;
        MetadataFrame.IsVisible = false;
        _metadataRows.Clear();
        TextPreviewFrame.IsVisible = false;
        FontPreviewFrame.IsVisible = false;
        AudioPreviewFrame.IsVisible = false;
        ModelPreviewFrame.IsVisible = false;
        OpenMeshPreviewButton.IsVisible = false;
        AssetImagePreview.Source = null;
        AssetTextPreview.Text = string.Empty;
        AudioPreviewInfo.Text = string.Empty;
        AudioPlaybackStatus.Text = "Stopped";
        AudioFrequency.Text = "— Hz";
        _updatingAudioPosition = true;
        AudioSeekSlider.Value = 0;
        _updatingAudioPosition = false;
        AudioCurrentTime.Text = "0:00";
        AudioDuration.Text = "0:00";
    }

    // Called by the host audio backend as playback progresses. Times are seconds.
    public void UpdateAudioPlaybackState(string status, double positionSeconds,
        double durationSeconds, int sampleRateHz)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            AudioPlaybackStatus.Text = string.IsNullOrWhiteSpace(status) ? "Stopped" : status;
            AudioFrequency.Text = sampleRateHz > 0 ? $"{sampleRateHz:N0} Hz" : "— Hz";
            var duration = Math.Max(0, durationSeconds);
            var position = Math.Clamp(positionSeconds, 0, duration);
            _updatingAudioPosition = true;
            AudioSeekSlider.Maximum = Math.Max(1, duration);
            AudioSeekSlider.Value = Math.Min(position, AudioSeekSlider.Maximum);
            _updatingAudioPosition = false;
            AudioCurrentTime.Text = FormatAudioTime(position);
            AudioDuration.Text = FormatAudioTime(duration);
        });
    }

    private static string FormatAudioTime(double seconds)
    {
        var total = (int)Math.Max(0, Math.Floor(seconds));
        return $"{total / 60}:{total % 60:00}";
    }

    private void OnAudioPlayClicked(object sender, EventArgs e) => AudioPlayRequested?.Invoke(this, EventArgs.Empty);
    private void OnAudioPauseClicked(object sender, EventArgs e) => AudioPauseRequested?.Invoke(this, EventArgs.Empty);
    private void OnAudioStopClicked(object sender, EventArgs e) => AudioStopRequested?.Invoke(this, EventArgs.Empty);

    private void OnAudioLoopClicked(object sender, EventArgs e)
    {
        _audioLoopEnabled = !_audioLoopEnabled;
        AudioLoopButton.Text = _audioLoopEnabled ? "Loop: On" : "Loop: Off";
        AudioLoopChanged?.Invoke(_audioLoopEnabled);
    }

    private void OnAudioSeekChanged(object sender, ValueChangedEventArgs e)
    {
        if (!_updatingAudioPosition) AudioSeekRequested?.Invoke(e.NewValue);
    }

    private void OnAudioVolumeChanged(object sender, ValueChangedEventArgs e) => AudioVolumeChanged?.Invoke(e.NewValue);

    private bool _previewEnabled = true;
    private bool _displayAssetInformation = true;
    private bool _showErrorMessages = true;

    private async void OnFileMenuClicked(object sender, EventArgs e)
    {
        var choice = await Choose("File", "Load file", "Load folder", "Extract file", "Extract folder");
        if (choice == null) return;

        switch (choice)
        {
            case "Load file":
                await PickAssetFilesAsync();
                break;
            case "Load folder":
                await PickAssetFolderAsync();
                break;
            case "Extract file":
                await ExtractFilesAsync();
                break;
            case "Extract folder":
                await ExtractFolderAsync();
                break;
        }
    }

    private async Task ExtractFilesAsync()
    {
        if (ExtractFilesRequested == null)
        {
            if (AssetFolderPickerRequested != null)
            {
                ReportUnwired("Extract file");
                return;
            }

            try
            {
                var pickedSources = await FilePicker.PickMultipleAsync(new PickOptions { PickerTitle = "Select files to extract" });
                var sources = pickedSources?.ToList() ?? new List<FileResult>();
                if (sources.Count == 0) return;

                var persisted = new List<FileResult>();
                foreach (var source in sources)
                    persisted.Add(await _documentLibrary.ImportAsync(source));

                SetBusy(true, $"Unpacking {persisted.Count} file(s)…");
                var archivePath = await Task.Run(() => MobileBundleExtractor.CreateArchive(
                    persisted.Select(file => file.FullPath).ToArray()));
                await Share.Default.RequestAsync(new ShareFileRequest(
                    "AssetStudio unpacked files", new ShareFile(archivePath)));
                SetBusy(false, "Unpacked files are ready to share.");
            }
            catch (Exception ex)
            {
                SetBusy(false, $"File extraction failed: {ex.Message}");
                if (_showErrorMessages) await DisplayAlert("File extraction failed", ex.Message, "OK");
            }
            return;
        }

        if (AssetFolderPickerRequested == null)
        {
            ReportUnwired("Extract file");
            return;
        }

        try
        {
            var pickedSources = await FilePicker.PickMultipleAsync(new PickOptions { PickerTitle = "Select files to extract" });
            var sources = pickedSources?.ToList() ?? new List<FileResult>();
            if (sources.Count == 0) return;
            var destination = await AssetFolderPickerRequested.Invoke();
            if (string.IsNullOrWhiteSpace(destination)) return;

            using var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            SetBusy(true, "Extracting files…");
            var progress = new Progress<AssetLoadProgress>(update =>
                SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), string.IsNullOrWhiteSpace(update.Message) ? "Extracting files…" : update.Message));
            try
            {
                var count = await ExtractFilesRequested.Invoke(sources, destination, progress, cancellation.Token);
                SetBusy(false, $"Finished extracting {count} files.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                SetBusy(false, "File extraction cancelled.");
            }
            catch (Exception ex)
            {
                SetBusy(false, $"File extraction failed: {ex.Message}");
                if (_showErrorMessages) await DisplayAlert("File extraction failed", ex.Message, "OK");
            }
            finally { _loadCancellation = null; }
        }
        catch (Exception ex)
        {
            SetBusy(false, "Ready");
            StatusLabel.Text = $"File extraction selection failed: {ex.Message}";
        }
    }

    private async Task ExtractFolderAsync()
    {
        if (ExtractFolderRequested == null || AssetFolderPickerRequested == null)
        {
            await DisplayAlert("Extract folder",
                "On iPhone, choose Extract file and select the Unity bundles or ZIP files to unpack.",
                "Choose files");
            await ExtractFilesAsync();
            return;
        }

        try
        {
            var source = await AssetFolderPickerRequested.Invoke();
            if (string.IsNullOrWhiteSpace(source)) return;
            var destination = await AssetFolderPickerRequested.Invoke();
            if (string.IsNullOrWhiteSpace(destination)) return;

            using var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            SetBusy(true, "Extracting folder…");
            var progress = new Progress<AssetLoadProgress>(update =>
                SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), string.IsNullOrWhiteSpace(update.Message) ? "Extracting folder…" : update.Message));
            try
            {
                var count = await ExtractFolderRequested.Invoke(source, destination, progress, cancellation.Token);
                SetBusy(false, $"Finished extracting {count} files.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                SetBusy(false, "Folder extraction cancelled.");
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Folder extraction failed: {ex.Message}");
                if (_showErrorMessages) await DisplayAlert("Folder extraction failed", ex.Message, "OK");
            }
            finally { _loadCancellation = null; }
        }
        catch (Exception ex)
        {
            SetBusy(false, "Ready");
            StatusLabel.Text = $"Folder extraction selection failed: {ex.Message}";
        }
    }

    private async Task PickAssetFolderAsync()
    {
        if (AssetFolderPickerRequested == null || AssetFolderSelected == null)
        {
            await DisplayAlert("Load folder",
                "On iPhone, choose Load file to select multiple Unity files from Files.",
                "Choose files");
            await PickAssetFilesAsync();
            return;
        }

        try
        {
            var folderPath = await AssetFolderPickerRequested.Invoke();
            if (string.IsNullOrWhiteSpace(folderPath)) return; // Keep the current workspace on cancel.

            ResetLoadedWorkspace();
            using var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            SetBusy(true, "Loading folder…");
            var progress = new Progress<AssetLoadProgress>(update =>
            {
                var percent = Math.Clamp(update.Percent, 0, 100);
                SetLoadingProgress(percent, string.IsNullOrWhiteSpace(update.Message) ? "Loading folder…" : update.Message);
            });
            try
            {
                await AssetFolderSelected.Invoke(folderPath, progress, cancellation.Token);
                SetBusy(false, $"Folder load finished · {_allAssets.Count} assets");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                SetBusy(false, $"Folder loading cancelled · {_allAssets.Count} assets currently loaded");
            }
            catch (Exception loadError)
            {
                SetBusy(false, $"Folder load failed: {loadError.Message}");
                if (_showErrorMessages)
                    await DisplayAlert("Folder loading failed", loadError.Message, "OK");
            }
            finally
            {
                _loadCancellation = null;
            }
        }
        catch (Exception ex)
        {
            SetBusy(false, "Ready");
            StatusLabel.Text = $"Folder selection failed: {ex.Message}";
        }
    }

    private async Task PickAssetFilesAsync()
    {
        try
        {
            var files = await FilePicker.PickMultipleAsync(new PickOptions { PickerTitle = "Select Unity asset files" });
            var selected = files?.ToList() ?? new List<FileResult>();
            if (selected.Count == 0) return;
            var persisted = new List<FileResult>();
            foreach (var file in selected) persisted.Add(await _documentLibrary.ImportAsync(file));
            await LoadPersistentFilesAsync(persisted, "Imported files");
        }
        catch (Exception ex)
        {
            SetBusy(false, "Ready");
            StatusLabel.Text = $"File import failed: {ex.Message}";
            await DisplayAlert("File import failed", ex.Message, "OK");
        }
    }

    // Mirrors the original ResetForm() lifecycle before a confirmed new load.
    // Existing content is retained when the native picker is cancelled.
    private void ResetLoadedWorkspace()
    {
        _allAssets.Clear();
        _visibleAssets.Clear();
        _selectedAssets.Clear();
        _selectedAsset = null;
        AssetCollection.SelectedItems.Clear();
        _hierarchy.Clear();
        _hierarchyRoots.Clear();
        _hierarchyById.Clear();
        _hierarchySearchResults.Clear();
        _nextHierarchySearchResult = 0;
        _classes.Clear();
        _visibleClasses.Clear();
        _metadataRows.Clear();
        _selectedTypes = null;
        _sortColumn = -1;
        _reverseSort = false;
        ClearSelectedAsset();
        PreviewTitle.Text = "Preview";
        PreviewKind.Text = "Select an asset";
        PreviewDetails.Text = "Select an asset to inspect it.";
        DumpText.Text = "Select an asset to view its dump.";
        ApplyAssetFilter();
        ApplyClassFilter();
    }

    // Called by the host while its real AssetStudio load operation is running.
    // Progress is supplied by the loader; the UI never invents a completion value.
    public void SetLoadingProgress(int percent, string message)
    {
        var safePercent = Math.Clamp(percent, 0, 100);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            BusyIndicator.IsVisible = safePercent < 100;
            BusyIndicator.IsRunning = safePercent < 100;
            StatusLabel.Text = $"{safePercent}% · {message}";
        });
    }

    public void SetLoadingFinished(string message)
    {
        MainThread.BeginInvokeOnMainThread(() => SetBusy(false, message));
    }

    private void SetBusy(bool busy, string message)
    {
        BusyIndicator.IsVisible = busy;
        BusyIndicator.IsRunning = busy;
        CancelLoadButton.IsVisible = busy && _loadCancellation != null;
        CancelLoadButton.IsEnabled = true;
        StatusLabel.Text = message;
    }

    private void OnCancelLoadClicked(object sender, EventArgs e)
    {
        if (_loadCancellation == null) return;
        CancelLoadButton.IsEnabled = false;
        StatusLabel.Text = "Cancellation requested…";
        _loadCancellation.Cancel();
    }

    private async void OnOptionsMenuClicked(object sender, EventArgs e)
    {
        var choice = await Choose("Options", "Display all assets", "Enable preview", "Display asset information", "Specify Unity version", "Export options");
        if (choice == null) return;
        switch (choice)
        {
            case "Display all assets":
                _showAllAssets = !_showAllAssets;
                StatusLabel.Text = $"Display all assets: {(_showAllAssets ? "On" : "Off")}";
                break;
            case "Enable preview":
                _previewEnabled = !_previewEnabled;
                RefreshPreviewSurfaceVisibility();
                StatusLabel.Text = $"Preview: {(_previewEnabled ? "Enabled" : "Disabled")}";
                break;
            case "Display asset information":
                _displayAssetInformation = !_displayAssetInformation;
                PreviewDetails.IsVisible = _displayAssetInformation;
                MetadataFrame.IsVisible = _displayAssetInformation && _metadataRows.Count > 0;
                StatusLabel.Text = $"Asset information: {(_displayAssetInformation ? "Shown" : "Hidden")}";
                break;
            default: ReportUnwired(choice); break;
        }
    }

    private async void OnModelMenuClicked(object sender, EventArgs e)
    {
        var choice = await Choose("Model", "Export all objects (split)", "Export selected objects (split)",
            "Export selected objects (split) + selected AnimationClips", "Export selected objects (merge)",
            "Export selected objects (merge) + selected AnimationClips");
        if (choice != null) ReportUnwired(choice);
    }

    private async void OnExportMenuClicked(object sender, EventArgs e)
    {
        var choice = await Choose("Export", "All assets", "Selected assets", "Filtered assets",
            "Animator + selected AnimationClips", "Raw", "Dump", "Asset list to XML");
        if (choice == null) return;

        var exportKind = "Convert";
        var scope = choice;
        if (choice is "Raw" or "Dump" or "Asset list to XML")
        {
            scope = await Choose(choice, "All assets", "Selected assets", "Filtered assets");
            if (scope == null) return;
            exportKind = choice;
        }
        if (choice == "Animator + selected AnimationClips")
        {
            ReportUnwired(choice);
            return;
        }
        if (choice == "Asset list to XML")
        {
            await RunAssetListXmlExportAsync(scope);
            return;
        }
        await RunAssetExportAsync(exportKind, scope);
    }

    private async Task RunAssetListXmlExportAsync(string scope)
    {
        if (ExportAssetListXmlRequested == null || AssetFolderPickerRequested == null)
        {
            ReportUnwired("Asset list to XML export");
            return;
        }

        IReadOnlyList<AssetRow> targets = scope switch
        {
            "All assets" => _allAssets.ToList(),
            "Selected assets" => _selectedAssets.ToList(),
            "Filtered assets" => _visibleAssets.ToList(),
            _ => Array.Empty<AssetRow>()
        };
        if (targets.Count == 0)
        {
            StatusLabel.Text = "No assets in the selected XML export scope";
            return;
        }

        var destination = await AssetFolderPickerRequested.Invoke();
        if (string.IsNullOrWhiteSpace(destination)) return;

        var snapshot = targets.Select(row => new AssetListExportRow(
            row.Name, row.Container, row.Type, row.TypeId, row.PathId, row.SourceFile, row.Size, row.SourceAsset))
            .ToList();
        using var cancellation = new CancellationTokenSource();
        try
        {
            SetBusy(true, $"Exporting XML asset list ({snapshot.Count})…");
            var progress = new Progress<AssetLoadProgress>(update =>
                SetLoadingProgress(Math.Clamp(update.Percent, 0, 100),
                    string.IsNullOrWhiteSpace(update.Message) ? "Exporting XML asset list…" : update.Message));
            var count = await ExportAssetListXmlRequested.Invoke(snapshot, destination, progress, cancellation.Token);
            StatusLabel.Text = $"Exported XML asset list · {count} item(s) · {scope}";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "XML asset-list export cancelled";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"XML export failed: {ex.Message}";
            if (_showErrorMessages) await DisplayAlert("XML export error", ex.Message, "OK");
        }
        finally
        {
            SetBusy(false, StatusLabel.Text);
        }
    }

    private async Task RunAssetExportAsync(string exportKind, string scope)
    {
        IReadOnlyList<AssetRow> targets = scope switch
        {
            "All assets" => _allAssets.ToList(),
            "Selected assets" => _selectedAssets.ToList(),
            "Filtered assets" => _visibleAssets.ToList(),
            _ => Array.Empty<AssetRow>()
        };
        if (targets.Count == 0)
        {
            StatusLabel.Text = "No assets in the selected export scope";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        try
        {
            SetBusy(true, $"Exporting {targets.Count} assets…");
            int count;
            if (ExportAssetsRequested != null)
            {
                if (AssetFolderPickerRequested == null)
                    throw new InvalidOperationException("The connected exporter requires a destination folder picker.");

                var destination = await AssetFolderPickerRequested.Invoke();
                if (string.IsNullOrWhiteSpace(destination)) return;
                var progress = new Progress<AssetLoadProgress>(update =>
                    SetLoadingProgress(Math.Clamp(update.Percent, 0, 100),
                        string.IsNullOrWhiteSpace(update.Message) ? $"Exporting {targets.Count} assets…" : update.Message));
                count = await ExportAssetsRequested.Invoke(exportKind, targets, destination, progress, cancellation.Token);
            }
            else
            {
                var sourceAssets = targets
                    .Select(row => row.SourceAsset)
                    .OfType<AssetStudio.Object>()
                    .ToArray();
                if (sourceAssets.Length != targets.Count)
                    throw new InvalidOperationException("One or more selected rows are not backed by parsed Unity assets.");

                var outputPath = await Task.Run(() =>
                {
                    if (sourceAssets.Length == 1)
                    {
                        return exportKind switch
                        {
                            "Raw" => MobileAssetExporter.ExportRaw(sourceAssets[0]),
                            "Dump" => MobileAssetExporter.ExportDump(sourceAssets[0]),
                            _ => MobileAssetExporter.Export(sourceAssets[0])
                        };
                    }

                    var kind = exportKind switch
                    {
                        "Raw" => MobileExportKind.Raw,
                        "Dump" => MobileExportKind.Dump,
                        _ => MobileExportKind.Converted
                    };
                    return MobileAssetExporter.ExportArchive(sourceAssets, kind);
                }, cancellation.Token);

                await Share.Default.RequestAsync(new ShareFileRequest(
                    $"AssetStudio {exportKind} export", new ShareFile(outputPath)));
                count = sourceAssets.Length;
            }
            StatusLabel.Text = $"Exported {count} item(s) · {exportKind} · {scope}";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Export cancelled";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Export failed: {ex.Message}";
            if (_showErrorMessages) await DisplayAlert("Export error", ex.Message, "OK");
        }
        finally
        {
            SetBusy(false, StatusLabel.Text);
        }
    }

    private async void OnFilterMenuClicked(object sender, EventArgs e)
    {
        var types = _allAssets.Select(x => x.Type).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        if (types.Length == 0)
        {
            StatusLabel.Text = "Load assets before choosing asset types";
            return;
        }

        var dialog = new AssetTypeFilterPage(types, _selectedTypes, selected =>
        {
            _selectedTypes = selected == null || selected.Count == types.Length ? null : selected;
            ApplyAssetFilter();
        });
        await Navigation.PushModalAsync(CreateModalNavigationPage(dialog));
    }

    private async void OnDebugMenuClicked(object sender, EventArgs e)
    {
        var choice = await Choose("Debug", "Show error message", "Export class structures");
        if (choice == "Show error message")
        {
            _showErrorMessages = !_showErrorMessages;
            StatusLabel.Text = $"Show error message: {(_showErrorMessages ? "On" : "Off")}";
        }
        else if (choice != null) ReportUnwired(choice);
    }

    private async Task<string?> Choose(string title, params string[] options)
    {
        var choice = await DisplayActionSheet(title, "Cancel", null, options);
        return string.IsNullOrWhiteSpace(choice) || choice == "Cancel" ? null : choice;
    }

    private void ReportUnwired(string action) =>
        StatusLabel.Text = $"{action} — requires connection to the host asset service";

    private HashSet<string>? _selectedTypes;

    private async Task ShowMenu(string title, params string[] options)
    {
        var choice = await DisplayActionSheet(title, "Cancel", null, options);
        if (string.IsNullOrWhiteSpace(choice) || choice == "Cancel") return;

        // Explicitly report that this is a UI shell until the corresponding
        // original mobile service is wired into the host project.
        StatusLabel.Text = $"{choice}: action requires host-service integration";
        if (title == "Options" && choice == "Display all assets")
        {
            _showAllAssets = !_showAllAssets;
            StatusLabel.Text = $"Display all assets: {(_showAllAssets ? "On" : "Off")}";
        }
    }

    private void OnBrowserTabClicked(object sender, EventArgs e)
    {
        if (sender is not Button button) return;
        _browserTab = button.Text;
        HierarchyTab.BackgroundColor = _browserTab == HierarchyTab.Text ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        AssetListTab.BackgroundColor = _browserTab == AssetListTab.Text ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        ClassesTab.BackgroundColor = _browserTab == ClassesTab.Text ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        RefreshBrowserTab();
    }

    private void RefreshBrowserTab()
    {
        HierarchyTab.BackgroundColor = _browserTab == HierarchyTab.Text ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        AssetListTab.BackgroundColor = _browserTab == AssetListTab.Text ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        ClassesTab.BackgroundColor = _browserTab == ClassesTab.Text ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        AssetCollection.IsVisible = _browserTab == "Asset List";
        HierarchyCollection.IsVisible = _browserTab == "Scene Hierarchy";
        ClassesCollection.IsVisible = _browserTab == "Asset Classes";
        AssetSearch.IsVisible = _browserTab == "Asset List";
        ClassSearch.IsVisible = _browserTab == "Asset Classes";
        HierarchySearchPanel.IsVisible = _browserTab == "Scene Hierarchy";
        HierarchyActions.IsVisible = _browserTab == "Scene Hierarchy";
        AssetColumnHeaders.IsVisible = _compactWorkspace != true && _browserTab == "Asset List";
        StatusLabel.Text = $"{_browserTab} · {(_browserTab == "Asset List" ? _visibleAssets.Count : _browserTab == "Scene Hierarchy" ? _hierarchy.Count : _visibleClasses.Count)} items";
    }

    private void OnAssetSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyAssetFilter();

    private void ApplyAssetFilter()
    {
        var query = AssetSearch?.Text?.Trim() ?? "";
        IEnumerable<AssetRow> filtered = _allAssets.Where(asset =>
            (_selectedTypes == null || _selectedTypes.Contains(asset.Type)) &&
            (query.Length == 0 ||
             Contains(asset.Name, query) || Contains(asset.Container, query) ||
             Contains(asset.Type, query) || Contains(asset.PathId, query)));
        filtered = SortAssets(filtered);
        _visibleAssets.Clear();
        foreach (var asset in filtered) _visibleAssets.Add(asset);
        _selectedAssets.RemoveAll(asset => !_visibleAssets.Contains(asset));
        if (_selectedAsset != null && !_visibleAssets.Contains(_selectedAsset))
        {
            AssetCollection.SelectedItems.Clear();
            ClearSelectedAsset();
        }
        if (StatusLabel != null && _browserTab == "Asset List")
            StatusLabel.Text = $"{_visibleAssets.Count} / {_allAssets.Count} assets" +
                (_selectedTypes == null ? " · All types" : $" · {_selectedTypes.Count} types selected");
    }


    private IEnumerable<AssetRow> SortAssets(IEnumerable<AssetRow> source)
    {
        Func<AssetRow, object> key = _sortColumn switch
        {
            0 => x => x.Name ?? "",
            1 => x => x.Container ?? "",
            2 => x => x.Type ?? "",
            3 => x => long.TryParse(x.PathId, out var id) ? id : long.MinValue,
            4 => x => ParseSize(x.Size),
            _ => x => x.Name ?? ""
        };
        IOrderedEnumerable<AssetRow> ordered = _sortColumn switch
        {
            3 => _reverseSort ? source.OrderByDescending(x => (long)key(x)) : source.OrderBy(x => (long)key(x)),
            4 => _reverseSort ? source.OrderByDescending(x => (long)key(x)) : source.OrderBy(x => (long)key(x)),
            _ => _reverseSort ? source.OrderByDescending(x => (string)key(x), StringComparer.OrdinalIgnoreCase)
                              : source.OrderBy(x => (string)key(x), StringComparer.OrdinalIgnoreCase)
        };
        return ordered;
    }

    private static long ParseSize(string? value)
    {
        if (long.TryParse(value, out var bytes)) return bytes;
        var digits = new string((value ?? "").TakeWhile(c => char.IsDigit(c) || c == '-').ToArray());
        return long.TryParse(digits, out bytes) ? bytes : long.MinValue;
    }

    private void SortBy(int column)
    {
        if (_sortColumn == column) _reverseSort = !_reverseSort;
        else { _sortColumn = column; _reverseSort = false; }
        AssetCollection.SelectedItems.Clear();
        ClearSelectedAsset();
        ApplyAssetFilter();
        StatusLabel.Text = $"Sorted by {(new[] { "Name", "Container", "Type", "PathID", "Size" })[column]} · {(_reverseSort ? "descending" : "ascending")}";
    }
    private void OnSortName(object sender, EventArgs e) => SortBy(0);
    private void OnSortContainer(object sender, EventArgs e) => SortBy(1);
    private void OnSortType(object sender, EventArgs e) => SortBy(2);
    private void OnSortPathId(object sender, EventArgs e) => SortBy(3);
    private void OnSortSize(object sender, EventArgs e) => SortBy(4);

    // Mirrors the original WinForms tree search: Enter selects the next matching
    // object, wraps at the end, and makes its ancestor path visible.
    private void OnHierarchySearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _hierarchySearchResults.Clear();
        _nextHierarchySearchResult = 0;
        var query = e.NewTextValue?.Trim() ?? string.Empty;
        if (query.Length == 0) return;
        foreach (var root in _hierarchyRoots) CollectHierarchyMatches(root, query);
        StatusLabel.Text = _hierarchySearchResults.Count == 0
            ? "No matching scene objects"
            : $"{_hierarchySearchResults.Count} scene object matches · press Enter or Next";
    }

    private void CollectHierarchyMatches(HierarchyRow node, string query)
    {
        if (Contains(node.DisplayName, query)) _hierarchySearchResults.Add(node);
        foreach (var child in node.Children) CollectHierarchyMatches(child, query);
    }

    private void OnHierarchySearchButtonPressed(object sender, EventArgs e) => SelectNextHierarchySearchResult();
    private void OnHierarchySearchNextClicked(object sender, EventArgs e) => SelectNextHierarchySearchResult();

    private void SelectNextHierarchySearchResult()
    {
        if (_hierarchySearchResults.Count == 0)
        {
            StatusLabel.Text = "No matching scene objects";
            return;
        }
        if (_nextHierarchySearchResult >= _hierarchySearchResults.Count) _nextHierarchySearchResult = 0;
        var match = _hierarchySearchResults[_nextHierarchySearchResult++];
        for (var parent = match.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
        RebuildVisibleHierarchy();
        StatusLabel.Text = $"Scene object {_nextHierarchySearchResult} / {_hierarchySearchResults.Count}: {match.DisplayName}";
    }

    private void OnHierarchyCheckChanged(object sender, CheckedChangedEventArgs e)
    {
        if (_updatingHierarchyChecks || sender is not CheckBox check || check.BindingContext is not HierarchyRow row) return;
        _updatingHierarchyChecks = true;
        SetHierarchyCheckedRecursive(row, e.Value);
        UpdateHierarchyAncestors(row.Parent);
        _updatingHierarchyChecks = false;
        StatusLabel.Text = $"{_hierarchyById.Values.Count(x => x.IsChecked)} scene objects checked";
    }

    private static void SetHierarchyCheckedRecursive(HierarchyRow node, bool value)
    {
        node.IsChecked = value;
        foreach (var child in node.Children) SetHierarchyCheckedRecursive(child, value);
    }

    // Keep ancestor checkboxes synchronized with their descendants. A parent is
    // checked only when every direct child subtree is checked.
    private static void UpdateHierarchyAncestors(HierarchyRow? node)
    {
        while (node != null)
        {
            node.IsChecked = node.Children.Count > 0 && node.Children.All(child => child.IsChecked);
            node = node.Parent;
        }
    }

    // Export/integration layer can retrieve the exact checked scene objects.
    public IReadOnlyList<HierarchyRow> GetCheckedHierarchyObjects() =>
        _hierarchyById.Values.Where(node => node.IsChecked).ToList();

    private static bool Contains(string? value, string query) =>
        (value ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);

    private async void OnAssetContextClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not AssetRow row)
            return;

        // The context action applies to the current multi-selection when the
        // pressed row is already selected; otherwise it applies to that row.
        IReadOnlyList<AssetRow> targets = _selectedAssets.Contains(row)
            ? _selectedAssets.ToList()
            : new List<AssetRow> { row };

        var actions = new List<string>
        {
            "Copy name", "Copy container", "Copy type", "Copy PathID"
        };
        if (targets.Count == 1)
        {
            actions.Add("Show original file");
            actions.Add("Go to Scene Hierarchy");
        }
        actions.Add("Export selected assets");

        var action = await DisplayActionSheet(row.Name, "Cancel", null, actions.ToArray());
        if (string.IsNullOrWhiteSpace(action) || action == "Cancel") return;

        string? copyValue = action switch
        {
            "Copy name" => row.Name,
            "Copy container" => row.Container,
            "Copy type" => row.Type,
            "Copy PathID" => row.PathId,
            _ => null
        };

        if (copyValue != null)
        {
            await Clipboard.Default.SetTextAsync(copyValue);
            StatusLabel.Text = $"Copied {action[5..]}";
            return;
        }

        if (action == "Export selected assets")
        {
            AssetContextActionRequested?.Invoke("Export selected assets", targets);
            StatusLabel.Text = $"Export request: {targets.Count} selected asset(s)";
            return;
        }

        AssetContextActionRequested?.Invoke(action, targets);
        StatusLabel.Text = $"{action}: requires host AssetStudio integration";
    }

    private void OnAssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedAssets.Clear();
        _selectedAssets.AddRange(e.CurrentSelection.OfType<AssetRow>());
        var row = _selectedAssets.LastOrDefault();
        if (row == null)
        {
            ClearSelectedAsset();
            return;
        }

        _selectedAsset = row;
        _showingEmptyPreview = false;
        ResetPreviewSurface();
        PreviewTitle.Text = row.Name;
        PreviewKind.Text = row.Type;
        PreviewDetails.Text = $"Container: {row.Container}\nType: {row.Type}\nPathID: {row.PathId}\nSize: {row.Size}\n\nDecoded preview data has not been supplied by the host asset service.";
        DumpText.Text = "Serialized dump is not connected.\n\nSelected asset:\n" +
                        $"Name: {row.Name}\nContainer: {row.Container}\nType: {row.Type}\nPathID: {row.PathId}\nSize: {row.Size}";
        _showingDump = false;
        RefreshPreviewSurfaceVisibility();
        StatusLabel.Text = $"{_selectedAssets.Count} selected · Preview: {row.Name}";
        _ = PresentAssetPreviewAsync(row);
    }

    private async Task PresentAssetPreviewAsync(AssetRow row)
    {
        try
        {
            switch (row.SourceAsset)
            {
                case AssetStudio.TextAsset text:
                    ShowTextPreview(row.Name, "TextAsset", Encoding.UTF8.GetString(text.m_Script));
                    break;
                case AssetStudio.Texture2D texture:
                    PreviewDetails.Text = "Decoding texture…";
                    var imagePath = await Task.Run(() => MobileAssetExporter.Export(texture));
                    if (_selectedAsset?.Id != row.Id) return;
                    ShowTexturePreview(row.Name, ImageSource.FromFile(imagePath), texture.m_Width,
                        texture.m_Height, texture.m_TextureFormat.ToString());
                    break;
                case AssetStudio.Mesh mesh:
                    ShowModelPreview(row.Name,
                        $"Mesh · {mesh.m_VertexCount:N0} vertices · Open the wireframe preview to inspect it.");
                    break;
                case AssetStudio.MonoBehaviour behaviour:
                    ShowTextPreview(row.Name, "MonoBehaviour dump", behaviour.Dump() ?? string.Empty);
                    break;
                case AssetStudio.AudioClip audio:
                    ShowAudioPreview(row.Name, audio.m_CompressionFormat.ToString(),
                        "Audio is available for export in its stored format. Playback needs a native iOS audio backend.");
                    break;
                case AssetStudio.VideoClip:
                    ShowUnsupportedPreview(row.Name, "VideoClip",
                        "Video data can be exported in its stored format.");
                    break;
                default:
                    ShowUnsupportedPreview(row.Name, row.Type,
                        $"No mobile preview is available for {row.Type}. Use Export to save the asset.");
                    break;
            }
        }
        catch (Exception ex)
        {
            if (_selectedAsset?.Id != row.Id) return;
            ShowUnsupportedPreview(row.Name, row.Type, $"Preview failed: {ex.Message}");
        }
    }

    private async void OnMeshPreviewClicked(object sender, EventArgs e)
    {
        if (_selectedAsset?.SourceAsset is not AssetStudio.Mesh mesh)
            return;

        try
        {
            await Navigation.PushModalAsync(CreateModalNavigationPage(new MeshPreviewPage(mesh)));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Mesh preview failed", ex.Message, "OK");
        }
    }

    private static NavigationPage CreateModalNavigationPage(Page page) =>
        new(page)
        {
            BarBackgroundColor = Color.FromArgb("#E2E2E2"),
            BarTextColor = Color.FromArgb("#202020")
        };

    // Returns the current multi-selection in the same order reported by CollectionView.
    // Export commands can consume this list once connected to the real exporter.
    public IReadOnlyList<AssetRow> GetSelectedAssets() => _selectedAssets.ToList();

    private void ClearSelectedAsset()
    {
        _selectedAsset = null;
        _selectedAssets.Clear();
        _showingEmptyPreview = true;
        ResetPreviewSurface();
        PreviewTitle.Text = "Preview";
        PreviewKind.Text = "Select an asset";
        PreviewDetails.Text = "Select an asset to inspect it.";
        DumpText.Text = "Select an asset to view its dump.";
        _showingDump = false;
        RefreshPreviewSurfaceVisibility();
    }

    private void RefreshPreviewSurfaceVisibility()
    {
        PreviewScroll.IsVisible = _previewEnabled && !_showingDump && !_showingEmptyPreview;
        DumpScroll.IsVisible = _previewEnabled && _showingDump && !_showingEmptyPreview;
        EmptyPreviewSurface.IsVisible = _previewEnabled && _showingEmptyPreview;
        PreviewTab.BackgroundColor = !_showingDump ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        DumpTab.BackgroundColor = _showingDump ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
    }

    private void OnHierarchySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is HierarchyRow node)
            StatusLabel.Text = $"Selected scene object: {node.DisplayName}";
    }

    private void OnClassSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyClassFilter();

    private void ApplyClassFilter()
    {
        var query = ClassSearch?.Text?.Trim() ?? string.Empty;
        var filtered = _classes.Where(item => query.Length == 0 ||
            Contains(item.Name, query) || Contains(item.Id, query) || Contains(item.Version, query));
        _visibleClasses.Clear();
        foreach (var item in filtered) _visibleClasses.Add(item);
        if (StatusLabel != null && _browserTab == "Asset Classes")
            StatusLabel.Text = $"{_visibleClasses.Count} / {_classes.Count} classes";
    }

    private void OnClassSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ClassRow item) return;
        _showingEmptyPreview = false;
        PreviewTitle.Text = $"{item.Name} ({item.Id})";
        PreviewDetails.Text = string.IsNullOrWhiteSpace(item.Summary)
            ? $"Class ID: {item.Id}\nVersion: {item.Version}\nSelect Dump to inspect the serialized type tree."
            : item.Summary;
        DumpText.Text = string.IsNullOrWhiteSpace(item.StructureText)
            ? $"No type-tree structure was supplied for {item.Name}.\n\nClass ID: {item.Id}\nVersion: {item.Version}"
            : item.StructureText;
        RefreshPreviewSurfaceVisibility();
        StatusLabel.Text = $"Selected class: {item.Name} ({item.Id})";
        OnDumpTabClicked(this, EventArgs.Empty);
    }

    private void OnPreviewTabClicked(object sender, EventArgs e)
    {
        _showingDump = false;
        RefreshPreviewSurfaceVisibility();
    }

    private async void OnDumpTabClicked(object sender, EventArgs e)
    {
        _showingDump = true;
        RefreshPreviewSurfaceVisibility();
        var row = _selectedAsset;
        if (_browserTab != "Asset List" || row == null || row.SourceAsset is not AssetStudio.Object asset)
            return;

        DumpText.Text = "Generating serialized dump…";
        try
        {
            var dump = await Task.Run(() => asset.Dump() ?? string.Empty);
            if (_selectedAsset?.Id == row.Id)
                DumpText.Text = dump;
        }
        catch (Exception ex)
        {
            if (_selectedAsset?.Id != row.Id) return;
            DumpText.Text = $"Dump failed: {ex.Message}";
            StatusLabel.Text = $"Dump failed: {ex.Message}";
        }
    }

    // Native multi-select type filter. The original WinForms menu permits
    // several ClassIDType entries to be checked simultaneously; a single-choice
    // action sheet would not preserve that behavior.
    private sealed class AssetTypeFilterPage : ContentPage
    {
        private readonly HashSet<string> _selected;
        private readonly string[] _allTypes;
        private readonly Action<HashSet<string>?> _completed;
        private bool _finished;

        public AssetTypeFilterPage(string[] types, HashSet<string>? current, Action<HashSet<string>?> completed)
        {
            _allTypes = types;
            _selected = current == null ? new HashSet<string>(types, StringComparer.Ordinal)
                                        : new HashSet<string>(current, StringComparer.Ordinal);
            _completed = completed;
            Title = "Filter Type";
            BackgroundColor = Color.FromArgb("#FFFFFF");

            var selectAll = new Button { Text = "Select all", BackgroundColor = Color.FromArgb("#E0E0E0"), TextColor = Color.FromArgb("#202020") };
            selectAll.Clicked += (_, _) => SetAll(true);
            var clearAll = new Button { Text = "Clear", BackgroundColor = Color.FromArgb("#E0E0E0"), TextColor = Color.FromArgb("#202020") };
            clearAll.Clicked += (_, _) => SetAll(false);
            var list = new VerticalStackLayout { Spacing = 2, Padding = new Thickness(12, 4) };
            foreach (var type in types)
            {
                var check = new CheckBox { IsChecked = _selected.Contains(type), Color = Color.FromArgb("#2878C8"), VerticalOptions = LayoutOptions.Center };
                check.CheckedChanged += (_, args) =>
                {
                    if (args.Value) _selected.Add(type); else _selected.Remove(type);
                };
                var label = new Label { Text = type, TextColor = Color.FromArgb("#202020"), VerticalTextAlignment = TextAlignment.Center, FontSize = 14 };
                var row = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) }, Padding = new Thickness(4, 3) };
                row.Add(check); row.Add(label, 1);
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => check.IsChecked = !check.IsChecked;
                row.GestureRecognizers.Add(tap);
                list.Add(row);
            }

            var apply = new Button { Text = "Apply", BackgroundColor = Color.FromArgb("#E2E2E2"), TextColor = Color.FromArgb("#202020") };
            apply.Clicked += async (_, _) => await Finish(true);
            var cancel = new Button { Text = "Cancel", BackgroundColor = Color.FromArgb("#E0E0E0"), TextColor = Color.FromArgb("#202020") };
            cancel.Clicked += async (_, _) => await Finish(false);
            var top = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 8 };
            top.Add(selectAll); top.Add(clearAll, 1);
            var bottom = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 8, Padding = 12 };
            bottom.Add(cancel); bottom.Add(apply, 1);
            Content = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
                Children = { new VerticalStackLayout { Padding = 12, Children = { top } }, CreateRow(new ScrollView { Content = list }, 1), CreateRow(bottom, 2) } };
        }

        private static View CreateRow(View view, int row)
        {
            Grid.SetRow(view, row);
            return view;
        }

        private void SetAll(bool enabled)
        {
            _selected.Clear();
            if (enabled) foreach (var type in _allTypes) _selected.Add(type);
            // Rebuild to synchronize visible checkbox states without relying on
            // an implicit BindingContext or stale checkbox references.
            if (Content is Grid grid && grid.Children.Count > 1 && grid.Children[1] is ScrollView scroll && scroll.Content is VerticalStackLayout rows)
            {
                foreach (var row in rows.Children.OfType<Grid>())
                    if (row.Children.FirstOrDefault() is CheckBox check) check.IsChecked = enabled;
            }
        }

        private async Task Finish(bool apply)
        {
            if (_finished) return;
            _finished = true;
            _completed(apply ? new HashSet<string>(_selected, StringComparer.Ordinal) : null);
            await Navigation.PopModalAsync();
        }
    }

    // Progress adapter for the real AssetStudio host. Percent must come from
    // the loader's actual work; this page never estimates it.
    public sealed record AssetLoadProgress(int Percent, string Message);

    public sealed class AssetRow : BindableObject
    {
        private bool _isCompactLayout;

        // Stable UI identity is separate from PathID: PathIDs are only unique
        // within a serialized file and can legitimately repeat across files.
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string Name { get; init; } = "";
        public string Container { get; init; } = "";
        public string Type { get; init; } = "";
        // Numeric ClassIDType value for the original XML Type/@id attribute.
        public int TypeId { get; init; }
        public string PathId { get; init; } = "";
        public string Size { get; init; } = "";
        // Original serialized-file identity used by AssetStudio XML inventory export.
        public string SourceFile { get; init; } = "";

        // Opaque reference to the real AssetStudio.Object owned by the host.
        // It deliberately avoids referencing the WinForms-only AssetItem type.
        // Keep this reference alive for as long as the row is in the workspace.
        public object? SourceAsset { get; init; }

        public bool IsCompactLayout
        {
            get => _isCompactLayout;
            set
            {
                if (_isCompactLayout == value) return;
                _isCompactLayout = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsDesktopLayout));
            }
        }

        public bool IsDesktopLayout => !IsCompactLayout;
        public string CompactDetails => string.Join(" · ", new[]
        {
            Type,
            Container,
            string.IsNullOrWhiteSpace(PathId) ? null : $"ID {PathId}",
            Size
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    // Host adapter: create a UI row from the real core AssetStudio object.
    // The host supplies metadata readers because this MAUI-only project does
    // not reference AssetStudio assemblies or their version-specific APIs.
    public static AssetRow CreateAssetRow(
        object sourceAsset,
        string name,
        string container,
        string type,
        long pathId,
        long byteSize,
        int typeId = 0,
        string sourceFile = "")
    {
        ArgumentNullException.ThrowIfNull(sourceAsset);
        return new AssetRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name ?? string.Empty,
            Container = container ?? string.Empty,
            Type = type ?? string.Empty,
            TypeId = typeId,
            PathId = pathId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Size = byteSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceFile = sourceFile ?? string.Empty,
            SourceAsset = sourceAsset
        };
    }

    // Resolve only rows backed by actual core objects. This is the safe input
    // for a refactored mobile exporter; display-only/test rows are excluded.
    public static IReadOnlyList<object> GetSourceAssets(IEnumerable<AssetRow> rows)
    {
        return (rows ?? Enumerable.Empty<AssetRow>())
            .Select(row => row.SourceAsset)
            .Where(asset => asset != null)
            .Cast<object>()
            .ToList();
    }
    // Immutable snapshot matching the fields emitted by original Studio.ExportAssetsList XML.
    // SourceAsset is retained for host-side lookups but is not serialized by the page.
    public sealed record AssetListExportRow(
        string Name, string Container, string Type, int TypeId, string PathId,
        string SourceFile, string Size, object? SourceAsset);

    public sealed class HierarchyRow : BindableObject
    {
        private bool _isChecked;
        private bool _isExpanded;
        private int _depth;
        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public string? ParentId { get; init; }
        public string DisplayName { get; init; } = "";
        public int Depth
        {
            get => _depth;
            set { if (_depth == value) return; _depth = value; OnPropertyChanged(); OnPropertyChanged(nameof(IndentPadding)); }
        }
        public Thickness IndentPadding => new(5 + Math.Max(0, Depth) * 15, 1, 3, 1);
        public List<HierarchyRow> Children { get; } = new();
        public HierarchyRow? Parent { get; set; }
        public bool HasChildren => Children.Count > 0;
        public string ExpandGlyph => IsExpanded ? "▾" : "▸";
        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded == value) return; _isExpanded = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpandGlyph)); }
        }
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }
    }
    // Adapter for the original AssetStudioGUI.Components.TypeTreeItem.
    // Its ToString() emits: indentation + type + name + byte size + alignment flag.
    public static ClassRow CreateClassRow(int typeId, string version, IEnumerable<TypeTreeNodeRow> nodes)
    {
        var tree = nodes?.ToList() ?? new List<TypeTreeNodeRow>();
        if (tree.Count == 0)
            return new ClassRow { Id = typeId.ToString(), Version = version, Name = "(empty type tree)" };

        var root = tree[0];
        var text = string.Concat(tree.Select(node =>
            $"{new string('\t', Math.Max(0, node.Level))}{node.Type} {node.Name} {node.ByteSize} {((node.MetaFlag & 0x4000) != 0)}\r\n"));
        return new ClassRow
        {
            Name = $"{root.Type} {root.Name}",
            Id = typeId.ToString(),
            Version = version ?? string.Empty,
            Summary = $"Class ID: {typeId}\nVersion: {version}\nNodes: {tree.Count}",
            StructureText = text
        };
    }

    public sealed class TypeTreeNodeRow
    {
        public int Level { get; init; }
        public string Type { get; init; } = "";
        public string Name { get; init; } = "";
        public int ByteSize { get; init; }
        public int MetaFlag { get; init; }
    }

    // Mirrors the original TypeTreeItem: display name + type ID + the full
    // indented type-tree text shown in the original read-only classTextBox.
    public sealed class ClassRow
    {
        public string Name { get; init; } = "";
        public string Id { get; init; } = "";
        public string Version { get; init; } = "";
        public string Summary { get; init; } = "";
        public string StructureText { get; init; } = "";
    }
}
