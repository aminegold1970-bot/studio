using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
#if IOS || MACCATALYST
using AVFoundation;
using Foundation;
#endif
#if IOS
using CoreText;
using UIKit;
#endif

namespace AssetStudioMobile;

// The mobile loader populates this page from parsed Unity assets; no sample data is inserted.
public partial class MainPage : ContentPage
{
    // The native audio layer subscribes to these events and performs actual playback.
    public event EventHandler? AudioPlayRequested;
    public event EventHandler? AudioPauseRequested;
    public event EventHandler? AudioStopRequested;
    public event Action<bool>? AudioLoopChanged;
    public event Action<double>? AudioSeekRequested;
    public event Action<double>? AudioVolumeChanged;
    private CancellationTokenSource? _loadCancellation;
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
    private string _classSortKey = "name";
    private bool _classSortDescending;
    private string _browserTab = "Asset List";
    private bool _showAllAssets;
    private int _sortColumn = -1;
    private bool _reverseSort;
    private bool _updatingHierarchyChecks;
    private readonly List<HierarchyRow> _hierarchySearchResults = new();
    private int _nextHierarchySearchResult;
    private AssetRow? _selectedAsset;
#if IOS || MACCATALYST || ANDROID || WINDOWS
    private AssetStudio.AudioClip? _selectedAudioClip;
#endif
    private bool _isPickingAssetFiles;
    private readonly List<AssetRow> _selectedAssets = new();
    private bool _showingDump;
    private bool _showingEmptyPreview = true;
    private MobileImageFormat _textureExportFormat = MobileImageFormat.Png;
    private bool _convertTextures = true;
    private bool _convertAudio = true;
    private bool _restoreTextAssetExtension = true;
    private MobileArchiveGrouping _archiveGrouping = MobileArchiveGrouping.None;
    private string? _previewImagePath;
    private int _previewImageWidth;
    private int _previewImageHeight;
    private string _previewImageFormat = string.Empty;
    private CommunityToolkit.Maui.Views.MediaElement? _videoPlayer;
    private readonly AssetDocumentLibrary _documentLibrary = new();
    private readonly AssetStudioMobileLoader _assetLoader = new();
    private readonly MobileMonoBehaviourAssemblyService _monoBehaviourAssemblyService = new();
    private bool? _compactWorkspace;
    private bool _showingPreviewPane;
    private const double WorkspaceDividerWidth = 28;
    private double _workspaceBrowserRatio = 0.62;
    // In narrow/portrait layouts, the preview starts collapsed to 0% but the divider remains draggable.
    private double _compactBrowserRatio = 1.0;
    private double _workspaceExpandedBrowserRatio = 0.62;
    private double _workspaceDragStartRatio = 0.62;
    private TaskCompletionSource<string?>? _menuChoiceCompletion;
    private Button? _menuAnchor;

    public MainPage()
    {
        InitializeComponent();
        if (Enum.TryParse(
            Preferences.Get("AssetStudio.TextureExportFormat", nameof(MobileImageFormat.Png)),
            ignoreCase: true,
            out MobileImageFormat savedImageFormat))
        {
            _textureExportFormat = savedImageFormat;
        }
        _convertTextures = Preferences.Get("AssetStudio.ConvertTextures", true);
        _convertAudio = Preferences.Get("AssetStudio.ConvertAudio", true);
        _restoreTextAssetExtension = Preferences.Get("AssetStudio.RestoreTextAssetExtension", true);
        if (Enum.TryParse(
            Preferences.Get("AssetStudio.ArchiveGrouping", nameof(MobileArchiveGrouping.None)),
            ignoreCase: true,
            out MobileArchiveGrouping savedArchiveGrouping)
            && Enum.IsDefined(typeof(MobileArchiveGrouping), savedArchiveGrouping))
        {
            _archiveGrouping = savedArchiveGrouping;
        }
        _assetLoader.MainPageHost = (assets, hierarchy, classes) =>
        {
            SetAssets(assets);
            SetHierarchy(hierarchy);
            SetClasses(classes);
        };
        AudioPlayRequested += OnAudioPlaybackRequested;
        AudioPauseRequested += OnAudioPauseRequested;
        AudioStopRequested += OnAudioStopRequested;
        AudioLoopChanged += OnAudioLoopChanged;
        AudioSeekRequested += OnAudioSeekRequested;
        AudioVolumeChanged += OnAudioVolumeChanged;
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
        DropFailed += OnDropFailed;
        DropTargetChanged += OnDropTargetChanged;
#if IOS
        Loaded += OnDropPageLoaded;
#endif
        var workspaceSplitter = new PanGestureRecognizer();
        workspaceSplitter.PanUpdated += OnWorkspaceDividerPanUpdated;
        WorkspaceDivider.GestureRecognizers.Add(workspaceSplitter);
    }



    private void OnWorkspaceSizeChanged(object? sender, EventArgs e)
    {
        if (WorkspaceGrid.Width <= 0 || WorkspaceGrid.Height <= 0)
            return;

        var compact = WorkspaceGrid.Width < 500;
        if (_compactWorkspace == compact)
            return;

        _compactWorkspace = compact;
        WorkspaceGrid.ColumnDefinitions = compact
            ? new ColumnDefinitionCollection
            {
                new(new GridLength(_compactBrowserRatio, GridUnitType.Star)),
                new(new GridLength(WorkspaceDividerWidth, GridUnitType.Absolute)),
                new(new GridLength(1 - _compactBrowserRatio, GridUnitType.Star))
            }
            : new ColumnDefinitionCollection
            {
                new(new GridLength(_workspaceBrowserRatio, GridUnitType.Star)),
                new(new GridLength(WorkspaceDividerWidth, GridUnitType.Absolute)),
                new(new GridLength(1 - _workspaceBrowserRatio, GridUnitType.Star))
            };
        Grid.SetColumn(BrowserPane, 0);
        Grid.SetColumn(WorkspaceDivider, 1);
        Grid.SetColumn(PreviewPane, 2);
        WorkspaceDivider.WidthRequest = WorkspaceDividerWidth;
        WorkspaceDivider.HeightRequest = -1;
        WorkspaceDivider.IsVisible = true;
        WorkspaceViewTabs.IsVisible = false;
        BrowserTabsGrid.IsVisible = true;
        PreviewTabsGrid.IsVisible = true;
        BrowserPane.IsVisible = true;
        PreviewPane.IsVisible = true;
        if (compact)
            ApplyCompactWorkspacePaneRatio();
        else
            ApplyWorkspacePaneRatio();
        RefreshWorkspaceTabs();

        HierarchyTab.Text = "Scene Hierarchy";
        AssetListTab.Text = "Asset List";
        ClassesTab.Text = "Asset Classes";
        foreach (var tab in new[] { HierarchyTab, AssetListTab, ClassesTab })
        {
            tab.FontSize = 10;
            tab.HeightRequest = 22;
            tab.MinimumHeightRequest = 0;
            tab.MinimumWidthRequest = 0;
            tab.Padding = compact ? new Thickness(3, 0) : new Thickness(5, 0);
        }
        AssetSearchFrame.HeightRequest = 21;
        ClassSearchFrame.HeightRequest = 21;
        HierarchySearchFrame.HeightRequest = 21;

        if (MenuScroll.Content is HorizontalStackLayout menu)
        {
            foreach (var child in menu.Children.OfType<Button>())
            {
                child.FontSize = compact ? 9 : 10;
                child.HeightRequest = compact ? 24 : 25;
                child.MinimumHeightRequest = 0;
                child.MinimumWidthRequest = 0;
                child.Padding = compact ? new Thickness(4, 0) : new Thickness(5, 0);
            }
        }

        AssetColumnHeaders.IsVisible = !compact && _browserTab == "Asset List";
        ClassColumnHeaders.IsVisible = _browserTab == "Asset Classes";
        foreach (var asset in _allAssets)
            asset.IsCompactLayout = compact;
    }

    private void OnWorkspaceTabClicked(object sender, EventArgs e)
    {
        if (sender is not Button button)
            return;

        var target = button.CommandParameter as string ?? "Asset List";
        if (target is "Preview" or "Dump")
        {
            _showingPreviewPane = true;
            if (target == "Dump")
                OnDumpTabClicked(this, e);
            else
                OnPreviewTabClicked(this, e);
        }
        else
        {
            _showingPreviewPane = false;
            _browserTab = target;
            RefreshBrowserTab();
        }
        BrowserPane.IsVisible = !_showingPreviewPane;
        PreviewPane.IsVisible = _showingPreviewPane;
        RefreshWorkspaceTabs();
    }

    private void RefreshWorkspaceTabs()
    {
        var selectedTab = _showingPreviewPane
            ? (_showingDump ? "Dump" : "Preview")
            : _browserTab;
        SceneWorkspaceTab.BackgroundColor = selectedTab == "Scene Hierarchy" ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        AssetsWorkspaceTab.BackgroundColor = selectedTab == "Asset List" ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        ClassesWorkspaceTab.BackgroundColor = selectedTab == "Asset Classes" ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        PreviewWorkspaceTab.BackgroundColor = selectedTab == "Preview" ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        DumpWorkspaceTab.BackgroundColor = selectedTab == "Dump" ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
    }

    private void OnWorkspaceDividerPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started)
        {
            _workspaceDragStartRatio = _compactWorkspace == true ? _compactBrowserRatio : _workspaceBrowserRatio;
            return;
        }

        if (e.StatusType == GestureStatus.Running)
        {
            var availableWidth = WorkspaceGrid.Width - WorkspaceDividerWidth;
            if (availableWidth <= 0)
                return;
            var ratio = _workspaceDragStartRatio + e.TotalX / availableWidth;
            if (_compactWorkspace == true)
            {
                _compactBrowserRatio = Math.Clamp(ratio, 0.25, 1.0);
                ApplyCompactWorkspacePaneRatio();
            }
            else
            {
                _workspaceBrowserRatio = Math.Clamp(ratio, 0.25, 0.85);
                _workspaceExpandedBrowserRatio = _workspaceBrowserRatio;
                ApplyWorkspacePaneRatio();
            }
            return;
        }

        if (e.StatusType == GestureStatus.Completed)
        {
            StatusLabel.Text = "Ready to go";
        }
    }

    private void ApplyWorkspacePaneRatio()
    {
        WorkspaceGrid.ColumnDefinitions = new ColumnDefinitionCollection
        {
            new(new GridLength(_workspaceBrowserRatio, GridUnitType.Star)),
            new(new GridLength(WorkspaceDividerWidth, GridUnitType.Absolute)),
            new(new GridLength(1 - _workspaceBrowserRatio, GridUnitType.Star))
        };
        WorkspacePaneToggleButton.Text = _workspaceBrowserRatio >= 0.99 ? ">" : "<";
    }


    private void ApplyCompactWorkspacePaneRatio()
    {
        WorkspaceGrid.ColumnDefinitions = new ColumnDefinitionCollection
        {
            new(new GridLength(_compactBrowserRatio, GridUnitType.Star)),
            new(new GridLength(WorkspaceDividerWidth, GridUnitType.Absolute)),
            new(new GridLength(1 - _compactBrowserRatio, GridUnitType.Star))
        };
        WorkspacePaneToggleButton.Text = _compactBrowserRatio >= 0.99 ? ">" : "<";
    }

    private void OnWorkspaceDividerToggleClicked(object? sender, EventArgs e)
    {
        if (_compactWorkspace == true)
        {
            _compactBrowserRatio = _compactBrowserRatio >= 0.99 ? 0.65 : 1.0;
            ApplyCompactWorkspacePaneRatio();
            StatusLabel.Text = _compactBrowserRatio >= 0.99 ? "Preview pane hidden · 0%" : $"Preview pane · {(1 - _compactBrowserRatio):P0}";
            return;
        }
        if (_workspaceBrowserRatio >= 0.99)
            _workspaceBrowserRatio = Math.Clamp(_workspaceExpandedBrowserRatio, 0.25, 0.75);
        else
        {
            _workspaceExpandedBrowserRatio = _workspaceBrowserRatio;
            _workspaceBrowserRatio = 1.0;
        }
        ApplyWorkspacePaneRatio();
        StatusLabel.Text = _workspaceBrowserRatio >= 0.99 ? "Preview pane hidden · 0%" : $"Preview pane · {(1 - _workspaceBrowserRatio):P0}";
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
#if IOS
        // The MAUI page handler can be created before its native view enters the
        // window. Attach again after layout so the live iOS view is a real drop target.
        MainThread.BeginInvokeOnMainThread(AttachNativeDropTarget);
#endif
    }

#if IOS
    private void OnDropPageLoaded(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(AttachNativeDropTarget);

    private void AttachNativeDropTarget()
    {
        if (Handler?.PlatformView is UIKit.UIViewController controller &&
            controller.View is UIKit.UIView pageView)
        {
            AssetStudioDropBridge.Attach(pageView);
            if (BrowserPane?.Handler?.PlatformView is UIKit.UIView browserView)
                AssetStudioDropBridge.Attach(browserView);
            if (WorkspaceGrid?.Handler?.PlatformView is UIKit.UIView workspaceView)
                AssetStudioDropBridge.Attach(workspaceView);
            if (pageView.Window is UIKit.UIWindow window)
                AssetStudioDropBridge.Attach(window);
        }
    }
#endif

    // Native iOS UIDropInteraction sends temporary file paths here. They are
    // immediately copied into the app's Documents library before the provider URL expires.
    public static event Action<IReadOnlyList<string>>? DroppedFilesReceived;
    public static event Action<string>? DropFailed;
    public static event Action<bool>? DropTargetChanged;
    public static void PublishDroppedFiles(IReadOnlyList<string> paths) => DroppedFilesReceived?.Invoke(paths);
    public static void PublishDropFailure(string message) => DropFailed?.Invoke(message);
    public static void PublishDropTargetChanged(bool isTargeted) => DropTargetChanged?.Invoke(isTargeted);

    private async void OnDroppedFilesReceived(IReadOnlyList<string> paths)
    {
        try
        {
            var imported = new List<FileResult>();
            foreach (var path in paths)
            {
                try
                {
                    if (!File.Exists(path))
                        throw new FileNotFoundException("A dropped file was no longer available.", path);
                    imported.Add(await _documentLibrary.ImportPathAsync(path));
                }
                finally
                {
#if IOS
                    AssetStudioDropBridge.CleanupTemporaryCopy(path);
#endif
                }
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

    private async void OnDropFailed(string message)
    {
        StatusLabel.Text = $"Drop failed: {message}";
        if (_showErrorMessages)
            await DisplayAlert("Could not import dropped file", message, "OK");
    }

    private void OnDropTargetChanged(bool isTargeted) =>
        MainThread.BeginInvokeOnMainThread(() => DropTargetOverlay.IsVisible = isTargeted);

    private async Task OpenLibraryAsync()
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
        _lastLoadedFiles = selected.ToList();
        _lastLoadedFolderPath = null;
        _lastLoadSourceLabel = sourceLabel;
        ResetLoadedWorkspace();
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        SetBusy(true, $"Loading {selected.Count} saved file(s)…");
        var progress = new Progress<AssetLoadProgress>(update => SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), update.Message));
        try
        {
            await _assetLoader.LoadFilesAsync(selected, progress, cancellation.Token);
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

    private async Task LoadPersistentFolderAsync(string folderPath, string sourceLabel)
    {
        _lastLoadedFolderPath = folderPath;
        _lastLoadedFiles = Array.Empty<FileResult>();
        _lastLoadSourceLabel = sourceLabel;

        ResetLoadedWorkspace();
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        SetBusy(true, $"Loading folder {Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath))}…");
        var progress = new Progress<AssetLoadProgress>(update =>
            SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), update.Message));
        try
        {
            await _assetLoader.LoadFolderAsync(folderPath, progress, cancellation.Token);
            SetBusy(false, $"{sourceLabel} loaded · {_allAssets.Count} assets");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetBusy(false, "Folder loading cancelled");
        }
        catch (Exception exception)
        {
            SetBusy(false, $"Folder load failed: {exception.Message}");
            await DisplayAlert("Folder loading failed", exception.Message, "OK");
        }
        finally
        {
            _loadCancellation = null;
        }
    }

    // Publishes parsed assets and scene data to the workspace.
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
        }
        if (MainThread.IsMainThread) Apply();
        else MainThread.BeginInvokeOnMainThread(Apply);
    }

    // Supply the original GameObject tree as a flat sequence with stable IDs and
    // ParentId references. Roots have ParentId == null or an empty ParentId.
    public void SetHierarchy(IEnumerable<HierarchyRow> nodes)
    {
        var incoming = nodes?.ToList() ?? new List<HierarchyRow>();
        void Apply()
        {
            _hierarchy.Clear();
            _hierarchyRoots.Clear();
            _hierarchyById.Clear();
            HierarchyCollection.SelectedItem = null;

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
                    !ReferenceEquals(parent, node) &&
                    !WouldCreateHierarchyCycle(node, parent, _hierarchyById))
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
            HierarchySearchNextButton.IsVisible = false;
            if (_browserTab == "Scene Hierarchy")
                StatusLabel.Text = $"Scene Hierarchy · {incoming.Count} objects";
        }

        if (MainThread.IsMainThread) Apply();
        else MainThread.BeginInvokeOnMainThread(Apply);
    }

    private static bool WouldCreateHierarchyCycle(
        HierarchyRow node,
        HierarchyRow proposedParent,
        IReadOnlyDictionary<string, HierarchyRow> nodesById)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (HierarchyRow? current = proposedParent; current != null;)
        {
            if (ReferenceEquals(current, node) || !visited.Add(current.Id))
                return true;
            current = !string.IsNullOrEmpty(current.ParentId) &&
                      nodesById.TryGetValue(current.ParentId, out var parent)
                ? parent
                : null;
        }

        return false;
    }

    private void AddVisibleHierarchyNode(HierarchyRow node, int depth)
    {
        AddVisibleHierarchyNode(node, depth, new HashSet<HierarchyRow>());
    }

    private void AddVisibleHierarchyNode(HierarchyRow node, int depth, HashSet<HierarchyRow> visited)
    {
        if (!visited.Add(node))
            return;
        node.Depth = depth;
        _hierarchy.Add(node);
        if (node.IsExpanded)
            foreach (var child in node.Children) AddVisibleHierarchyNode(child, depth + 1, visited);
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

    private static void SetHierarchyExpansionRecursive(IEnumerable<HierarchyRow> nodes, bool expanded) =>
        SetHierarchyExpansionRecursive(nodes, expanded, new HashSet<HierarchyRow>());

    private static void SetHierarchyExpansionRecursive(IEnumerable<HierarchyRow> nodes, bool expanded, HashSet<HierarchyRow> visited)
    {
        foreach (var node in nodes)
        {
            if (!visited.Add(node)) continue;
            node.IsExpanded = expanded && node.HasChildren;
            SetHierarchyExpansionRecursive(node.Children, expanded, visited);
        }
    }

    public void SetClasses(IEnumerable<ClassRow> classes)
    {
        var incoming = classes?.ToList() ?? new List<ClassRow>();
        void Apply()
        {
            _classes.Clear();
            foreach (var item in incoming) _classes.Add(item);
            ApplyClassFilter();
            if (_browserTab == "Asset Classes")
                StatusLabel.Text = $"Asset Classes · {_classes.Count} classes";
        }

        if (MainThread.IsMainThread) Apply();
        else MainThread.BeginInvokeOnMainThread(Apply);
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

    // Preview methods accept decoded data without depending on Windows Forms types.
    public void ShowTexturePreview(string title, ImageSource image, int width, int height,
        string format, string additionalInfo = "", string? imagePath = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            _previewImagePath = imagePath;
            _previewImageWidth = width;
            _previewImageHeight = height;
            _previewImageFormat = format ?? string.Empty;
            PreviewTitle.Text = title;
            PreviewKind.Text = "Texture / Sprite";
            ImagePreviewFrame.IsVisible = image != null;
            AssetImagePreview.Source = image;
            OpenFullTexturePreviewButton.IsVisible = !string.IsNullOrWhiteSpace(imagePath);
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

    private void ShowVideoClipPreview(string title, AssetStudio.VideoClip video)
    {
        var hasFrameRate = video.m_FrameRate > 0 && double.IsFinite(video.m_FrameRate);
        var frameRate = hasFrameRate
            ? $"{video.m_FrameRate:N3} fps"
            : "Unknown";
        var duration = hasFrameRate
            ? $"{video.m_FrameCount / video.m_FrameRate:N2} seconds"
            : "Unknown";
        var channels = video.m_AudioChannelCount is { Length: > 0 }
            ? string.Join(", ", video.m_AudioChannelCount)
            : "None";
        var sampleRates = video.m_AudioSampleRate is { Length: > 0 }
            ? string.Join(", ", video.m_AudioSampleRate.Select(rate => $"{rate:N0} Hz"))
            : "None";
        var languages = video.m_AudioLanguage is { Length: > 0 }
            ? string.Join(", ", video.m_AudioLanguage)
            : "None";
        var externalResource = string.IsNullOrWhiteSpace(video.m_ExternalResources?.m_Source)
            ? "Embedded"
            : video.m_ExternalResources.m_Source;
        var storedDataSize = video.m_ExternalResources?.m_Size ?? -1;
        var dataSize = storedDataSize >= 0
            ? $"{storedDataSize:N0} bytes"
            : "Unknown";
        var pixelAspect = video.m_PixelAspectRatioNum > 0 && video.m_PixelAspectRatioDen > 0
            ? $"{video.m_PixelAspectRatioNum}:{video.m_PixelAspectRatioDen}"
            : "Not specified";
        var metadata = new[]
        {
            new MetadataRow("Resolution", $"{video.m_Width} × {video.m_Height}"),
            new MetadataRow("Proxy resolution", $"{video.m_ProxyWidth} × {video.m_ProxyHeight}"),
            new MetadataRow("Frame rate", frameRate),
            new MetadataRow("Frame count", video.m_FrameCount.ToString("N0")),
            new MetadataRow("Duration", duration),
            new MetadataRow("Format ID", video.m_Format.ToString()),
            new MetadataRow("Pixel aspect ratio", pixelAspect),
            new MetadataRow("Audio channels", channels),
            new MetadataRow("Audio sample rates", sampleRates),
            new MetadataRow("Audio languages", languages),
            new MetadataRow("Split alpha", video.m_HasSplitAlpha.ToString()),
            new MetadataRow(
                "Original path",
                string.IsNullOrWhiteSpace(video.m_OriginalPath) ? "Not specified" : video.m_OriginalPath),
            new MetadataRow("External resource", externalResource),
            new MetadataRow("Stored data size", dataSize)
        };
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            PreviewTitle.Text = title;
            PreviewKind.Text = "VideoClip metadata";
            TextPreviewFrame.IsVisible = true;
            AssetTextPreview.Text = string.Join(
                Environment.NewLine, metadata.Select(row => $"{row.Key}: {row.Value}"));
            foreach (var row in metadata)
                _metadataRows.Add(row);
            MetadataFrame.IsVisible = _metadataRows.Count > 0 && _displayAssetInformation;
            VideoPreviewFrame.IsVisible = video.m_VideoData?.Size > 0;
            LoadVideoPlaybackButton.IsVisible = VideoPreviewFrame.IsVisible;
            VideoPlaybackStatus.Text = VideoPreviewFrame.IsVisible
                ? "Load video to use the embedded player."
                : "This video has no readable stored stream.";
            PreviewDetails.Text =
                VideoPreviewFrame.IsVisible
                    ? "Playback uses the device's supported codecs. You can still export the stored stream unchanged."
                    : "This video has no readable stored stream to play or export.";
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
            OpenSceneObjectPreviewButton.IsVisible = false;
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
            TextPreviewFrame.IsVisible = true;
            AssetTextPreview.Text = message;
            PreviewDetails.Text = "The source asset remains selected. Export actions are available where supported.";
            RefreshPreviewSurfaceVisibility();
        });
    }

    private void ResetPreviewSurface()
    {
#if IOS
        UnregisterEmbeddedPreviewFont();
#endif
        ImagePreviewFrame.IsVisible = false;
        OpenFullTexturePreviewButton.IsVisible = false;
        if (_videoPlayer?.Source != null)
            _videoPlayer.Stop();
        _videoPlayer = null;
        VideoPlayerHost.Content = null;
        VideoPlayerHost.IsVisible = false;
        VideoPreviewFrame.IsVisible = false;
        LoadVideoPlaybackButton.IsVisible = false;
        VideoPlaybackStatus.Text = string.Empty;
        _previewImagePath = null;
        _previewImageWidth = 0;
        _previewImageHeight = 0;
        _previewImageFormat = string.Empty;
        MetadataFrame.IsVisible = false;
        _metadataRows.Clear();
        TextPreviewFrame.IsVisible = false;
        FontPreviewFrame.IsVisible = false;
        AudioPreviewFrame.IsVisible = false;
        ModelPreviewFrame.IsVisible = false;
        OpenMeshPreviewButton.IsVisible = false;
        OpenSceneObjectPreviewButton.IsVisible = false;
        AssetImagePreview.Source = null;
        AssetTextPreview.Text = string.Empty;
        FontSpecimenSmall.FontFamily = null;
        FontSpecimenMedium.FontFamily = null;
        FontSpecimenLarge.FontFamily = null;
        FontSpecimenCharacters.FontFamily = null;
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
        _menuAnchor = sender as Button;
        var choice = await Choose("File", "Load file", "Load multiple files", "Load folder",
            "Open Asset Library", "Extract file", "Extract folder");
        if (choice == null) return;

        switch (choice)
        {
            case "Load file":
                await PickAssetFilesAsync(allowMultiple: false);
                break;
            case "Load multiple files":
                await PickAssetFilesAsync(allowMultiple: true);
                break;
            case "Load folder":
                await PickAssetFolderAsync();
                break;
            case "Open Asset Library":
                await OpenLibraryAsync();
                break;
            case "Extract file":
                await ExtractFilesAsync();
                break;
            case "Extract folder":
                await ExtractFolderAsync();
                break;
        }
    }

    private async Task<List<FileResult>> PickFilesForExtractionAsync()
    {
#if IOS
        // Use the exact same native Files picker as Load file. This avoids the
        // MAUI picker callback issue on iOS and keeps selection behavior identical.
        await WaitForActionSheetDismissalAsync();
        return await AssetStudioNativeFilePicker.PickAsync(allowMultiple: true);
#else
        var picked = await FilePicker.Default.PickMultipleAsync(
            new PickOptions { PickerTitle = "Select files to extract" });
        return picked?.ToList() ?? new List<FileResult>();
#endif
    }

    private async Task ExtractFilesAsync()
    {
        try
        {
            var sources = await PickFilesForExtractionAsync();
            if (sources.Count == 0) return;

            using var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            SetBusy(true, $"Unpacking {sources.Count} file(s)…");

            var persisted = new List<FileResult>(sources.Count);
            foreach (var source in sources)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                persisted.Add(await _documentLibrary.ImportAsync(source, cancellation.Token));
            }

            var archivePath = await Task.Run(
                () => MobileBundleExtractor.CreateArchive(
                    persisted.Select(file => file.FullPath).ToArray(),
                    cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            await Share.Default.RequestAsync(new ShareFileRequest(
                "AssetStudio unpacked files", new ShareFile(archivePath)));
            SetBusy(false, "Unpacked files are ready to share.");
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "File extraction cancelled.");
        }
        catch (Exception exception)
        {
            SetBusy(false, $"File extraction failed: {exception.Message}");
            if (_showErrorMessages)
                await DisplayAlert("File extraction failed", exception.Message, "OK");
        }
        finally
        {
            _loadCancellation = null;
        }
    }

    private async Task ExtractFolderAsync()
    {
        try
        {
            var source = await MobileFolderPicker.PickAsync(_documentLibrary);
            if (string.IsNullOrWhiteSpace(source)) return;

            using var cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            SetBusy(true, "Extracting folder…");
            var archivePath = await Task.Run(
                () => MobileBundleExtractor.CreateArchiveFromFolder(source, cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            await Share.Default.RequestAsync(new ShareFileRequest(
                "AssetStudio unpacked files", new ShareFile(archivePath)));
            SetBusy(false, "Unpacked folder files are ready to share.");
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Folder extraction cancelled.");
        }
        catch (Exception exception)
        {
            SetBusy(false, $"Folder extraction failed: {exception.Message}");
            if (_showErrorMessages)
                await DisplayAlert("Folder extraction failed", exception.Message, "OK");
        }
        finally
        {
            _loadCancellation = null;
        }
    }

    private async Task PickAssetFolderAsync()
    {
        try
        {
            var folderPath = await MobileFolderPicker.PickAsync(_documentLibrary);
            if (string.IsNullOrWhiteSpace(folderPath)) return; // Keep the current workspace on cancel.

            await LoadPersistentFolderAsync(folderPath, "Loaded folder");
        }
        catch (Exception exception)
        {
            SetBusy(false, "Ready");
            StatusLabel.Text = $"Folder selection failed: {exception.Message}";
            if (_showErrorMessages)
                await DisplayAlert("Folder selection failed", exception.Message, "OK");
        }
    }

    private async Task PickAssetFilesAsync(bool allowMultiple = false)
    {
        if (_isPickingAssetFiles)
            return;

        _isPickingAssetFiles = true;
        try
        {
#if IOS
            await WaitForActionSheetDismissalAsync();
#endif
            SetBusy(true, allowMultiple ? "Choose Unity files…" : "Choose a Unity file…");
            List<FileResult> selected;
#if IOS
            // Use Apple's document picker directly. This avoids the MAUI FilePicker
            // callback getting stuck on iOS after a document is tapped.
            selected = await AssetStudioNativeFilePicker.PickAsync(allowMultiple);
#else
            if (allowMultiple)
            {
                var files = await FilePicker.Default.PickMultipleAsync(
                    new PickOptions { PickerTitle = "Select Unity asset files" });
                selected = files?.ToList() ?? new List<FileResult>();
            }
            else
            {
                var file = await FilePicker.Default.PickAsync(
                    new PickOptions { PickerTitle = "Select a Unity asset file" });
                selected = file == null ? new List<FileResult>() : new List<FileResult> { file };
            }
#endif

            if (selected.Count == 0)
            {
                SetBusy(false, "No files selected.");
                return;
            }

            // Use the selected document paths directly; do not copy them into an app library.
            // The host loader must consume the picker-provided FileResult paths while access is valid.
            await LoadPersistentFilesAsync(selected, "Loaded files");
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "File selection cancelled.");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"File import failed: {ex.Message}");
            StatusLabel.Text = $"File import failed: {ex.Message}";
            await DisplayAlert("File import failed", ex.Message, "OK");
        }
        finally
        {
            _isPickingAssetFiles = false;
        }
    }

#if IOS
    private async Task WaitForActionSheetDismissalAsync()
    {
        var fallbackPresenter = Handler?.PlatformView as UIKit.UIViewController;
        var rootPresenter = UIKit.UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIKit.UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .FirstOrDefault(window => window.IsKeyWindow)?
            .RootViewController
            ?? fallbackPresenter;
        if (rootPresenter == null)
            throw new InvalidOperationException("The iOS file picker could not find the active window. Try again.");

        var stableDismissalChecks = 0;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var topPresenter = GetTopPresentedViewController(rootPresenter);
            if (topPresenter is not UIKit.UIAlertController)
            {
                if (++stableDismissalChecks >= 3)
                {
                    await Task.Delay(300);
                    if (GetTopPresentedViewController(rootPresenter) is not UIKit.UIAlertController)
                        return;
                    stableDismissalChecks = 0;
                }
            }
            else
            {
                stableDismissalChecks = 0;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("The iOS menu did not dismiss before the file picker was opened. Try again.");
    }

    private static UIKit.UIViewController GetTopPresentedViewController(UIKit.UIViewController presenter)
    {
        while (true)
        {
            if (presenter.PresentedViewController is { } presented)
            {
                presenter = presented;
                continue;
            }

            if (presenter is UIKit.UINavigationController navigationController
                && navigationController.VisibleViewController is { } visible)
            {
                presenter = visible;
                continue;
            }

            if (presenter is UIKit.UITabBarController tabBarController
                && tabBarController.SelectedViewController is { } selected)
            {
                presenter = selected;
                continue;
            }

            return presenter;
        }
    }
#endif

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
        HierarchySearchNextButton.IsVisible = false;
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
            LoadProgressBar.IsVisible = safePercent < 100;
            LoadProgressBar.Progress = safePercent / 100d;
            BusyIndicator.IsVisible = false;
            BusyIndicator.IsRunning = false;
            StatusLabel.Text = message;
        });
    }

    public void SetLoadingFinished(string message)
    {
        MainThread.BeginInvokeOnMainThread(() => SetBusy(false, message));
    }

    private void SetBusy(bool busy, string message)
    {
        BusyIndicator.IsVisible = false;
        BusyIndicator.IsRunning = false;
        LoadProgressBar.IsVisible = busy;
        if (!busy) LoadProgressBar.Progress = 0;
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
        _menuAnchor = sender as Button;
        var choice = await Choose(
            "Options",
            "Display all assets",
            "Enable preview",
            "Display asset information",
            "Specify Unity version",
            "Load script assemblies",
            "Clear script assemblies",
            "Export options");
        if (choice == null) return;
        switch (choice)
        {
            case "Display all assets":
                _showAllAssets = !_showAllAssets;
                ApplyAssetFilter();
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
            case "Specify Unity version":
                await SpecifyUnityVersionAsync();
                break;
            case "Load script assemblies":
                await LoadMonoBehaviourAssembliesAsync();
                break;
            case "Clear script assemblies":
                _monoBehaviourAssemblyService.Clear();
                StatusLabel.Text = "Script assemblies cleared.";
                if (_selectedAsset?.SourceAsset is AssetStudio.MonoBehaviour)
                    await PresentAssetPreviewAsync(_selectedAsset);
                break;
            case "Export options":
                await ChooseDefaultExportKindAsync();
                break;
            default:
                StatusLabel.Text = $"Unsupported options action: {choice}";
                break;
        }
    }

    private async Task LoadMonoBehaviourAssembliesAsync()
    {
        SetBusy(true, "Choose the game's Managed DLL assemblies…");
        try
        {
            var selected = await FilePicker.Default.PickMultipleAsync(
                new PickOptions { PickerTitle = "Select all DLLs from the game's Managed folder" });
            var selectedFiles = selected?.ToList() ?? new List<FileResult>();
            var dllFiles = selectedFiles
                .Where(file => string.Equals(Path.GetExtension(file.FileName), ".dll", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var skippedSelections = selectedFiles
                .Where(file => !string.Equals(Path.GetExtension(file.FileName), ".dll", StringComparison.OrdinalIgnoreCase))
                .Select(file => $"Ignored non-DLL selection: {file.FileName}")
                .ToList();
            if (dllFiles.Count == 0)
            {
                SetBusy(false, selectedFiles.Count > 0
                    ? "No DLL files selected."
                    : "Assembly selection cancelled.");
                return;
            }

            var persisted = new List<FileResult>(dllFiles.Count);
            foreach (var file in dllFiles)
            {
                SetBusy(true, $"Copying {file.FileName} to Library…");
                var imported = await _documentLibrary.ImportAsync(file);
                if (string.IsNullOrWhiteSpace(imported.FullPath) || !File.Exists(imported.FullPath))
                    throw new IOException($"The selected assembly '{file.FileName}' was not saved to the AssetStudio library.");
                persisted.Add(imported);
            }

            SetBusy(true, $"Reading {persisted.Count} managed assemblies…");
            var summary = await Task.Run(() =>
                _monoBehaviourAssemblyService.LoadFiles(persisted.Select(file => file.FullPath)));
            var warnings = summary.Warnings.Concat(skippedSelections).ToList();
            var warningText = warnings.Count == 0
                ? string.Empty
                : $" · {warnings.Count} skipped";
            SetBusy(false, $"{summary.LoadedAssemblyCount} script assemblies loaded{warningText}");

            if (warnings.Count > 0)
            {
                var details = string.Join(Environment.NewLine, warnings.Take(8));
                await DisplayAlert("Some assemblies were skipped", details, "OK");
            }

            if (_selectedAsset is { SourceAsset: AssetStudio.MonoBehaviour } selectedAsset)
                await PresentAssetPreviewAsync(selectedAsset);
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Assembly selection cancelled.");
        }
        catch (Exception exception)
        {
            SetBusy(false, $"Assembly load failed: {exception.Message}");
            await DisplayAlert("Assembly load failed", exception.Message, "OK");
        }
    }

    private async Task SpecifyUnityVersionAsync()
    {
        var version = await DisplayPromptAsync(
            "Specify Unity version",
            "Enter a version such as 2021.3.12f1. Leave empty to clear the override.",
            "Apply",
            "Cancel",
            maxLength: 32,
            keyboard: Keyboard.Default,
            initialValue: _assetLoader.SpecifyUnityVersion ?? string.Empty);
        if (version == null)
            return;

        _assetLoader.SpecifyUnityVersion = version.Trim();
        if (!string.IsNullOrEmpty(_lastLoadedFolderPath))
        {
            await LoadPersistentFolderAsync(_lastLoadedFolderPath, _lastLoadSourceLabel);
            return;
        }

        if (_lastLoadedFiles.Count == 0)
        {
            StatusLabel.Text = string.IsNullOrEmpty(_assetLoader.SpecifyUnityVersion)
                ? "Unity version override cleared"
                : $"Unity version override set to {_assetLoader.SpecifyUnityVersion}; it will be used for the next load";
            return;
        }

        await LoadPersistentFilesAsync(_lastLoadedFiles, _lastLoadSourceLabel);
    }

    private async Task ChooseDefaultExportKindAsync()
    {
        var choice = await Choose("Export options",
            "Default: Converted", "Default: Raw", "Default: Dump",
            $"Texture image: {_textureExportFormat}",
            $"Texture2D data: {(_convertTextures ? "Converted" : "Original .tex")}",
            $"ZIP grouping: {GetArchiveGroupingLabel(_archiveGrouping)}",
            $"Audio export: {(_convertAudio ? "PCM to WAV" : "Original data")}",
            $"TextAsset extension: {(_restoreTextAssetExtension ? "Restore source" : ".txt")}");
        if (choice == null)
            return;

        if (choice.StartsWith("Texture image:", StringComparison.Ordinal))
        {
            var imageFormat = await Choose("Texture export format", "JPEG", "PNG", "BMP", "TGA");
            if (imageFormat == null)
                return;
            _textureExportFormat = imageFormat switch
            {
                "JPEG" => MobileImageFormat.Jpeg,
                "BMP" => MobileImageFormat.Bmp,
                "TGA" => MobileImageFormat.Tga,
                _ => MobileImageFormat.Png
            };
            Preferences.Set("AssetStudio.TextureExportFormat", _textureExportFormat.ToString());
            StatusLabel.Text = $"Texture export format: {_textureExportFormat}";
            return;
        }

        if (choice.StartsWith("Texture2D data:", StringComparison.Ordinal))
        {
            var textureMode = await Choose("Texture2D export data", "Decoded image", "Original .tex data");
            if (textureMode == null)
                return;
            _convertTextures = textureMode == "Decoded image";
            Preferences.Set("AssetStudio.ConvertTextures", _convertTextures);
            StatusLabel.Text = $"Texture2D export data: {(_convertTextures ? "decoded image" : "original .tex data")}";
            return;
        }

        if (choice.StartsWith("ZIP grouping:", StringComparison.Ordinal))
        {
            var groupingChoice = await Choose(
                "ZIP asset grouping",
                "By type name", "By container path", "By source file name", "Do not group");
            if (groupingChoice == null)
                return;
            _archiveGrouping = groupingChoice switch
            {
                "By type name" => MobileArchiveGrouping.TypeName,
                "By container path" => MobileArchiveGrouping.ContainerPath,
                "By source file name" => MobileArchiveGrouping.SourceFileName,
                _ => MobileArchiveGrouping.None
            };
            Preferences.Set("AssetStudio.ArchiveGrouping", _archiveGrouping.ToString());
            StatusLabel.Text = $"ZIP grouping: {GetArchiveGroupingLabel(_archiveGrouping)}";
            return;
        }

        if (choice.StartsWith("Audio export:", StringComparison.Ordinal))
        {
            var audioMode = await Choose(
                "Audio export",
                "Convert supported PCM to WAV",
                "Preserve original audio data");
            if (audioMode == null)
                return;
            _convertAudio = audioMode == "Convert supported PCM to WAV";
            Preferences.Set("AssetStudio.ConvertAudio", _convertAudio);
            StatusLabel.Text = $"Audio export: {(_convertAudio ? "supported PCM to WAV" : "original embedded data")}";
            return;
        }

        if (choice.StartsWith("TextAsset extension:", StringComparison.Ordinal))
        {
            var extensionMode = await Choose(
                "TextAsset export extension",
                "Restore container extension",
                "Use .txt");
            if (extensionMode == null)
                return;
            _restoreTextAssetExtension = extensionMode == "Restore container extension";
            Preferences.Set("AssetStudio.RestoreTextAssetExtension", _restoreTextAssetExtension);
            StatusLabel.Text = $"TextAsset extension: {(_restoreTextAssetExtension ? "restored from container" : ".txt")}";
            return;
        }

        _defaultExportKind = choice switch
        {
            "Default: Raw" => "Raw",
            "Default: Dump" => "Dump",
            _ => "Convert"
        };
        StatusLabel.Text = $"Default export format: {_defaultExportKind}";
    }

    private async void OnModelMenuClicked(object sender, EventArgs e)
    {
        _menuAnchor = sender as Button;
        var modelOptions = new List<string>
        {
            "Preview checked scene objects",
            "Export all meshes (split OBJ)",
            "Export all meshes (merged OBJ)",
            "Export checked scene objects (split OBJ)",
            "Export checked scene objects (merged OBJ)"
        };
#if IOS
        modelOptions.Add("Export all meshes (static FBX)");
        modelOptions.Add("Export checked scene objects (static FBX)");
#endif
        var choice = await Choose("Model", modelOptions.ToArray());
        if (choice == null)
            return;
        if (choice == "Preview checked scene objects")
        {
            await PreviewCheckedSceneObjectsAsync();
            return;
        }

        var exportAll = choice.StartsWith("Export all", StringComparison.Ordinal);
        var merge = choice.Contains("merged", StringComparison.Ordinal);
        var exportFbx = choice.Contains("(static FBX)", StringComparison.Ordinal);
        var sceneObjects = exportAll ? null : GetCheckedSceneObjects().ToList();
        var materialContexts = exportAll
            ? _allAssets.Select(row => row.SourceAsset).OfType<AssetStudio.GameObject>().Distinct().ToList()
            : sceneObjects!;
        var meshes = exportAll
            ? _allAssets.Select(row => row.SourceAsset).OfType<AssetStudio.Mesh>().Distinct().ToList()
            : sceneObjects!
                .SelectMany(GetMeshes)
                .Distinct()
                .ToList();
        if (meshes.Count == 0)
        {
            StatusLabel.Text = exportAll
                ? "No meshes are loaded"
                : "Check scene objects in Scene Hierarchy before exporting their meshes";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        CancelLoadButton.Text = "Cancel export";
        try
        {
            SetBusy(true, $"Preparing {meshes.Count} mesh(es)…");
            string outputPath;
            if (exportFbx)
            {
                outputPath = await Task.Run(
                    () => sceneObjects == null
                        ? MobileFbxExporter.ExportSceneMeshes(
                            MobileAssetExporter.GetMeshAssetGeometries(
                                meshes,
                                materialContexts,
                                cancellation.Token),
                            cancellation.Token)
                        : MobileFbxExporter.ExportSceneMeshes(
                            MobileAssetExporter.GetSceneMeshGeometries(sceneObjects, cancellation.Token),
                            cancellation.Token),
                    cancellation.Token);
            }
            else if (sceneObjects != null)
            {
                outputPath = await Task.Run(
                    () => merge
                        ? MobileAssetExporter.ExportMergedSceneObjects(sceneObjects, cancellation.Token)
                        : MobileAssetExporter.ExportSceneObjectsArchive(sceneObjects, cancellation.Token),
                    cancellation.Token);
            }
            else
            {
                outputPath = await Task.Run(
                    () => merge
                        ? MobileAssetExporter.ExportMergedMeshAssetsArchive(
                            meshes,
                            materialContexts,
                            cancellation.Token)
                        : MobileAssetExporter.ExportMeshAssetsArchive(
                            meshes,
                            materialContexts,
                            cancellation.Token),
                    cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var shareTitle = exportFbx
                ? "AssetStudio static FBX model"
                : sceneObjects != null
                    ? "AssetStudio scene OBJ model archive"
                    : merge ? "AssetStudio merged OBJ model" : "AssetStudio split OBJ models";
            await Share.Default.RequestAsync(new ShareFileRequest(shareTitle, new ShareFile(outputPath)));
            if (exportFbx)
            {
                StatusLabel.Text = sceneObjects == null
                    ? $"Exported {meshes.Count} mesh(es) as static FBX with available material colors/PBR values; scene transforms, textures, rigs and animations are not included"
                    : $"Exported {meshes.Count} mesh(es) as static FBX with scene transforms, material colors/PBR values and baked skin/blend-shape pose; textures, rigs and animation are not included";
            }
            else if (sceneObjects != null)
            {
                StatusLabel.Text =
                    $"Exported {meshes.Count} scene mesh(es) as static OBJ with material maps, vertex colors and baked skin/blend-shape pose; rigs and animation are not included";
            }
            else
            {
                StatusLabel.Text =
                    $"Exported {meshes.Count} mesh(es) as OBJ with available material colors and texture maps; scene transforms and animation are not included";
            }
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Model export cancelled";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Model export failed: {ex.Message}";
            if (_showErrorMessages)
                await DisplayAlert("Model export failed", ex.Message, "OK");
        }
        finally
        {
            _loadCancellation = null;
            CancelLoadButton.Text = "Cancel load";
            SetBusy(false, StatusLabel.Text);
        }
    }

    private async Task PreviewCheckedSceneObjectsAsync()
    {
        var sceneObjects = GetCheckedSceneObjects().ToList();
        if (sceneObjects.Count == 0)
        {
            StatusLabel.Text = "Check scene objects or select a GameObject before opening the scene preview.";
            return;
        }

        try
        {
            StatusLabel.Text = $"Preparing a preview of {sceneObjects.Count} scene object(s)…";
            var geometries = await Task.Run(() =>
                MobileAssetExporter.GetSceneMeshGeometries(sceneObjects, CancellationToken.None));
            if (geometries.Count == 0)
            {
                StatusLabel.Text = "The selected scene objects contain no exportable meshes.";
                return;
            }

            var title = sceneObjects.Count == 1
                ? sceneObjects[0].m_Name
                : $"{sceneObjects.Count} scene objects";
            var drawable = await Task.Run(() => new MeshPreviewDrawable(geometries));
            await Navigation.PushModalAsync(CreateModalNavigationPage(new MeshPreviewPage(title, drawable)));
            StatusLabel.Text = $"Previewing {geometries.Count} scene mesh(es)";
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Scene preview failed: {exception.Message}";
            if (_showErrorMessages)
                await DisplayAlert("Scene preview failed", exception.Message, "OK");
        }
    }

    private IEnumerable<AssetStudio.GameObject> GetCheckedSceneObjects()
    {
        var checkedNodes = _hierarchyById.Values.Where(node => node.IsChecked).ToList();
        var checkedNodeSet = checkedNodes.ToHashSet();
        var checkedObjects = checkedNodes
            .Where(node =>
            {
                var visitedAncestors = new HashSet<HierarchyRow>();
                for (var parent = node.Parent; parent != null && visitedAncestors.Add(parent); parent = parent.Parent)
                {
                    if (checkedNodeSet.Contains(parent))
                        return false;
                }
                return true;
            })
            .Select(node => node.SourceAsset)
            .OfType<AssetStudio.GameObject>()
            .Distinct()
            .ToList();
        if (checkedObjects.Count > 0)
            return checkedObjects;

        if (HierarchyCollection.SelectedItem is HierarchyRow selectedNode)
        {
            var selectedObjects = new List<AssetStudio.GameObject>();
            var pendingNodes = new Stack<HierarchyRow>();
            var visitedNodes = new HashSet<HierarchyRow>();
            pendingNodes.Push(selectedNode);
            while (pendingNodes.Count > 0)
            {
                var node = pendingNodes.Pop();
                if (!visitedNodes.Add(node))
                    continue;
                if (node.SourceAsset is AssetStudio.GameObject gameObject)
                    selectedObjects.Add(gameObject);
                for (var index = node.Children.Count - 1; index >= 0; index--)
                    pendingNodes.Push(node.Children[index]);
            }
            if (selectedObjects.Count > 0)
                return selectedObjects.Distinct().ToList();
        }

        return _selectedAssets
            .Select(row => row.SourceAsset)
            .OfType<AssetStudio.GameObject>()
            .Distinct();
    }

    private static IEnumerable<AssetStudio.Mesh> GetMeshes(AssetStudio.GameObject gameObject)
    {
        foreach (var pointer in gameObject.m_Components)
        {
            if (!pointer.TryGet(out var component))
                continue;
            switch (component)
            {
                case AssetStudio.MeshFilter meshFilter when meshFilter.m_Mesh.TryGet(out var mesh):
                    yield return mesh;
                    break;
                case AssetStudio.SkinnedMeshRenderer renderer when renderer.m_Mesh.TryGet(out var mesh):
                    yield return mesh;
                    break;
            }
        }
    }

    private static List<AssetStudio.GameObject> GetHierarchyGameObjects(HierarchyRow root)
    {
        var gameObjects = new List<AssetStudio.GameObject>();
        var pending = new Stack<HierarchyRow>();
        var visited = new HashSet<HierarchyRow>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
                continue;
            if (current.SourceAsset is AssetStudio.GameObject gameObject)
                gameObjects.Add(gameObject);
            for (var index = current.Children.Count - 1; index >= 0; index--)
                pending.Push(current.Children[index]);
        }
        return gameObjects;
    }

    private async void OnExportMenuClicked(object sender, EventArgs e)
    {
        _menuAnchor = sender as Button;
        var choice = await Choose("Export", "All assets", "Selected assets", "Filtered assets",
            "Animator + selected AnimationClips", "Raw", "Dump", "Asset list to XML");
        if (choice == null) return;

        var exportKind = _defaultExportKind;
        var scope = choice;
        if (choice == "Animator + selected AnimationClips")
        {
            var animatorAndClips = _selectedAssets
                .Where(row => row.SourceAsset is AssetStudio.Animator or AssetStudio.AnimationClip)
                .ToList();
            var selectedAnimators = animatorAndClips
                .Where(row => row.SourceAsset is AssetStudio.Animator)
                .Select(row => (AssetStudio.Animator)row.SourceAsset!)
                .ToArray();
            if (selectedAnimators.Length != 1)
            {
                StatusLabel.Text = "Select exactly one Animator and any AnimationClips to export them";
                return;
            }
            await RunAssetExportAsync(
                "Convert", "Selected assets", animatorAndClips, selectedAnimators[0]);
            return;
        }
        if (choice is "Raw" or "Dump" or "Asset list to XML")
        {
            scope = await Choose(choice, "All assets", "Selected assets", "Filtered assets");
            if (scope == null) return;
            exportKind = choice;
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

        var snapshot = targets.Select(row => new AssetListExportRow(
            row.Name, row.Container, row.Type, row.TypeId, row.PathId, row.SourceFile, row.Size, row.SourceAsset))
            .ToList();
        using var cancellation = new CancellationTokenSource();
        try
        {
            SetBusy(true, $"Exporting XML asset list ({snapshot.Count})…");
            var outputPath = await Task.Run(
                () => MobileAssetExporter.ExportAssetListXml(snapshot), cancellation.Token);
            await Share.Default.RequestAsync(new ShareFileRequest(
                "AssetStudio asset list", new ShareFile(outputPath)));
            StatusLabel.Text = $"Exported XML asset list · {snapshot.Count} item(s) · {scope}";
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

    private static string GetArchiveGroupingLabel(MobileArchiveGrouping grouping) => grouping switch
    {
        MobileArchiveGrouping.TypeName => "type name",
        MobileArchiveGrouping.ContainerPath => "container path",
        MobileArchiveGrouping.SourceFileName => "source file name",
        _ => "do not group"
    };

    private async Task RunAssetExportAsync(
        string exportKind,
        string scope,
        IReadOnlyList<AssetRow>? explicitTargets = null,
        AssetStudio.Animator? animationContext = null)
    {
        IReadOnlyList<AssetRow> targets = explicitTargets ?? scope switch
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
        _loadCancellation = cancellation;
        CancelLoadButton.Text = "Cancel export";
        try
        {
            SetBusy(true, $"Exporting {targets.Count} assets…");
            var count = targets.Count;
            var rawFallbackCount = 0;
            string? fallbackWarning = null;
            var progress = new Progress<AssetLoadProgress>(update =>
                SetLoadingProgress(Math.Clamp(update.Percent, 0, 100),
                    string.IsNullOrWhiteSpace(update.Message) ? $"Exporting {targets.Count} assets…" : update.Message));
            var exportItems = targets.Select(row =>
                row.SourceAsset is AssetStudio.Object sourceAsset
                    ? new MobileExportItem(
                        sourceAsset,
                        row.Container,
                        row.SourceFile,
                        sourceAsset is AssetStudio.AnimationClip ? animationContext : null)
                    : throw new InvalidOperationException(
                        "One or more selected rows are not backed by parsed Unity assets."))
                .ToArray();

            var kind = exportKind switch
            {
                "Raw" => MobileExportKind.Raw,
                "Dump" => MobileExportKind.Dump,
                _ => MobileExportKind.Converted
            };
            string outputPath;
            if (exportItems.Length == 1)
            {
                var item = exportItems[0];
                var result = await Task.Run(
                    () => MobileAssetExporter.ExportForShare(
                        item.Asset,
                        kind,
                        item.Container,
                        cancellation.Token,
                        _monoBehaviourAssemblyService,
                        _textureExportFormat,
                        _convertTextures,
                        _convertAudio,
                        _restoreTextAssetExtension),
                    cancellation.Token);
                outputPath = result.FilePath;
                rawFallbackCount = result.UsedRawFallback ? 1 : 0;
                fallbackWarning = result.Warning;
            }
            else
            {
                var result = await Task.Run(
                    () => MobileAssetExporter.ExportArchive(
                        exportItems, kind, progress, cancellation.Token,
                        _monoBehaviourAssemblyService, _textureExportFormat, _convertTextures, _archiveGrouping,
                        _convertAudio, _restoreTextAssetExtension),
                    cancellation.Token);
                outputPath = result.ArchivePath;
                rawFallbackCount = result.RawFallbackCount;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            await Share.Default.RequestAsync(new ShareFileRequest(
                $"AssetStudio {exportKind} export", new ShareFile(outputPath)));
            StatusLabel.Text = rawFallbackCount == 0
                ? $"Exported {count} item(s) · {exportKind} · {scope}"
                : rawFallbackCount == 1 && fallbackWarning != null
                    ? $"Exported {count} item(s) · raw fallback: {fallbackWarning}"
                    : $"Exported {count} item(s) · {rawFallbackCount} raw fallback(s); see ZIP report";
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
            _loadCancellation = null;
            CancelLoadButton.Text = "Cancel load";
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

        var currentTypes = _selectedTypes == null
            ? null
            : _selectedTypes.Where(types.Contains).ToHashSet(StringComparer.Ordinal);
        var dialog = new AssetTypeFilterPage(types, currentTypes);
        await Navigation.PushModalAsync(CreateModalNavigationPage(dialog));
        var selectedTypes = await dialog.Completion;
        if (selectedTypes == null)
            return;

        _selectedTypes = selectedTypes.Count == types.Length ? null : selectedTypes;
        _browserTab = "Asset List";
        RefreshBrowserTab();
        ClearAssetListSelectionForFilterChange();
        ApplyAssetFilter();
        AssetCollection.Focus();
    }

    private async void OnDebugMenuClicked(object sender, EventArgs e)
    {
        _menuAnchor = sender as Button;
        var choice = await Choose("Debug", "Show error message", "Export class structures");
        if (choice == "Show error message")
        {
            _showErrorMessages = !_showErrorMessages;
            StatusLabel.Text = $"Show error message: {(_showErrorMessages ? "On" : "Off")}";
        }
        else if (choice == "Export class structures")
            await ExportClassStructuresAsync();
        else if (choice != null)
            StatusLabel.Text = $"Unsupported debug action: {choice}";
    }

    private async Task ExportClassStructuresAsync()
    {
        if (_classes.Count == 0)
        {
            StatusLabel.Text = "No class structures are loaded";
            return;
        }

        try
        {
            SetBusy(true, $"Exporting {_classes.Count} class structures…");
            var outputPath = await Task.Run(() => MobileAssetExporter.ExportClassStructuresXml(_classes));
            await Share.Default.RequestAsync(new ShareFileRequest(
                "AssetStudio class structures", new ShareFile(outputPath)));
            StatusLabel.Text = $"Exported {_classes.Count} class structures as XML";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Class export failed: {ex.Message}";
            if (_showErrorMessages)
                await DisplayAlert("Class export failed", ex.Message, "OK");
        }
        finally
        {
            SetBusy(false, StatusLabel.Text);
        }
    }

    private Task<string?> Choose(string title, params string[] options)
    {
        if (options.Length == 0)
            return Task.FromResult<string?>(null);

        _menuChoiceCompletion?.TrySetResult(null);
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _menuChoiceCompletion = completion;
        MenuOptionsHost.Children.Clear();
        for (var index = 0; index < options.Length; index++)
        {
            var option = options[index];
            var hasSubmenu = title == "Export" && option is ("Raw" or "Dump" or "Asset list to XML");
            var content = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                HeightRequest = 24
            };
            content.Add(new Label
            {
                Text = option,
                TextColor = Color.FromArgb("#202020"),
                FontSize = 12,
                FontAutoScalingEnabled = false,
                VerticalTextAlignment = TextAlignment.Center,
                HorizontalTextAlignment = TextAlignment.Start,
                LineBreakMode = LineBreakMode.NoWrap
            });
            if (hasSubmenu)
            {
                content.Add(new Label
                {
                    Text = "▸",
                    TextColor = Color.FromArgb("#555555"),
                    FontSize = 12,
                    FontAutoScalingEnabled = false,
                    VerticalTextAlignment = TextAlignment.Center,
                    HorizontalTextAlignment = TextAlignment.End,
                    Margin = new Thickness(8, 0, 0, 0)
                }, 1);
            }
            var item = new Border
            {
                Style = (Style)Resources["PopupMenuItem"],
                Content = content
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => FinishMenuChoice(option);
            item.GestureRecognizers.Add(tap);
            MenuOptionsHost.Children.Add(item);

            // Match the desktop menu's visual grouping with thin separators.
            if (title == "Export" && index is 2 or 3 or 5)
                MenuOptionsHost.Children.Add(new BoxView
                {
                    HeightRequest = 1,
                    BackgroundColor = Color.FromArgb("#B7B7B7")
                });
        }

        PositionMenuPopup(title, options);
        MenuOverlay.IsVisible = true;
        return completion.Task;
    }

    private void OnMenuScrollSizeChanged(object? sender, EventArgs e) =>
        UpdateMenuOverflowButton();

    private void OnMenuScrollScrolled(object? sender, ScrolledEventArgs e) =>
        UpdateMenuOverflowButton();

    private void UpdateMenuOverflowButton()
    {
        var maxScroll = Math.Max(0, MenuScroll.ContentSize.Width - MenuScroll.Width);
        MenuOverflowButton.IsVisible = maxScroll > 1;
        MenuOverflowButton.Text = MenuScroll.ScrollX >= maxScroll - 1 ? "◂" : "▸";
    }

    private async void OnMenuOverflowClicked(object? sender, EventArgs e)
    {
        var maxScroll = Math.Max(0, MenuScroll.ContentSize.Width - MenuScroll.Width);
        if (maxScroll <= 1)
            return;

        var target = MenuScroll.ScrollX >= maxScroll - 1
            ? 0
            : Math.Min(maxScroll, MenuScroll.ScrollX + Math.Max(80, MenuScroll.Width * 0.7));
        await MenuScroll.ScrollToAsync(target, 0, true);
    }

    private void PositionMenuPopup(string title, IReadOnlyList<string> options)
    {
        // Size to the longest menu label instead of stretching across the phone.
        // The arrow column is included in the estimate for nested Export menus.
        var longest = options.Count == 0 ? 12 : options.Max(option => option.Length);
        var popupWidth = Math.Clamp(longest * 6.1 + 30, 190, 270);
        popupWidth = Math.Min(popupWidth, Math.Max(180, Width - 8));
        var maxLeft = Math.Max(4, Width - popupWidth - 4);
        var left = _menuAnchor == null ? 4 : _menuAnchor.X - MenuScroll.ScrollX;
        MenuPopupFrame.WidthRequest = popupWidth;
        MenuPopupFrame.Margin = new Thickness(Math.Clamp(left, 4, maxLeft), MenuScroll.Height, 4, 0);

        // Never compress rows to fit. Show at most five complete menu options;
        // the rest remain reachable by scrolling vertically.
        var visibleItems = Math.Min(options.Count, 5);
        var separators = title == "Export" ? new[] { 2, 3, 5 }.Count(index => index < visibleItems) : 0;
        MenuOptionsScroll.HeightRequest = visibleItems * 24 + separators;
    }

    private void OnMenuOverlayBackgroundTapped(object? sender, TappedEventArgs e) =>
        FinishMenuChoice(null);

    private void FinishMenuChoice(string? choice)
    {
        var completion = _menuChoiceCompletion;
        _menuChoiceCompletion = null;
        MenuOverlay.IsVisible = false;
        completion?.TrySetResult(choice);
    }

    private HashSet<string>? _selectedTypes;
    private IReadOnlyList<FileResult> _lastLoadedFiles = Array.Empty<FileResult>();
    private string? _lastLoadedFolderPath;
    private string _lastLoadSourceLabel = "Loaded files";
    private string _defaultExportKind = "Convert";

    private void OnBrowserTabClicked(object sender, EventArgs e)
    {
        if (sender is not Button button) return;
        _browserTab = button.CommandParameter as string ?? "Asset List";
        RefreshBrowserTab();
    }

    private void RefreshBrowserTab()
    {
        HierarchyTab.BackgroundColor = _browserTab == "Scene Hierarchy" ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        AssetListTab.BackgroundColor = _browserTab == "Asset List" ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        ClassesTab.BackgroundColor = _browserTab == "Asset Classes" ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#E2E2E2");
        AssetCollection.IsVisible = _browserTab == "Asset List";
        HierarchyCollection.IsVisible = _browserTab == "Scene Hierarchy";
        ClassesCollection.IsVisible = _browserTab == "Asset Classes";
        AssetSearchFrame.IsVisible = _browserTab == "Asset List";
        ClassSearchFrame.IsVisible = _browserTab == "Asset Classes";
        HierarchySearchPanel.IsVisible = _browserTab == "Scene Hierarchy";
        HierarchySearchNextButton.IsVisible = _browserTab == "Scene Hierarchy" && _hierarchySearchResults.Count > 0;
        HierarchyActions.IsVisible = _browserTab == "Scene Hierarchy" && _hierarchy.Count > 0;
        AssetColumnHeaders.IsVisible = _compactWorkspace != true && _browserTab == "Asset List";
        ClassColumnHeaders.IsVisible = _browserTab == "Asset Classes";
        StatusLabel.Text = _browserTab switch
        {
            "Asset List" => $"{_visibleAssets.Count} / {_allAssets.Count} assets",
            "Scene Hierarchy" => $"{_hierarchy.Count} visible / {_hierarchyById.Count} scene objects",
            _ => $"{_visibleClasses.Count} / {_classes.Count} classes"
        };
    }

    private void OnAssetSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ClearAssetListSelectionForFilterChange();
        ApplyAssetFilter();
    }

    // The original desktop FilterAssetList clears the ListView selection before
    // rebuilding the filtered list. Keep that interaction consistent on mobile.
    private void ClearAssetListSelectionForFilterChange()
    {
        _selectedAssets.Clear();
        AssetCollection?.SelectedItems.Clear();
        if (_selectedAsset != null)
            ClearSelectedAsset();
    }

    private void ApplyAssetFilter()
    {
        var query = AssetSearch?.Text?.Trim() ?? "";
        FilterTypeMenuButton.Text = _selectedTypes == null
            ? "Filter Type"
            : $"Filter Type ({_selectedTypes.Count})";
        // Mirror the original WinForms FilterAssetList search scope: Name,
        // Container and PathID. Type filtering is a separate filter-menu state;
        // searching Size, Type or SourceFile here changes desktop semantics.
        IEnumerable<AssetRow> filtered = _allAssets.Where(asset =>
            (_showAllAssets || asset.IsExportable) &&
            (_selectedTypes == null || _selectedTypes.Contains(asset.Type)) &&
            (query.Length == 0 ||
             Contains(asset.Name, query) || Contains(asset.Container, query) ||
             Contains(asset.PathId, query)));
        filtered = SortAssets(filtered);
        _visibleAssets.Clear();
        foreach (var asset in filtered) _visibleAssets.Add(asset);
        _selectedAssets.RemoveAll(asset => !_visibleAssets.Contains(asset));
        if (_selectedAsset != null && !_visibleAssets.Contains(_selectedAsset))
        {
            AssetCollection.SelectedItems.Clear();
            ClearSelectedAsset();
        }
        AssetEmptyLabel.Text = _allAssets.Count == 0
            ? "No assets loaded. Use File → Load file, Load multiple files, or Load folder."
            : "No assets match the current search and filters.";
        if (StatusLabel != null && _browserTab == "Asset List")
            StatusLabel.Text = $"{_visibleAssets.Count} / {_allAssets.Count} assets" +
                (_selectedTypes == null ? " · All types" : $" · {_selectedTypes.Count} types selected");
    }


    private IEnumerable<AssetRow> SortAssets(IEnumerable<AssetRow> source)
    {
        return _sortColumn switch
        {
            1 => _reverseSort
                ? source.OrderByDescending(x => x.Container, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(x => x.Container, StringComparer.OrdinalIgnoreCase),
            2 => _reverseSort
                ? source.OrderByDescending(x => x.Type, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(x => x.Type, StringComparer.OrdinalIgnoreCase),
            3 => _reverseSort
                ? source.OrderByDescending(x => long.TryParse(x.PathId, out var id) ? id : long.MinValue)
                : source.OrderBy(x => long.TryParse(x.PathId, out var id) ? id : long.MinValue),
            4 => _reverseSort
                ? source.OrderByDescending(x => ParseSize(x.Size))
                : source.OrderBy(x => ParseSize(x.Size)),
            _ => _reverseSort
                ? source.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static long ParseSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return long.MinValue;
        var text = value.Trim().Replace(',', '.');
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var bytes)) return bytes;

        // Support display sizes as well as raw byte counts (e.g. 1.25 MB).
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))\s*(B|KB|KIB|MB|MIB|GB|GIB|TB|TIB)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                out var number)) return long.MinValue;
        var unit = match.Groups[2].Value.ToUpperInvariant();
        var exponent = unit switch
        {
            "KB" or "KIB" => 1, "MB" or "MIB" => 2,
            "GB" or "GIB" => 3, "TB" or "TIB" => 4, _ => 0
        };
        var scaled = number * Math.Pow(1024, exponent);
        if (double.IsNaN(scaled) || double.IsInfinity(scaled) || scaled > long.MaxValue || scaled < long.MinValue)
            return long.MinValue;
        return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
    }

    private void SortBy(int column)
    {
        // Preserve selection by asset identity across a re-sort rather than dropping it.
        var selected = _selectedAssets.ToList();
        if (_sortColumn == column) _reverseSort = !_reverseSort;
        else { _sortColumn = column; _reverseSort = false; }
        ApplyAssetFilter();
        AssetCollection.SelectedItems.Clear();
        foreach (var asset in selected.Where(_visibleAssets.Contains))
            AssetCollection.SelectedItems.Add(asset);
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
        if (query.Length == 0)
        {
            HierarchySearchNextButton.IsVisible = false;
            HierarchySearchNextButton.Text = "Next";
            StatusLabel.Text = $"{_hierarchy.Count} visible / {_hierarchyById.Count} scene objects";
            return;
        }
        // Search the full tree in stable depth-first order, independent of the
        // current expansion state. A visited guard protects malformed graphs.
        var visited = new HashSet<HierarchyRow>();
        foreach (var root in _hierarchyRoots) CollectHierarchyMatches(root, query, visited);
        HierarchySearchNextButton.IsVisible = _hierarchySearchResults.Count > 0;
        StatusLabel.Text = _hierarchySearchResults.Count == 0
            ? "No matching scene objects"
            : $"{_hierarchySearchResults.Count} scene object matches · press Enter or Next";
        HierarchySearchNextButton.Text = _hierarchySearchResults.Count > 0
            ? $"Next ({_hierarchySearchResults.Count})"
            : "Next";
    }

    private void CollectHierarchyMatches(HierarchyRow node, string query, HashSet<HierarchyRow> visited)
    {
        if (!visited.Add(node)) return;
        // Match the visible object name and its stable source identifier. IDs are
        // useful when names are duplicated or when the user copied a PathID.
        if (Contains(node.DisplayName, query) || Contains(node.Id, query) || Contains(node.ParentId, query))
            _hierarchySearchResults.Add(node);
        foreach (var child in node.Children) CollectHierarchyMatches(child, query, visited);
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
        // Expand the complete ancestor chain, guarding against unexpected cycles.
        var ancestors = new HashSet<HierarchyRow>();
        for (var parent = match.Parent; parent != null && ancestors.Add(parent); parent = parent.Parent)
            parent.IsExpanded = true;
        RebuildVisibleHierarchy();
        HierarchyCollection.SelectedItem = match;
        HierarchyCollection.ScrollTo(match, ScrollToPosition.MakeVisible, animate: true);
        HierarchyCollection.Focus();
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

    private static void SetHierarchyCheckedRecursive(HierarchyRow node, bool value) =>
        SetHierarchyCheckedRecursive(node, value, new HashSet<HierarchyRow>());

    private static void SetHierarchyCheckedRecursive(HierarchyRow node, bool value, HashSet<HierarchyRow> visited)
    {
        if (!visited.Add(node)) return;
        node.IsChecked = value;
        foreach (var child in node.Children) SetHierarchyCheckedRecursive(child, value, visited);
    }

    // Keep ancestor checkboxes synchronized with their descendants. A parent is
    // checked only when every direct child subtree is checked.
    private static void UpdateHierarchyAncestors(HierarchyRow? node)
    {
        var visited = new HashSet<HierarchyRow>();
        while (node != null && visited.Add(node))
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

        _menuAnchor = null;
        var action = await Choose(row.Name, actions.ToArray());
        if (action == null) return;

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

        if (action == "Go to Scene Hierarchy")
        {
            NavigateToHierarchyNode(row);
            return;
        }

        if (action == "Show original file")
        {
            if (File.Exists(row.SourceFile))
            {
                await Share.Default.RequestAsync(new ShareFileRequest(
                    "AssetStudio source file", new ShareFile(row.SourceFile)));
            }
            else
            {
                StatusLabel.Text = $"Original source file is unavailable: {row.SourceFile}";
            }
            return;
        }

        if (action == "Export selected assets")
        {
            await RunAssetExportAsync("Convert", "Selected assets", targets);
            return;
        }

        StatusLabel.Text = $"Unsupported asset action: {action}";
    }

    private void NavigateToHierarchyNode(AssetRow row)
    {
        if (string.IsNullOrEmpty(row.HierarchyNodeId)
            || !_hierarchyById.TryGetValue(row.HierarchyNodeId, out var node))
        {
            StatusLabel.Text = $"{row.Name} is not associated with a scene object";
            return;
        }

        for (var parent = node.Parent; parent != null; parent = parent.Parent)
            parent.IsExpanded = true;
        _browserTab = "Scene Hierarchy";
        RefreshBrowserTab();
        RebuildVisibleHierarchy();
        HierarchyCollection.SelectedItem = node;
        HierarchyCollection.ScrollTo(node, ScrollToPosition.MakeVisible, animate: true);
        HierarchyCollection.Focus();
        StatusLabel.Text = $"Scene object: {node.DisplayName}";
    }

    private void OnAssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
#if IOS || MACCATALYST || ANDROID || WINDOWS
        StopPreviewAudio();
#endif
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
        PreviewDetails.Text = BuildAssetSummary(row);
        try
        {
            DumpText.Text = row.SourceAsset is AssetStudio.Object source
                ? source.Dump() ?? "No serialized type tree is available for this asset."
                : "No parsed asset data is available for this row.";
        }
        catch (Exception ex)
        {
            DumpText.Text = $"Could not create serialized dump: {ex.Message}";
        }
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
                        texture.m_Height, texture.m_TextureFormat.ToString(), imagePath: imagePath);
                    break;
                case AssetStudio.Mesh mesh:
                    ShowModelPreview(row.Name,
                        $"Mesh · {mesh.m_VertexCount:N0} vertices · Open the wireframe preview to inspect it.");
                    break;
                case AssetStudio.MonoBehaviour behaviour:
                    var conversion = await Task.Run(() => _monoBehaviourAssemblyService.Convert(behaviour));
                    if (_selectedAsset?.Id != row.Id) return;
                    if (conversion.Data != null)
                    {
                        var json = Newtonsoft.Json.JsonConvert.SerializeObject(
                            conversion.Data, Newtonsoft.Json.Formatting.Indented);
                        ShowTextPreview(
                            row.Name,
                            conversion.AssemblyAssisted ? "MonoBehaviour · assembly-assisted" : "MonoBehaviour",
                            json);
                    }
                    else
                    {
                        ShowTextPreview(
                            row.Name,
                            "MonoBehaviour dump",
                            behaviour.Dump() ?? string.Empty,
                            conversion.Error ?? "No serialized type tree or assembly-derived schema is available.");
                    }
                    break;
                case AssetStudio.AudioClip audio:
#if IOS || MACCATALYST || ANDROID || WINDOWS
                    _selectedAudioClip = audio;
#endif
                    ShowAudioPreview(row.Name, audio.m_CompressionFormat.ToString(),
                        $"Length: {audio.m_Length:N2} seconds · {audio.m_Channels} channel(s)\nPlay uses the native audio player; unsupported codecs can still be exported.");
                    break;
                case AssetStudio.Font font:
                    ShowFontPreview(row.Name,
                        font.m_FontData is { Length: > 0 }
                            ? $"Embedded font data · {font.m_FontData.Length:N0} bytes."
                            : "This font has no embedded data to preview or export.");
#if IOS
                    if (font.m_FontData is { Length: > 0 })
                        await ApplyEmbeddedFontPreviewAsync(font, row);
#endif
                    break;
                case AssetStudio.Shader shader:
                    var shaderSource = await Task.Run(() => AssetStudio.ShaderConverter.Convert(shader));
                    if (_selectedAsset?.Id != row.Id) return;
                    ShowTextPreview(row.Name, "Shader source",
                        shaderSource.Replace("\n", "\r\n").Replace("\0", ""));
                    break;
                case AssetStudio.Sprite sprite:
                    PreviewDetails.Text = "Cropped sprite preview with packed rotation, tight-mesh masking, and separate alpha texture applied when present.";
                    var spriteImage = await Task.Run(() => MobileAssetExporter.Export(sprite));
                    if (_selectedAsset?.Id != row.Id) return;
                    ShowTexturePreview(row.Name, ImageSource.FromFile(spriteImage),
                        (int)sprite.m_Rect.width, (int)sprite.m_Rect.height,
                        "PNG sprite crop", PreviewDetails.Text, spriteImage);
                    break;
                case AssetStudio.VideoClip video:
                    ShowVideoClipPreview(row.Name, video);
                    break;
                case AssetStudio.MovieTexture:
                    await ShowAssetDumpOrSummaryAsync(row,
                        "Movie data is available through Export in its stored OGV format.");
                    break;
                default:
                    await ShowAssetDumpOrSummaryAsync(row,
                        "Use the Dump tab to inspect serialized fields when available, or Export to save this asset.");
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
            var drawable = await Task.Run(() => new MeshPreviewDrawable(mesh));
            await Navigation.PushModalAsync(CreateModalNavigationPage(new MeshPreviewPage(mesh.m_Name, drawable)));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Mesh preview failed", ex.Message, "OK");
        }
    }

    private async void OnSceneObjectPreviewClicked(object sender, EventArgs e)
    {
        if (HierarchyCollection.SelectedItem is not HierarchyRow node)
            return;

        try
        {
            var gameObjects = GetHierarchyGameObjects(node);
            var geometries = await Task.Run(() =>
                MobileAssetExporter.GetSceneMeshGeometries(gameObjects, CancellationToken.None));
            if (geometries.Count == 0)
            {
                StatusLabel.Text = $"No renderable meshes in {node.DisplayName}";
                return;
            }

            var drawable = await Task.Run(() => new MeshPreviewDrawable(geometries));
            await Navigation.PushModalAsync(CreateModalNavigationPage(
                new MeshPreviewPage(node.DisplayName, drawable)));
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Scene object preview failed: {exception.Message}";
            await DisplayAlert("Scene object preview failed", exception.Message, "OK");
        }
    }

    private async void OnFullTexturePreviewClicked(object sender, EventArgs e)
    {
        var imagePath = _previewImagePath;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            await DisplayAlert("Texture preview",
                "The decoded preview image is no longer available. Select the asset again to regenerate it.", "OK");
            return;
        }

        try
        {
            await Navigation.PushModalAsync(CreateModalNavigationPage(new TexturePreviewPage(
                PreviewTitle.Text, imagePath, _previewImageWidth, _previewImageHeight, _previewImageFormat)));
        }
        catch (Exception exception)
        {
            await DisplayAlert("Texture preview failed", exception.Message, "OK");
        }
    }

    private async void OnVideoPlayerClicked(object sender, EventArgs e)
    {
        if (_selectedAsset?.SourceAsset is not AssetStudio.VideoClip video)
            return;
        if (!EnsureVideoPlayer())
            return;

        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        CancelLoadButton.Text = "Cancel open";
        try
        {
            SetBusy(true, "Preparing video for playback…");
            var result = await Task.Run(
                () => MobileAssetExporter.ExportForShare(
                    video,
                    MobileExportKind.Converted,
                    cancellationToken: cancellation.Token),
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.UsedRawFallback)
                throw new NotSupportedException(
                    result.Warning ?? "The video stream could not be prepared for playback.");

            if (_selectedAsset?.SourceAsset != video)
                return;

            _videoPlayer!.ShouldAutoPlay = true;
            _videoPlayer.Source = CommunityToolkit.Maui.Views.MediaSource.FromFile(result.FilePath);
            VideoPlayerHost.IsVisible = true;
            LoadVideoPlaybackButton.IsVisible = false;
            VideoPlaybackStatus.Text = "Opening video…";
            PreviewDetails.Text = "Playback uses the device's supported codecs.";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Video playback cancelled";
        }
        catch (Exception exception)
        {
            VideoPlaybackStatus.Text = $"Could not play video: {exception.Message}";
            StatusLabel.Text = VideoPlaybackStatus.Text;
            if (_showErrorMessages)
                await DisplayAlert("Video playback unavailable", exception.Message, "OK");
        }
        finally
        {
            _loadCancellation = null;
            CancelLoadButton.Text = "Cancel load";
            SetBusy(false, StatusLabel.Text);
        }
    }

    private void OnVideoMediaOpened(object? sender, EventArgs e)
    {
        VideoPlaybackStatus.Text = "Video ready";
    }

    private void OnVideoMediaFailed(object? sender, EventArgs e)
    {
        VideoPlaybackStatus.Text = "Playback failed; the codec may not be supported on this device.";
        StatusLabel.Text = VideoPlaybackStatus.Text;
    }

    private void OnVideoMediaEnded(object? sender, EventArgs e)
    {
        VideoPlaybackStatus.Text = "Playback ended";
    }

    private bool EnsureVideoPlayer()
    {
        if (_videoPlayer != null)
            return true;
#if ANDROID
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            VideoPlaybackStatus.Text = "Embedded video playback requires Android 8.0 or later.";
            StatusLabel.Text = VideoPlaybackStatus.Text;
            return false;
        }
#endif

        _videoPlayer = new CommunityToolkit.Maui.Views.MediaElement
        {
            HeightRequest = 240,
            Aspect = Aspect.AspectFit,
            ShouldShowPlaybackControls = true,
            ShouldAutoPlay = false
        };
        _videoPlayer.MediaOpened += OnVideoMediaOpened;
        _videoPlayer.MediaFailed += OnVideoMediaFailed;
        _videoPlayer.MediaEnded += OnVideoMediaEnded;
        VideoPlayerHost.Content = _videoPlayer;
        return true;
    }

    private async Task ShowAssetDumpOrSummaryAsync(AssetRow row, string details)
    {
        var dump = row.SourceAsset is AssetStudio.Object asset
            ? await Task.Run(() => asset.Dump())
            : null;
        if (_selectedAsset?.Id != row.Id)
            return;

        if (!string.IsNullOrWhiteSpace(dump))
            ShowTextPreview(row.Name, $"{row.Type} serialized data", dump, details);
        else
            ShowTextPreview(row.Name, $"{row.Type} details", BuildAssetSummary(row), details);
    }

    private static string BuildAssetSummary(AssetRow row)
    {
        var lines = new List<string>
        {
            $"Type: {row.Type}",
            $"Class ID: {row.TypeId}",
            $"Path ID: {row.PathId}",
            $"Size: {row.Size}"
        };
        if (!string.IsNullOrWhiteSpace(row.Container))
            lines.Add($"Container: {row.Container}");
        if (!string.IsNullOrWhiteSpace(row.SourceFile))
            lines.Add($"Source file: {Path.GetFileName(row.SourceFile)}");
        if (row.SourceAsset is AssetStudio.Object asset)
        {
            lines.Add($"Serialized size: {asset.byteSize:N0} bytes");
            lines.Add($"Platform: {asset.platform}");
            lines.Add($"Type tree: {(asset.serializedType?.m_Type == null ? "not available" : "available")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

#if IOS
    private string? _registeredPreviewFontPath;

    private async Task ApplyEmbeddedFontPreviewAsync(AssetStudio.Font font, AssetRow row)
    {
        var path = await Task.Run(() => MobileAssetExporter.Export(font));
        if (_selectedAsset?.Id != row.Id) return;

        var url = NSUrl.FromFilename(path);
        var registrationError = CTFontManager.RegisterFontsForUrl(url, CTFontManagerScope.Process);
        if (registrationError != null)
            throw new InvalidDataException(
                $"iOS could not register the embedded font: {registrationError.LocalizedDescription}");
        _registeredPreviewFontPath = path;

        var descriptor = CTFontManager.GetFonts(url).FirstOrDefault()
            ?? throw new InvalidDataException("The embedded font contains no readable font face.");
        var attributes = descriptor.GetAttributes();
        var fontName = attributes.Name ?? attributes.FamilyName;
        if (string.IsNullOrWhiteSpace(fontName) || UIFont.FromName(fontName, 12f) == null)
            throw new InvalidDataException("iOS registered the font but could not resolve its family name.");

        FontSpecimenSmall.FontFamily = fontName;
        FontSpecimenMedium.FontFamily = fontName;
        FontSpecimenLarge.FontFamily = fontName;
        FontSpecimenCharacters.FontFamily = fontName;
        PreviewDetails.Text += $"\nRendering embedded font: {fontName}";
    }

    private void UnregisterEmbeddedPreviewFont()
    {
        if (_registeredPreviewFontPath is not { } path)
            return;

        _registeredPreviewFontPath = null;
        var unregisterError = CTFontManager.UnregisterFontsForUrl(
            NSUrl.FromFilename(path), CTFontManagerScope.Process);
        if (unregisterError != null)
            StatusLabel.Text = $"Could not unregister previous font preview: {unregisterError.LocalizedDescription}";
    }
#endif

#if IOS || MACCATALYST
    private AVAudioPlayer? _audioPlayer;
    private async void OnAudioPlaybackRequested(object? sender, EventArgs e)
    {
        var clip = _selectedAudioClip;
        if (clip == null)
            return;

        try
        {
            if (_audioPlayer == null)
            {
                var path = await Task.Run(() => MobileAssetExporter.Export(clip));
                if (!ReferenceEquals(_selectedAudioClip, clip))
                    return;

                _audioPlayer = AVAudioPlayer.FromUrl(NSUrl.FromFilename(path), out var error)
                    ?? throw new InvalidDataException(error?.LocalizedDescription ?? "The audio format is not supported by iOS.");
                _audioPlayer.PrepareToPlay();
                _audioPlayer.NumberOfLoops = _audioLoopEnabled ? -1 : 0;
                _audioPlayer.Volume = (float)AudioVolumeSlider.Value;
            }

            _audioPlayer.Play();
            var player = _audioPlayer;
            Dispatcher.StartTimer(TimeSpan.FromMilliseconds(250), () =>
            {
                if (!ReferenceEquals(_audioPlayer, player))
                    return false;
                UpdateAudioPlaybackState(
                    player.Playing ? "Playing" : "Stopped",
                    player.CurrentTime,
                    player.Duration,
                    clip.m_Frequency);
                return player.Playing;
            });
            UpdateAudioPlaybackState("Playing", player.CurrentTime, player.Duration, clip.m_Frequency);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_selectedAudioClip, clip))
                return;
            StatusLabel.Text = $"Audio playback failed: {ex.Message}";
            await DisplayAlert("Audio playback failed", ex.Message, "OK");
        }
    }

    private void OnAudioPauseRequested(object? sender, EventArgs e)
    {
        _audioPlayer?.Pause();
        if (_audioPlayer != null && _selectedAudioClip != null)
            UpdateAudioPlaybackState("Paused", _audioPlayer.CurrentTime, _audioPlayer.Duration, _selectedAudioClip.m_Frequency);
    }

    private void OnAudioStopRequested(object? sender, EventArgs e)
    {
        var duration = _audioPlayer?.Duration ?? 0;
        _audioPlayer?.Stop();
        if (_audioPlayer != null && _selectedAudioClip != null)
            UpdateAudioPlaybackState("Stopped", 0, duration, _selectedAudioClip.m_Frequency);
    }

    private void OnAudioLoopChanged(bool enabled)
    {
        if (_audioPlayer != null)
            _audioPlayer.NumberOfLoops = enabled ? -1 : 0;
    }

    private void OnAudioSeekRequested(double seconds)
    {
        if (_audioPlayer != null)
            _audioPlayer.CurrentTime = Math.Clamp(seconds, 0, _audioPlayer.Duration);
    }

    private void OnAudioVolumeChanged(double volume)
    {
        if (_audioPlayer != null)
            _audioPlayer.Volume = (float)Math.Clamp(volume, 0, 1);
    }

    private void StopPreviewAudio()
    {
        _audioPlayer?.Stop();
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        _selectedAudioClip = null;
    }
#elif ANDROID
    private MobileAndroidAudioPlayer? _audioPlayer;

    private async void OnAudioPlaybackRequested(object? sender, EventArgs e)
    {
        var clip = _selectedAudioClip;
        if (clip == null)
            return;

        try
        {
            if (_audioPlayer is { IsPrepared: false })
            {
                _audioPlayer.Dispose();
                _audioPlayer = null;
            }

            if (_audioPlayer == null)
            {
                var path = await Task.Run(() => MobileAssetExporter.Export(clip));
                if (!ReferenceEquals(_selectedAudioClip, clip))
                    return;

                var player = new MobileAndroidAudioPlayer();
                _audioPlayer = player;
                await player.PrepareAsync(path);
                if (!ReferenceEquals(_audioPlayer, player))
                    return;

                player.Looping = _audioLoopEnabled;
                player.Volume = AudioVolumeSlider.Value;
                player.PlaybackCompleted += (_, _) =>
                {
                    if (ReferenceEquals(_audioPlayer, player))
                        UpdateAudioPlaybackState("Stopped", player.CurrentTime, player.Duration, clip.m_Frequency);
                };
            }

            _audioPlayer.Play();
            var activePlayer = _audioPlayer;
            Dispatcher.StartTimer(TimeSpan.FromMilliseconds(250), () =>
            {
                if (!ReferenceEquals(_audioPlayer, activePlayer))
                    return false;
                UpdateAudioPlaybackState(
                    activePlayer.IsPlaying ? "Playing" : activePlayer.IsPaused ? "Paused" : "Stopped",
                    activePlayer.CurrentTime,
                    activePlayer.Duration,
                    clip.m_Frequency);
                return activePlayer.IsPlaying;
            });
            UpdateAudioPlaybackState("Playing", activePlayer.CurrentTime, activePlayer.Duration, clip.m_Frequency);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_selectedAudioClip, clip))
                return;
            StatusLabel.Text = $"Audio playback failed: {ex.Message}";
            await DisplayAlert("Audio playback failed", ex.Message, "OK");
        }
    }

    private void OnAudioPauseRequested(object? sender, EventArgs e)
    {
        _audioPlayer?.Pause();
        if (_audioPlayer != null && _selectedAudioClip != null)
            UpdateAudioPlaybackState("Paused", _audioPlayer.CurrentTime, _audioPlayer.Duration, _selectedAudioClip.m_Frequency);
    }

    private void OnAudioStopRequested(object? sender, EventArgs e)
    {
        var player = _audioPlayer;
        var duration = player?.Duration ?? 0;
        player?.Stop();
        player?.Dispose();
        _audioPlayer = null;
        if (_selectedAudioClip != null)
            UpdateAudioPlaybackState("Stopped", 0, duration, _selectedAudioClip.m_Frequency);
    }

    private void OnAudioLoopChanged(bool enabled)
    {
        if (_audioPlayer != null)
            _audioPlayer.Looping = enabled;
    }

    private void OnAudioSeekRequested(double seconds) => _audioPlayer?.Seek(seconds);

    private void OnAudioVolumeChanged(double volume)
    {
        if (_audioPlayer != null)
            _audioPlayer.Volume = volume;
    }

    private void StopPreviewAudio()
    {
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        _selectedAudioClip = null;
    }
#elif WINDOWS
    private Windows.Media.Playback.MediaPlayer? _audioPlayer;

    private async void OnAudioPlaybackRequested(object? sender, EventArgs e)
    {
        var clip = _selectedAudioClip;
        if (clip == null)
            return;

        try
        {
            if (_audioPlayer == null)
            {
                var path = await Task.Run(() => MobileAssetExporter.Export(clip));
                if (!ReferenceEquals(_selectedAudioClip, clip))
                    return;

                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                if (!ReferenceEquals(_selectedAudioClip, clip))
                    return;

                var player = new Windows.Media.Playback.MediaPlayer
                {
                    Source = Windows.Media.Core.MediaSource.CreateFromStorageFile(file),
                    IsLoopingEnabled = _audioLoopEnabled,
                    Volume = AudioVolumeSlider.Value
                };
                player.MediaFailed += (_, args) =>
                {
                    var message = args.ErrorMessage;
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (ReferenceEquals(_audioPlayer, player))
                            StatusLabel.Text = $"Audio playback failed: {message}";
                    });
                };
                _audioPlayer = player;
            }

            var activePlayer = _audioPlayer
                ?? throw new InvalidOperationException("Windows audio playback was not initialized.");
            activePlayer.Play();
            Dispatcher.StartTimer(TimeSpan.FromMilliseconds(250), () =>
            {
                if (!ReferenceEquals(_audioPlayer, activePlayer))
                    return false;
                var session = activePlayer.PlaybackSession;
                var isPlaying = session.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
                UpdateAudioPlaybackState(
                    isPlaying ? "Playing" : "Stopped",
                    session.Position.TotalSeconds,
                    session.NaturalDuration.TotalSeconds,
                    clip.m_Frequency);
                return isPlaying;
            });
            var playback = activePlayer.PlaybackSession;
            UpdateAudioPlaybackState(
                "Playing", playback.Position.TotalSeconds,
                playback.NaturalDuration.TotalSeconds, clip.m_Frequency);
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_selectedAudioClip, clip))
                return;
            StatusLabel.Text = $"Audio playback failed: {exception.Message}";
            await DisplayAlert("Audio playback failed", exception.Message, "OK");
        }
    }

    private void OnAudioPauseRequested(object? sender, EventArgs e)
    {
        _audioPlayer?.Pause();
        if (_audioPlayer != null && _selectedAudioClip != null)
        {
            var session = _audioPlayer.PlaybackSession;
            UpdateAudioPlaybackState(
                "Paused", session.Position.TotalSeconds,
                session.NaturalDuration.TotalSeconds, _selectedAudioClip.m_Frequency);
        }
    }

    private void OnAudioStopRequested(object? sender, EventArgs e)
    {
        if (_audioPlayer == null)
            return;

        var session = _audioPlayer.PlaybackSession;
        var duration = session.NaturalDuration.TotalSeconds;
        _audioPlayer.Pause();
        if (session.CanSeek)
            session.Position = TimeSpan.Zero;
        if (_selectedAudioClip != null)
            UpdateAudioPlaybackState("Stopped", 0, duration, _selectedAudioClip.m_Frequency);
    }

    private void OnAudioLoopChanged(bool enabled)
    {
        if (_audioPlayer != null)
            _audioPlayer.IsLoopingEnabled = enabled;
    }

    private void OnAudioSeekRequested(double seconds)
    {
        if (_audioPlayer?.PlaybackSession is { CanSeek: true } session)
            session.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, session.NaturalDuration.TotalSeconds));
    }

    private void OnAudioVolumeChanged(double volume)
    {
        if (_audioPlayer != null)
            _audioPlayer.Volume = Math.Clamp(volume, 0, 1);
    }

    private void StopPreviewAudio()
    {
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        _selectedAudioClip = null;
    }
#else
    private async void OnAudioPlaybackRequested(object? sender, EventArgs e) =>
        await DisplayAlert("Audio playback unavailable",
            "Audio playback is unavailable on this platform. The clip can still be exported.", "OK");

    private static void OnAudioPauseRequested(object? sender, EventArgs e) { }
    private static void OnAudioStopRequested(object? sender, EventArgs e) { }
    private static void OnAudioLoopChanged(bool enabled) { }
    private static void OnAudioSeekRequested(double seconds) { }
    private static void OnAudioVolumeChanged(double volume) { }
#endif

    private static NavigationPage CreateModalNavigationPage(Page page) =>
        new(page)
        {
            BarBackgroundColor = Color.FromArgb("#E2E2E2"),
            BarTextColor = Color.FromArgb("#202020")
        };

    // Returns the current multi-selection in CollectionView order.
    public IReadOnlyList<AssetRow> GetSelectedAssets() => _selectedAssets.ToList();

    private void ClearSelectedAsset()
    {
#if IOS || MACCATALYST || ANDROID || WINDOWS
        StopPreviewAudio();
#endif
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
        if (_browserTab != "Scene Hierarchy"
            || e.CurrentSelection.FirstOrDefault() is not HierarchyRow node)
        {
            return;
        }

        var gameObjects = GetHierarchyGameObjects(node);
        var meshCount = gameObjects.SelectMany(GetMeshes).Distinct().Count();
        _showingEmptyPreview = false;
        ResetPreviewSurface();
        PreviewTitle.Text = node.DisplayName;
        PreviewKind.Text = "Scene Hierarchy Object";
        ModelPreviewFrame.IsVisible = true;
        ModelPreviewInfo.Text = meshCount > 0
            ? $"{gameObjects.Count} object(s) in this subtree · {meshCount} mesh(es). Preview applies scene transforms and the stored skin/blend-shape pose."
            : $"{gameObjects.Count} object(s) in this subtree · no renderable meshes.";
        OpenSceneObjectPreviewButton.IsVisible = meshCount > 0;
        PreviewDetails.Text = "Check objects to export or use Model → Preview checked scene objects for a combined preview.";
        RefreshPreviewSurfaceVisibility();
        StatusLabel.Text = $"Selected scene object: {node.DisplayName} · {meshCount} mesh(es)";
    }

    private void OnClassSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyClassFilter();

    private void ApplyClassFilter()
    {
        var query = ClassSearch?.Text?.Trim() ?? string.Empty;
        var filtered = _classes.Where(item => query.Length == 0 ||
            Contains(item.Name, query) || Contains(item.Id, query) ||
            Contains(item.Version, query) || Contains(item.Summary, query) ||
            Contains(item.StructureText, query));
        filtered = _classSortKey switch
        {
            "id" => filtered.OrderBy(item => ParseClassId(item.Id)).ThenBy(item => item.Id, StringComparer.OrdinalIgnoreCase),
            "version" => filtered.OrderBy(item => item.Version, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => ParseClassId(item.Id))
        };
        if (_classSortDescending) filtered = filtered.Reverse();
        var selectedClass = ClassesCollection?.SelectedItem as ClassRow;
        _visibleClasses.Clear();
        foreach (var item in filtered) _visibleClasses.Add(item);
        if (selectedClass != null && _visibleClasses.Contains(selectedClass))
            ClassesCollection.SelectedItem = selectedClass;
        else if (ClassesCollection != null)
            ClassesCollection.SelectedItem = null;
        ClassEmptyLabel.Text = _classes.Count == 0
            ? "No asset classes were loaded."
            : "No classes match the current search.";
        if (StatusLabel != null && _browserTab == "Asset Classes")
            StatusLabel.Text = $"{_visibleClasses.Count} / {_classes.Count} classes";
    }

    private static long ParseClassId(string value) => long.TryParse(value, out var id) ? id : long.MaxValue;

    private void ChangeClassSort(string key)
    {
        if (_classSortKey == key) _classSortDescending = !_classSortDescending;
        else { _classSortKey = key; _classSortDescending = false; }
        ApplyClassFilter();
        if (_browserTab == "Asset Classes")
            StatusLabel.Text = $"{_visibleClasses.Count} / {_classes.Count} classes · sorted by {_classSortKey}{(_classSortDescending ? " ↓" : " ↑")}";
    }

    private void OnClassSortNameClicked(object sender, EventArgs e) => ChangeClassSort("name");
    private void OnClassSortIdClicked(object sender, EventArgs e) => ChangeClassSort("id");
    private void OnClassSortVersionClicked(object sender, EventArgs e) => ChangeClassSort("version");

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
            var dump = await Task.Run(() =>
            {
                if (asset is not AssetStudio.MonoBehaviour behaviour)
                    return asset.Dump() ?? string.Empty;

                var text = _monoBehaviourAssemblyService.GetDump(behaviour, out var error);
                return text ?? throw new NotSupportedException(
                    error ?? "The assembly-derived MonoBehaviour dump could not be read.");
            });
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
        private readonly List<TypeFilterRow> _allRows;
        private readonly TaskCompletionSource<HashSet<string>?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SearchBar _search = new()
        {
            Placeholder = "Search asset types…",
            FontSize = 14,
            TextColor = Color.FromArgb("#202020"),
            PlaceholderColor = Color.FromArgb("#777777"),
            BackgroundColor = Colors.White
        };
        private readonly CollectionView _items = new() { SelectionMode = SelectionMode.None };
        private readonly Label _summary = new() { FontSize = 12, TextColor = Color.FromArgb("#666666") };
        private bool _updatingRows;
        private bool _finished;

        public AssetTypeFilterPage(string[] types, HashSet<string>? current)
        {
            _selected = current == null ? new HashSet<string>(types, StringComparer.Ordinal)
                                        : new HashSet<string>(current, StringComparer.Ordinal);
            Title = "Filter Type";
            BackgroundColor = Color.FromArgb("#FFFFFF");

            _allRows = types.Select(type => new TypeFilterRow(type, _selected.Contains(type))).ToList();
            foreach (var row in _allRows)
            {
                row.SelectionChanged += changed =>
                {
                    if (_updatingRows) return;
                    if (changed.IsSelected) _selected.Add(changed.Type);
                    else _selected.Remove(changed.Type);
                    UpdateSummary();
                };
            }

            _items.ItemTemplate = new DataTemplate(() =>
            {
                var check = new CheckBox { Color = Color.FromArgb("#2878C8"), VerticalOptions = LayoutOptions.Center };
                check.SetBinding(CheckBox.IsCheckedProperty,
                    new Binding(nameof(TypeFilterRow.IsSelected), BindingMode.TwoWay));
                var label = new Label
                {
                    TextColor = Color.FromArgb("#202020"),
                    VerticalTextAlignment = TextAlignment.Center,
                    FontSize = 14
                };
                label.SetBinding(Label.TextProperty, nameof(TypeFilterRow.Type));
                var row = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Star)
                    },
                    Padding = new Thickness(4, 3)
                };
                row.Add(check);
                row.Add(label, 1);
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) =>
                {
                    if (label.BindingContext is TypeFilterRow item)
                        item.IsSelected = !item.IsSelected;
                };
                label.GestureRecognizers.Add(tap);
                return row;
            });
            _items.EmptyView = new Label
            {
                Text = "No asset types match this search.",
                TextColor = Color.FromArgb("#666666"),
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            };
            _search.TextChanged += (_, _) => ApplyFilter();

            var selectAll = new Button { Text = "Select all", BackgroundColor = Color.FromArgb("#E0E0E0"), TextColor = Color.FromArgb("#202020") };
            selectAll.Clicked += (_, _) => SetAll(true);
            var clearAll = new Button { Text = "Clear all", BackgroundColor = Color.FromArgb("#E0E0E0"), TextColor = Color.FromArgb("#202020") };
            clearAll.Clicked += (_, _) => SetAll(false);
            var apply = new Button { Text = "Apply", BackgroundColor = Color.FromArgb("#E2E2E2"), TextColor = Color.FromArgb("#202020") };
            apply.Clicked += async (_, _) => await Finish(true);
            var cancel = new Button { Text = "Cancel", BackgroundColor = Color.FromArgb("#E0E0E0"), TextColor = Color.FromArgb("#202020") };
            cancel.Clicked += async (_, _) => await Finish(false);
            var top = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 8 };
            top.Add(selectAll); top.Add(clearAll, 1);
            var bottom = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 8, Padding = 12 };
            bottom.Add(cancel); bottom.Add(apply, 1);
            Content = new Grid
            {
                Padding = new Thickness(12, 8),
                RowSpacing = 8,
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto)
                },
                Children =
                {
                    new Label { Text = "Filter asset types", FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#202020") },
                    _search,
                    top,
                    _summary,
                    _items,
                    bottom
                }
            };
            Grid.SetRow(_search, 1);
            Grid.SetRow(top, 2);
            Grid.SetRow(_summary, 3);
            Grid.SetRow(_items, 4);
            Grid.SetRow(bottom, 5);
            ApplyFilter();
        }

        public Task<HashSet<string>?> Completion => _completion.Task;

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            if (!_finished)
                _completion.TrySetResult(null);
        }

        private void SetAll(bool enabled)
        {
            _updatingRows = true;
            foreach (var row in _allRows)
                row.IsSelected = enabled;
            _updatingRows = false;
            _selected.Clear();
            if (enabled)
                foreach (var row in _allRows)
                    _selected.Add(row.Type);
            UpdateSummary();
        }

        private void ApplyFilter()
        {
            var query = _search.Text?.Trim() ?? string.Empty;
            _items.ItemsSource = _allRows.Where(row =>
                query.Length == 0 || Contains(row.Type, query)).ToList();
            UpdateSummary();
        }

        private void UpdateSummary() =>
            _summary.Text = $"{_selected.Count} of {_allRows.Count} types selected · " +
                $"{_items.ItemsSource?.Cast<TypeFilterRow>().Count() ?? 0} shown";

        private sealed class TypeFilterRow : BindableObject
        {
            private bool _isSelected;

            public TypeFilterRow(string type, bool isSelected)
            {
                Type = type;
                _isSelected = isSelected;
            }

            public string Type { get; }
            public event Action<TypeFilterRow>? SelectionChanged;

            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    OnPropertyChanged();
                    SelectionChanged?.Invoke(this);
                }
            }
        }

        private async Task Finish(bool apply)
        {
            if (_finished) return;
            _finished = true;
            var result = apply
                ? new HashSet<string>(_selected, StringComparer.Ordinal)
                : null;
            try
            {
                await Navigation.PopModalAsync();
                _completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
                throw;
            }
        }
    }

    // Progress comes from the parser; this page never estimates completion.
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
        public bool IsExportable { get; init; }
        public string? HierarchyNodeId { get; set; }
        // Numeric ClassIDType value for the original XML Type/@id attribute.
        public int TypeId { get; init; }
        public string PathId { get; init; } = "";
        public string Size { get; init; } = "";
        public string DisplaySize => FormatByteSize(Size);
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
            DisplaySize
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string FormatByteSize(string value)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
            return value;
        if (bytes < 1024)
            return $"{bytes:N0} B";

        var amount = (double)bytes;
        var units = new[] { "KB", "MB", "GB", "TB" };
        foreach (var unit in units)
        {
            amount /= 1024;
            if (amount < 1024 || unit == units[^1])
                return $"{amount.ToString("0.#", CultureInfo.CurrentCulture)} {unit}";
        }
        return $"{bytes:N0} B";
    }

    // Adapt a parsed core object into the row shape shared by the mobile UI.
    public static AssetRow CreateAssetRow(
        object sourceAsset,
        string name,
        string container,
        string type,
        long pathId,
        long byteSize,
        int typeId = 0,
        string sourceFile = "",
        bool isExportable = true)
    {
        ArgumentNullException.ThrowIfNull(sourceAsset);
        return new AssetRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name ?? string.Empty,
            Container = container ?? string.Empty,
            Type = type ?? string.Empty,
            TypeId = typeId,
            IsExportable = isExportable,
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
        public object? SourceAsset { get; init; }
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
