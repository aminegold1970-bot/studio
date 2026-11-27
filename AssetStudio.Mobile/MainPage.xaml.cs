using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Color = Microsoft.Maui.Graphics.Color;
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
    private static readonly IReadOnlyDictionary<string, int[]> MenuSeparatorIndices =
        new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            ["Export"] = new[] { 2, 3, 5 },
            ["Model"] = new[] { 2, 4, 5 }
        };

    private static readonly IReadOnlyDictionary<string, string> OptionMenuHints =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Display all assets"] =
                "Check this option will display all types assets. Not extractable assets can export the RAW file.",
            ["Enable preview"] =
                "Toggle the loading and preview of readable assets, such as images, sounds, text, etc.\n" +
                "Disable preview if you have performance or compatibility issues.",
            ["Display asset infromation"] =
                "Toggle the overlay that shows information about each asset, eg. image size, format, audio bitrate, etc."
        };

    private CancellationTokenSource? _loadCancellation;
    private bool _assetLoadActive;
    private IDispatcherTimer? _assetFilterTimer;
    private const int AssetFilterDebounceMilliseconds = 800;
    private bool _suppressFilterTextChanged;
    private LoadMetadata? _loadMetadata;
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
    private readonly ObservableCollection<ClassGroup> _visibleClassGroups = new();
    private string _classSortKey = "version";
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
    private bool _shareAfterExport = true;
    private bool _restoreTextAssetExtension = true;
    private MobileArchiveGrouping _archiveGrouping = MobileArchiveGrouping.None;
    private string? _previewImagePath;
    private int _previewImageWidth;
    private int _previewImageHeight;
    private string _previewImageFormat = string.Empty;
    private const string TextureChannelNames = "BGRA";
    private readonly bool[] _textureChannels = { true, true, true, true };
    private string? _previewTextureMaskedPath;
    private CommunityToolkit.Maui.Views.MediaElement? _videoPlayer;
    private MeshPreviewDrawable? _inlineMeshDrawable;
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
    // Filter Type mirrors the desktop menu: "All" is a mode, not the set of
    // every type, so the menu stays open while individual types are checked.
    private bool _filterMenuOpen;
    private string[] _filterMenuTypes = Array.Empty<string>();
    private readonly Dictionary<string, Label> _filterMenuLabels = new(StringComparer.Ordinal);

    public MainPage()
    {
        InitializeComponent();
        // Desktop installs GUILogger at startup so parser errors honor the
        // Debug -> Show error message toggle and other events reach the status strip.
        AssetStudio.Logger.Default = new MobileLogger(this);
        _assetFilterTimer = Dispatcher.CreateTimer();
        _assetFilterTimer.Interval = TimeSpan.FromMilliseconds(AssetFilterDebounceMilliseconds);
        _assetFilterTimer.IsRepeating = false;
        _assetFilterTimer.Tick += OnAssetFilterTimerTick;
        _showAllAssets = Preferences.Get("AssetStudio.DisplayAllAssets", false);
        _previewEnabled = Preferences.Get("AssetStudio.EnablePreview", true);
        _displayAssetInformation = Preferences.Get("AssetStudio.DisplayAssetInformation", true);
        _showErrorMessages = Preferences.Get("AssetStudio.ShowErrorMessages", true);
        PreviewDetails.IsVisible = _displayAssetInformation;
        // Desktop loads every export option from settings at startup, including
        // the default export kind and the type-name grouping default.
        ApplyExportSettings(MobileExportSettings.Load(), announce: false);
        for (var index = 0; index < _textureChannels.Length; index++)
            _textureChannels[index] = Preferences.Get(TextureChannelsPage.PreferenceKeys[index], true);
        _assetLoader.MainPageHost = (assets, hierarchy, classes, metadata) =>
        {
            SetAssets(assets);
            SetHierarchy(hierarchy);
            SetClasses(classes);
            SetLoadMetadata(metadata);
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
        ClassesCollection.ItemsSource = _visibleClassGroups;
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

        // Desktop shows the mesh in the preview pane immediately; reuse the
        // full-screen drawable inline and keep the modal viewer for details.
        var inlineMeshPan = new PanGestureRecognizer();
        inlineMeshPan.PanUpdated += (_, args) =>
        {
            _inlineMeshDrawable?.UpdatePan(args);
            InlineMeshView.Invalidate();
        };
        InlineMeshView.GestureRecognizers.Add(inlineMeshPan);
        var inlineMeshPinch = new PinchGestureRecognizer();
        inlineMeshPinch.PinchUpdated += (_, args) =>
        {
            _inlineMeshDrawable?.UpdatePinch(args);
            InlineMeshView.Invalidate();
        };
        InlineMeshView.GestureRecognizers.Add(inlineMeshPinch);
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
    // immediately copied into the app cache before the provider URL expires.
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
            // Desktop routes a single dropped directory to LoadFolder and
            // multiple dropped paths to LoadFiles.
            if (paths.Count == 1 && Directory.Exists(paths[0]))
            {
                var folderPath = await _documentLibrary.ImportFolderAsync(paths[0]);
#if IOS
                AssetStudioDropBridge.CleanupTemporaryCopy(paths[0]);
#endif
                await LoadPersistentFolderAsync(folderPath, "Dropped folder");
                return;
            }

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
            await ShowErrorAsync("Drop import failed", ex.Message);
        }
    }

    private async void OnDropFailed(string message)
    {
        StatusLabel.Text = $"Drop failed: {message}";
        await ShowErrorAsync("Could not import dropped file", message);
    }

    private void OnDropTargetChanged(bool isTargeted) =>
        MainThread.BeginInvokeOnMainThread(() => DropTargetOverlay.IsVisible = isTargeted);

    private async Task LoadPersistentFilesAsync(IReadOnlyList<FileResult> selected, string sourceLabel)
    {
        if (_assetLoadActive || _loadCancellation != null)
        {
            StatusLabel.Text = "Wait for the current operation to finish before loading assets.";
            return;
        }

        _assetLoadActive = true;
        _lastLoadedFiles = selected.ToList();
        _lastLoadedFolderPath = null;
        _lastLoadSourceLabel = sourceLabel;
        ResetLoadedWorkspace();
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        SetBusy(true, $"Loading {selected.Count} saved file(s)…");
        var progress = new Progress<AssetLoadProgress>(update =>
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), update.Message);
        });
        try
        {
            await _assetLoader.LoadFilesAsync(selected, progress, cancellation.Token);
            SetBusy(false, BuildDesktopLoadStatus(sourceLabel));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetBusy(false, "Loading cancelled");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Load failed: {ex.Message}");
            await LogErrorAsync("Asset loading failed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
            _assetLoadActive = false;
        }
    }

    private async Task LoadPersistentFolderAsync(string folderPath, string sourceLabel)
    {
        if (_assetLoadActive || _loadCancellation != null)
        {
            StatusLabel.Text = "Wait for the current operation to finish before loading assets.";
            return;
        }

        _assetLoadActive = true;
        _lastLoadedFolderPath = folderPath;
        _lastLoadedFiles = Array.Empty<FileResult>();
        _lastLoadSourceLabel = sourceLabel;

        ResetLoadedWorkspace();
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        SetBusy(true, $"Loading folder {Path.GetFileName(Path.TrimEndingDirectorySeparator(folderPath))}…");
        var progress = new Progress<AssetLoadProgress>(update =>
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), update.Message);
        });
        try
        {
            await _assetLoader.LoadFolderAsync(folderPath, progress, cancellation.Token);
            SetBusy(false, BuildDesktopLoadStatus(sourceLabel));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetBusy(false, "Folder loading cancelled");
        }
        catch (Exception exception)
        {
            SetBusy(false, $"Folder load failed: {exception.Message}");
            await LogErrorAsync("Folder loading failed", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
            _assetLoadActive = false;
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

            for (var index = 0; index < _hierarchyRoots.Count; index++)
                AddVisibleHierarchyNode(_hierarchyRoots[index], 0, "", index == _hierarchyRoots.Count - 1, new HashSet<HierarchyRow>());
            _hierarchySearchResults.Clear();
            _nextHierarchySearchResult = 0;
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

    // Render the visible nodes as a WinForms-style tree: ancestor guide columns
    // plus a +/- expander and a checkbox, matching the desktop scene hierarchy.
    private void AddVisibleHierarchyNode(
        HierarchyRow node, int depth, string ancestorPrefix, bool isLastChild, HashSet<HierarchyRow> visited)
    {
        if (!visited.Add(node))
            return;
        node.Depth = depth;
        // File nodes are the TreeView roots and have no connector of their own.
        var isRoot = depth == 0;
        node.TreePrefix = isRoot ? "" : ancestorPrefix + (isLastChild ? "└─ " : "├─ ");
        _hierarchy.Add(node);
        if (!node.IsExpanded)
            return;

        var childPrefix = isRoot ? "" : ancestorPrefix + (isLastChild ? "   " : "│  ");
        for (var index = 0; index < node.Children.Count; index++)
            AddVisibleHierarchyNode(node.Children[index], depth + 1, childPrefix,
                index == node.Children.Count - 1, visited);
    }

    private void RebuildVisibleHierarchy()
    {
        _hierarchy.Clear();
        var visited = new HashSet<HierarchyRow>();
        for (var index = 0; index < _hierarchyRoots.Count; index++)
            AddVisibleHierarchyNode(_hierarchyRoots[index], 0, "", index == _hierarchyRoots.Count - 1, visited);
    }

    private void OnHierarchyExpandClicked(object sender, EventArgs e)
    {
        if (sender is not Button button || button.BindingContext is not HierarchyRow node || !node.HasChildren)
            return;
        node.IsExpanded = !node.IsExpanded;
        RebuildVisibleHierarchy();
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
            {
                var versionCount = incoming
                    .Select(item => item.Version)
                    .Where(version => !string.IsNullOrWhiteSpace(version))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                StatusLabel.Text = $"Asset Classes · {_classes.Count} classes · {versionCount} Unity version(s)";
            }
        }

        if (MainThread.IsMainThread) Apply();
        else MainThread.BeginInvokeOnMainThread(Apply);
    }

    // Mirrors the desktop window title after a load: product name, Unity
    // version, and target platform. The nav bar is hidden on mobile, so the
    // desktop-equivalent title is shown as a compact line above the status.
    public void SetLoadMetadata(LoadMetadata metadata)
    {
        _loadMetadata = metadata;
        void Apply()
        {
            LoadInfoLabel.IsVisible = true;
            var productName = string.IsNullOrWhiteSpace(metadata.ProductName)
                ? "no productName"
                : metadata.ProductName;
            LoadInfoLabel.Text = $"AssetStudioGUI · {productName} · {metadata.UnityVersion} · {metadata.TargetPlatform}";
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
        SetAssetMetadata(CreateTextureMetadataRows(width, height, format, filterMode,
            anisotropicLevel, mipMapBias, wrapMode, channels));
    }

    public void SetTextureMetadata(AssetStudio.Texture2D texture)
    {
        if (texture == null)
        {
            SetAssetMetadata(Array.Empty<MetadataRow>());
            return;
        }

        var settings = texture.m_TextureSettings;
        SetAssetMetadata(CreateTextureMetadataRows(
            texture.m_Width,
            texture.m_Height,
            texture.m_TextureFormat.ToString(),
            DescribeTextureFilterMode(settings?.m_FilterMode),
            settings?.m_Aniso ?? 0,
            settings?.m_MipBias ?? 0f,
            DescribeTextureWrapMode(settings?.m_WrapMode),
            DescribeEnabledTextureChannels()));
    }

    private static IReadOnlyList<MetadataRow> CreateTextureMetadataRows(int width, int height, string format,
        string filterMode, int anisotropicLevel, float mipMapBias, string wrapMode, string channels) =>
        new[]
        {
            new MetadataRow("Width", width.ToString()),
            new MetadataRow("Height", height.ToString()),
            new MetadataRow("Format", format),
            new MetadataRow("Filter Mode", filterMode),
            new MetadataRow("Anisotropic level", anisotropicLevel.ToString()),
            new MetadataRow("Mip map bias", mipMapBias.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new MetadataRow("Wrap mode", wrapMode),
            new MetadataRow("Channels", channels)
        };

    private static string DescribeTextureFilterMode(int? mode) => mode switch
    {
        0 => "Point",
        1 => "Bilinear",
        2 => "Trilinear",
        null => "Unknown",
        _ => $"Unknown ({mode.Value})"
    };

    private static string DescribeTextureWrapMode(int? mode) => mode switch
    {
        0 => "Repeat",
        1 => "Clamp",
        null => "Unknown",
        _ => $"Unknown ({mode.Value})"
    };

    private string DescribeEnabledTextureChannels()
    {
        var enabled = new string[TextureChannelNames.Length];
        var count = 0;
        for (var index = 0; index < _textureChannels.Length; index++)
        {
            if (_textureChannels[index])
                enabled[count++] = TextureChannelNames[index].ToString();
        }
        return count == 0 ? "None" : string.Join(string.Empty, enabled, 0, count);
    }

    private (string Path, bool Temporary) ApplyTextureChannelMask(string imagePath)
    {
        if (_textureChannels.All(channel => channel) || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return (imagePath, false);

        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(imagePath);
            var validChannel = _textureChannels.Count(channel => channel);
            var disabledColor = validChannel == 1 && _textureChannels[3] ? byte.MaxValue : byte.MinValue;
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        ref var pixel = ref row[x];
                        if (!_textureChannels[0]) pixel.B = disabledColor;
                        if (!_textureChannels[1]) pixel.G = disabledColor;
                        if (!_textureChannels[2]) pixel.R = disabledColor;
                        if (!_textureChannels[3]) pixel.A = byte.MaxValue;
                    }
                }
            });
            var maskedPath = Path.Combine(Path.GetTempPath(), $"assetstudio-channels-{Guid.NewGuid():N}.png");
            image.SaveAsPng(maskedPath);
            return (maskedPath, true);
        }
        catch
        {
            return (imagePath, false);
        }
    }

    private static void TryDeletePreviewFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
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
        string format, string additionalInfo = "", string? imagePath = null,
        IEnumerable<MetadataRow>? metadata = null, string? temporaryPreviewPath = null,
        bool supportsChannelToggles = false)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            _previewImagePath = imagePath;
            _previewTextureMaskedPath = temporaryPreviewPath;
            _previewImageWidth = width;
            _previewImageHeight = height;
            _previewImageFormat = format ?? string.Empty;
            PreviewTitle.Text = title;
            PreviewKind.Text = "Texture / Sprite";
            ImagePreviewFrame.IsVisible = image != null;
            AssetImagePreview.Source = image;
            TextureActionsRow.IsVisible = supportsChannelToggles;
            TextureChannelsButton.Text = $"Channels: {DescribeEnabledTextureChannels()}";
            OpenFullTexturePreviewButton.IsVisible = !string.IsNullOrWhiteSpace(imagePath);
            var metadataRows = metadata?.Where(row => row != null && !string.IsNullOrWhiteSpace(row.Key)).ToList();
            if (metadataRows is { Count: > 0 })
                SetAssetMetadata(metadataRows);
            else
                SetAssetMetadata(new[] { new MetadataRow("Width", width.ToString()),
                    new MetadataRow("Height", height.ToString()), new MetadataRow("Format", format ?? string.Empty) });
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

    private static string DescribeAudioCompressionFormat(AssetStudio.AudioClip audio)
    {
        if (audio == null)
            return "Unknown";

        if (audio.version is { Length: > 0 } version && version[0] < 5)
        {
            return audio.m_Type switch
            {
                AssetStudio.FMODSoundType.ACC => "Acc",
                AssetStudio.FMODSoundType.AIFF => "AIFF",
                AssetStudio.FMODSoundType.IT => "Impulse tracker",
                AssetStudio.FMODSoundType.MOD => "Protracker / Fasttracker MOD",
                AssetStudio.FMODSoundType.MPEG => "MP2/MP3 MPEG",
                AssetStudio.FMODSoundType.OGGVORBIS => "Ogg vorbis",
                AssetStudio.FMODSoundType.S3M => "ScreamTracker 3",
                AssetStudio.FMODSoundType.WAV => "Microsoft WAV",
                AssetStudio.FMODSoundType.XM => "FastTracker 2 XM",
                AssetStudio.FMODSoundType.XMA => "Xbox360 XMA",
                AssetStudio.FMODSoundType.VAG => "PlayStation Portable ADPCM",
                AssetStudio.FMODSoundType.AUDIOQUEUE => "iPhone",
                _ => "Unknown"
            };
        }

        return audio.m_CompressionFormat switch
        {
            AssetStudio.AudioCompressionFormat.PCM => "PCM",
            AssetStudio.AudioCompressionFormat.Vorbis => "Vorbis",
            AssetStudio.AudioCompressionFormat.ADPCM => "ADPCM",
            AssetStudio.AudioCompressionFormat.MP3 => "MP3",
            AssetStudio.AudioCompressionFormat.PSMVAG => "PlayStation Portable ADPCM",
            AssetStudio.AudioCompressionFormat.HEVAG => "PSVita ADPCM",
            AssetStudio.AudioCompressionFormat.XMA => "Xbox360 XMA",
            AssetStudio.AudioCompressionFormat.AAC => "AAC",
            AssetStudio.AudioCompressionFormat.GCADPCM => "Nintendo 3DS/Wii DSP",
            AssetStudio.AudioCompressionFormat.ATRAC9 => "PSVita ATRAC9",
            _ => "Unknown"
        };
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
            AudioCurrentTime.Text = "0:00.0";
            AudioDuration.Text = "0:00.0";
            _audioLoopEnabled = false;
            SetAudioLoopVisualState();
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

    public void ShowModelPreview(string title, string details) =>
        ShowModelPreview(title, details, null, sceneObject: false);

    private void ShowModelPreview(
        string title,
        string details,
        MeshPreviewDrawable? suppliedDrawable,
        bool sceneObject)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _showingEmptyPreview = false;
            ResetPreviewSurface();
            PreviewTitle.Text = title;
            PreviewKind.Text = sceneObject ? "Scene Object" : "Mesh / Model";
            ModelPreviewFrame.IsVisible = true;
            ModelPreviewInfo.Text = string.IsNullOrWhiteSpace(details)
                ? "A wireframe preview is available for individual Mesh assets." : details;
            if (suppliedDrawable != null)
            {
                SetInlineMeshDrawable(suppliedDrawable, "Unable to preview this scene object.");
            }
            else if (_selectedAsset?.SourceAsset is AssetStudio.Mesh mesh)
            {
                try
                {
                    var drawable = new MeshPreviewDrawable(mesh);
                    SetInlineMeshDrawable(drawable, "Unable to preview this mesh. The asset can still be exported.");
                }
                catch
                {
                    SetInlineMeshDrawable(null, "Unable to preview this mesh. The asset can still be exported.");
                }
            }
            OpenMeshPreviewButton.IsVisible = !sceneObject && _selectedAsset?.SourceAsset is AssetStudio.Mesh;
            OpenSceneObjectPreviewButton.IsVisible = sceneObject;
            PreviewDetails.Text = "";
            RefreshPreviewSurfaceVisibility();
        });
    }

    private void SetInlineMeshDrawable(MeshPreviewDrawable? drawable, string failureMessage)
    {
        _inlineMeshDrawable = drawable;
        InlineMeshView.Drawable = drawable;
        InlineMeshView.IsVisible = drawable != null;
        if (drawable != null)
            InlineMeshView.Invalidate();
        else
            ModelPreviewInfo.Text = failureMessage;
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
        TextureActionsRow.IsVisible = false;
        TextureChannelsButton.IsVisible = false;
        OpenFullTexturePreviewButton.IsVisible = false;
        if (_videoPlayer?.Source != null)
            _videoPlayer.Stop();
        _videoPlayer = null;
        VideoPlayerHost.Content = null;
        VideoPlayerHost.IsVisible = false;
        VideoPreviewFrame.IsVisible = false;
        LoadVideoPlaybackButton.IsVisible = false;
        VideoPlaybackStatus.Text = string.Empty;
        TryDeletePreviewFile(_previewTextureMaskedPath);
        _previewTextureMaskedPath = null;
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
        _inlineMeshDrawable = null;
        InlineMeshView.Drawable = null;
        InlineMeshView.IsVisible = false;
        OpenMeshPreviewButton.IsVisible = false;
        OpenSceneObjectPreviewButton.IsVisible = false;
        AssetImagePreview.Source = null;
        AssetTextPreview.Text = string.Empty;
        FontSpecimenTiny.FontFamily = null;
        FontSpecimenSmall.FontFamily = null;
        FontSpecimenMedium.FontFamily = null;
        FontSpecimenLarge.FontFamily = null;
        FontSpecimenXLarge.FontFamily = null;
        FontSpecimenHuge.FontFamily = null;
        FontSpecimenSize60.FontFamily = null;
        FontSpecimenSize72.FontFamily = null;
        AudioPreviewInfo.Text = string.Empty;
        AudioPlaybackStatus.Text = "Stopped";
        AudioFrequency.Text = "— Hz";
        _updatingAudioPosition = true;
        AudioSeekSlider.Value = 0;
        _updatingAudioPosition = false;
        AudioCurrentTime.Text = "0:00.0";
        AudioDuration.Text = "0:00.0";
        AudioPauseButton.Text = "Pause";
        SetAudioLoopVisualState();
    }

    // Called by the host audio backend as playback progresses. Times are seconds.
    public void UpdateAudioPlaybackState(string status, double positionSeconds,
        double durationSeconds, int sampleRateHz)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var playbackStatus = string.IsNullOrWhiteSpace(status) ? "Stopped" : status;
            AudioPlaybackStatus.Text = playbackStatus;
            AudioPauseButton.Text = playbackStatus == "Paused" ? "Resume" : "Pause";
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
        var tenths = (int)Math.Floor(Math.Max(0, seconds) * 10);
        return $"{tenths / 600}:{tenths / 10 % 60:00}.{tenths % 10}";
    }

    private void SetAudioLoopVisualState()
    {
        AudioLoopButton.Text = "Loop";
        AudioLoopButton.BackgroundColor = _audioLoopEnabled ? Color.FromArgb("#2878C8") : Color.FromArgb("#E8E8E8");
        AudioLoopButton.TextColor = _audioLoopEnabled ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#202020");
    }

    private void OnAudioPlayClicked(object sender, EventArgs e) => AudioPlayRequested?.Invoke(this, EventArgs.Empty);
    private void OnAudioPauseClicked(object sender, EventArgs e) => AudioPauseRequested?.Invoke(this, EventArgs.Empty);
    private void OnAudioStopClicked(object sender, EventArgs e) => AudioStopRequested?.Invoke(this, EventArgs.Empty);

    private void OnAudioLoopClicked(object sender, EventArgs e)
    {
        _audioLoopEnabled = !_audioLoopEnabled;
        SetAudioLoopVisualState();
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

    // Always persist the error, even when popups are off. The Debug toggle only
    // controls whether the user also sees the message immediately.
    private async Task ShowErrorAsync(string title, string message)
    {
        AssetErrorLog.Write(title, message);
        if (_showErrorMessages)
            await DisplayAlert(title, message, "OK");
    }

    // Error popups that the desktop does not gate behind Show error message
    // are still saved, while their display behavior stays unchanged.
    private async Task LogErrorAsync(string title, string message)
    {
        AssetErrorLog.Write(title, message);
        await DisplayAlert(title, message, "OK");
    }

    private async void OnFileMenuClicked(object sender, EventArgs e)
    {
        _menuAnchor = sender as Button;
        var choice = await Choose("File", "Load file", "Load multiple files", "Load folder",
            "Extract file", "Extract folder");
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
            case "Extract file":
                await ExtractFilesAsync();
                break;
            case "Extract folder":
                await ExtractFolderAsync();
                break;
        }
    }

    private async Task<List<FileResult>> PickFilesForExtractionAsync(
        IProgress<AssetLoadProgress> progress,
        CancellationToken cancellationToken)
    {
#if IOS
        // Use the exact same native Files picker as Load file. This avoids the
        // MAUI picker callback issue on iOS and keeps selection behavior identical.
        await WaitForActionSheetDismissalAsync(cancellationToken);
        return await AssetStudioNativeFilePicker.PickAsync(
            allowMultiple: true, progress: progress, cancellationToken: cancellationToken);
#else
        cancellationToken.ThrowIfCancellationRequested();
        var picked = await FilePicker.Default.PickMultipleAsync(
            new PickOptions { PickerTitle = "Select files to extract" });
        return picked?.ToList() ?? new List<FileResult>();
#endif
    }

    private async Task ExtractFilesAsync()
    {
        if (_loadCancellation != null)
        {
            StatusLabel.Text = "Wait for the current operation to finish before extracting files.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var progress = new Progress<AssetLoadProgress>(update =>
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), update.Message);
        });
        try
        {
            SetBusy(true, "Choose files to extract…");
            var sources = await PickFilesForExtractionAsync(progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (sources.Count == 0)
            {
                SetBusy(false, "No files selected.");
                return;
            }

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
            SetBusy(false, $"Finished extracting {CountArchiveEntries(archivePath)} files.");
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "File extraction cancelled.");
        }
        catch (Exception exception)
        {
            SetBusy(false, $"File extraction failed: {exception.Message}");
            await ShowErrorAsync("File extraction failed", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
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
            SetBusy(false, $"Finished extracting {CountArchiveEntries(archivePath)} files.");
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Folder extraction cancelled.");
        }
        catch (Exception exception)
        {
            SetBusy(false, $"Folder extraction failed: {exception.Message}");
            await ShowErrorAsync("Folder extraction failed", exception.Message);
        }
        finally
        {
            _loadCancellation = null;
        }
    }

    private async Task PickAssetFolderAsync()
    {
        if (_loadCancellation != null)
        {
            StatusLabel.Text = "Wait for the current operation to finish before selecting a folder.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        SetBusy(true, "Choose a Unity asset folder…");
        var progress = new Progress<AssetLoadProgress>(update =>
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), update.Message);
        });
        try
        {
            var folderPath = await MobileFolderPicker.PickAsync(
                _documentLibrary, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                SetBusy(false, "No folder selected.");
                return;
            }

            await LoadPersistentFolderAsync(folderPath, "Loaded folder");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetBusy(false, "Folder selection or import cancelled.");
        }
        catch (Exception exception)
        {
            SetBusy(false, "Ready");
            StatusLabel.Text = $"Folder selection failed: {exception.Message}";
            await ShowErrorAsync("Folder selection failed", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
                _loadCancellation = null;
        }
    }

    private async Task PickAssetFilesAsync(bool allowMultiple = false)
    {
        if (_isPickingAssetFiles)
            return;
        if (_loadCancellation != null)
        {
            StatusLabel.Text = "Wait for the current operation to finish before selecting files.";
            return;
        }

        _isPickingAssetFiles = true;
        CancellationTokenSource? pickerCancellation = null;
        try
        {
#if IOS
            var pickerCts = new CancellationTokenSource();
            pickerCancellation = pickerCts;
            _loadCancellation = pickerCts;
            await WaitForActionSheetDismissalAsync(pickerCts.Token);
#endif
            SetBusy(true, allowMultiple ? "Choose Unity files…" : "Choose a Unity file…");
            List<FileResult> selected;
#if IOS
            // Use Apple's document picker directly. This avoids the MAUI FilePicker
            // callback getting stuck on iOS after a document is tapped.
            var pickerProgress = new Progress<AssetLoadProgress>(update =>
            {
                if (ReferenceEquals(_loadCancellation, pickerCancellation))
                    SetLoadingProgress(Math.Clamp(update.Percent, 0, 100), update.Message);
            });
            selected = await AssetStudioNativeFilePicker.PickAsync(
                allowMultiple, pickerProgress, pickerCts.Token);
            pickerCts.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(_loadCancellation, pickerCancellation))
                _loadCancellation = null;
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
            await LogErrorAsync("File import failed", ex.Message);
        }
        finally
        {
#if IOS
            if (ReferenceEquals(_loadCancellation, pickerCancellation))
                _loadCancellation = null;
            pickerCancellation?.Dispose();
#endif
            _isPickingAssetFiles = false;
        }
    }

#if IOS
    private async Task WaitForActionSheetDismissalAsync(CancellationToken cancellationToken = default)
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
            cancellationToken.ThrowIfCancellationRequested();
            var topPresenter = GetTopPresentedViewController(rootPresenter);
            if (topPresenter is not UIKit.UIAlertController)
            {
                if (++stableDismissalChecks >= 3)
                {
                    await Task.Delay(300, cancellationToken);
                    if (GetTopPresentedViewController(rootPresenter) is not UIKit.UIAlertController)
                        return;
                    stableDismissalChecks = 0;
                }
            }
            else
            {
                stableDismissalChecks = 0;
            }

            await Task.Delay(50, cancellationToken);
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
        _assetFilterTimer?.Stop();
        _suppressFilterTextChanged = true;
        _loadMetadata = null;
        LoadInfoLabel.IsVisible = false;
        LoadInfoLabel.Text = string.Empty;
        _allAssets.Clear();
        _visibleAssets.Clear();
        _selectedAssets.Clear();
        _selectedAsset = null;
        AssetSearch.Text = string.Empty;
        ClassSearch.Text = string.Empty;
        HierarchySearch.Text = string.Empty;
        _suppressFilterTextChanged = false;
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
        void UpdateProgress()
        {
            LoadProgressBar.IsVisible = safePercent < 100;
            LoadProgressBar.Progress = safePercent / 100d;
            BusyIndicator.IsVisible = false;
            BusyIndicator.IsRunning = false;
            StatusLabel.Text = message;
        }

        if (MainThread.IsMainThread)
            UpdateProgress();
        else
            MainThread.BeginInvokeOnMainThread(UpdateProgress);
    }

    public void SetLoadingFinished(string message)
    {
        MainThread.BeginInvokeOnMainThread(() => SetBusy(false, message));
    }

    private static int CountArchiveEntries(string archivePath)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        return archive.Entries.Count;
    }

    // Mirrors the desktop status strip: "Finished loading N files with M
    // exportable assets", including the failed-read count when present.
    private string BuildDesktopLoadStatus(string sourceLabel)
    {
        if (_loadMetadata is not { } metadata)
            return $"{sourceLabel} loaded · {_allAssets.Count} assets · {_classes.Count} classes";

        var failed = metadata.FailedReadCount > 0
            ? $" and {metadata.FailedReadCount} assets failed to read"
            : string.Empty;
        return $"Finished loading {metadata.FileCount} files with {metadata.ExportableCount} exportable assets{failed}";
    }

    private void SetBusy(bool busy, string message)
    {
        BusyIndicator.IsVisible = false;
        BusyIndicator.IsRunning = false;
        MenuScroll.IsEnabled = !busy;
        MenuOverflowButton.IsEnabled = !busy;
        WorkspaceViewTabs.IsEnabled = !busy;
        WorkspaceGrid.IsEnabled = !busy;
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
        var checkedOptions = new List<string>();
        if (_showAllAssets)
            checkedOptions.Add("Display all assets");
        if (_previewEnabled)
            checkedOptions.Add("Enable preview");
        if (_displayAssetInformation)
            checkedOptions.Add("Display asset infromation");
        var choice = await Choose("Options", checkedOptions, OptionMenuHints,
            "Display all assets",
            "Enable preview",
            "Display asset infromation",
            "Specify Unity version",
            "Load script assemblies",
            "Clear script assemblies",
            "Export options");
        if (choice == null) return;
        switch (choice)
        {
            case "Display all assets":
                _showAllAssets = !_showAllAssets;
                Preferences.Set("AssetStudio.DisplayAllAssets", _showAllAssets);
                ApplyAssetFilter();
                StatusLabel.Text = $"Display all assets: {(_showAllAssets ? "On" : "Off")}";
                break;
            case "Enable preview":
                _previewEnabled = !_previewEnabled;
                Preferences.Set("AssetStudio.EnablePreview", _previewEnabled);
                if (!_previewEnabled)
                {
                    StopPreviewAudio();
                    _videoPlayer?.Stop();
                    ResetPreviewSurface();
                }
                else if (_selectedAsset != null)
                {
                    await PresentAssetPreviewAsync(_selectedAsset);
                }
                RefreshPreviewSurfaceVisibility();
                StatusLabel.Text = $"Preview: {(_previewEnabled ? "Enabled" : "Disabled")}";
                break;
            case "Display asset infromation":
                _displayAssetInformation = !_displayAssetInformation;
                Preferences.Set("AssetStudio.DisplayAssetInformation", _displayAssetInformation);
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
                try
                {
                    await Navigation.PushModalAsync(CreateModalNavigationPage(
                        new ExportOptionsPage(MobileExportSettings.Load(), settings => ApplyExportSettings(settings))));
                }
                catch (Exception exception)
                {
                    StatusLabel.Text = $"Could not open export options: {exception.Message}";
                    await ShowErrorAsync("Export options", exception.Message);
                }
                break;
            default:
                StatusLabel.Text = $"Unsupported options action: {choice}";
                break;
        }
    }

    private void ApplyExportSettings(MobileExportSettings settings, bool announce = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _defaultExportKind = settings.DefaultExportKind switch
        {
            MobileExportKind.Raw => "Raw",
            MobileExportKind.Dump => "Dump",
            _ => "Convert"
        };
        _textureExportFormat = settings.TextureFormat;
        _convertTextures = settings.ConvertTextures;
        _convertAudio = settings.ConvertAudio;
        _restoreTextAssetExtension = settings.RestoreTextAssetExtension;
        _archiveGrouping = settings.ArchiveGrouping;
        _shareAfterExport = settings.ShareAfterExport;
        if (announce)
        {
            StatusLabel.Text = "Export options saved · "
                + $"texture {settings.TextureFormat} · grouping {settings.ArchiveGrouping} · FBX {settings.Fbx.Format}";
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
                SetBusy(true, $"Copying {file.FileName}…");
                var imported = await _documentLibrary.ImportAsync(file);
                if (string.IsNullOrWhiteSpace(imported.FullPath) || !File.Exists(imported.FullPath))
                    throw new IOException($"The selected assembly '{file.FileName}' was not saved to the AssetStudio source cache.");
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
                await LogErrorAsync("Some assemblies were skipped", details);
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
            await LogErrorAsync("Assembly load failed", exception.Message);
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
            $"Audio export: {(_convertAudio ? "PCM/Vorbis/Ogg/ADPCM/VAG to WAV" : "Original data")}",
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
                "Convert AudioClip to WAV(PCM)",
                "Preserve original audio data");
            if (audioMode == null)
                return;
            _convertAudio = audioMode == "Convert AudioClip to WAV(PCM)";
            Preferences.Set("AssetStudio.ConvertAudio", _convertAudio);
            StatusLabel.Text = $"Audio export: {(_convertAudio ? "PCM/Vorbis/Ogg/ADPCM/VAG to WAV; other codecs raw" : "original embedded data")}";
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
            "Export all objects (split)",
            "Export selected objects (split)",
            "Export selected objects (split) + selected AnimationClips",
            "Export selected objects (merge)",
            "Export selected objects (merge) + selected AnimationClips",
            "Preview checked scene objects"
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
        var merge = choice.Contains("(merge)", StringComparison.Ordinal);
        var withAnimationClips = choice.EndsWith("+ selected AnimationClips", StringComparison.Ordinal);
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

        var explicitClips = _selectedAssets
            .Select(row => row.SourceAsset)
            .OfType<AssetStudio.AnimationClip>()
            .Distinct()
            .ToList();
        var animationContext = FindExportAnimationContext(sceneObjects, explicitClips);
        var animationClips = withAnimationClips
            ? CollectExportAnimationClips(sceneObjects, explicitClips)
            : null;
        if (withAnimationClips && (animationClips == null || animationClips.Count == 0))
        {
            StatusLabel.Text = "Select AnimationClips or an Animator with controller clips before exporting with animations";
            return;
        }

        var fbxOptions = MobileFbxExportOptions.Load();
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
                            cancellation.Token,
                            fbxOptions)
                        : MobileFbxExporter.ExportSceneMeshes(
                            MobileAssetExporter.GetSceneMeshGeometries(sceneObjects, cancellation.Token),
                            cancellation.Token,
                            fbxOptions),
                    cancellation.Token);
            }
            else if (sceneObjects != null)
            {
                outputPath = await Task.Run(
                    () => merge
                        ? MobileAssetExporter.ExportMergedSceneObjects(
                            sceneObjects, cancellation.Token, animationClips, animationContext)
                        : MobileAssetExporter.ExportSceneObjectsArchive(
                            sceneObjects, cancellation.Token, animationClips, animationContext),
                    cancellation.Token);
            }
            else
            {
                outputPath = await Task.Run(
                    () => merge
                        ? MobileAssetExporter.ExportMergedMeshAssetsArchive(
                            meshes,
                            materialContexts,
                            cancellation.Token,
                            animationClips,
                            animationContext)
                        : MobileAssetExporter.ExportMeshAssetsArchive(
                            meshes,
                            materialContexts,
                            cancellation.Token,
                            animationClips,
                            animationContext),
                    cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var animationNote = animationClips is { Count: > 0 }
                ? $" · {animationClips.Count} AnimationClip(s) included as converted data"
                : string.Empty;
            var shareTitle = exportFbx
                ? "AssetStudio static FBX model"
                : sceneObjects != null
                    ? "AssetStudio scene OBJ model archive"
                    : merge ? "AssetStudio merged OBJ model" : "AssetStudio split OBJ models";
            string status;
            if (exportFbx)
            {
                status = sceneObjects == null
                    ? $"Exported {meshes.Count} mesh(es) as static FBX with available material colors/PBR values; scene transforms, textures, rigs and animations are not included"
                    : $"Exported {meshes.Count} mesh(es) as static FBX with scene transforms, material colors/PBR values and baked skin/blend-shape pose; textures, rigs and animation are not included";
            }
            else if (sceneObjects != null)
            {
                status =
                    $"Exported {meshes.Count} scene mesh(es) as static OBJ with material maps, vertex colors and baked skin/blend-shape pose; rigs are not included{animationNote}";
            }
            else
            {
                status =
                    $"Exported {meshes.Count} mesh(es) as OBJ with available material colors and texture maps; scene transforms are not included{animationNote}";
            }
            await PresentExportResultAsync(shareTitle, outputPath, status);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Model export cancelled";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Model export failed: {ex.Message}";
            await ShowErrorAsync("Model export failed", ex.Message);
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
            await ShowErrorAsync("Scene preview failed", exception.Message);
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

    private AssetStudio.Animator? FindExportAnimationContext(
        IReadOnlyList<AssetStudio.GameObject>? sceneObjects,
        IEnumerable<AssetStudio.AnimationClip> explicitClips)
    {
        var selectedAnimator = _selectedAssets
            .Select(row => row.SourceAsset)
            .OfType<AssetStudio.Animator>()
            .FirstOrDefault(animator => animator.m_Controller.TryGet(out _));
        if (selectedAnimator != null)
            return selectedAnimator;

        if (sceneObjects == null)
            return null;
        foreach (var gameObject in sceneObjects)
        {
            foreach (var pointer in gameObject.m_Components)
            {
                if (pointer.TryGet(out var component)
                    && component is AssetStudio.Animator animator
                    && animator.m_Controller.TryGet(out _))
                {
                    return animator;
                }
            }
        }
        return null;
    }

    private static List<AssetStudio.AnimationClip> CollectExportAnimationClips(
        IReadOnlyList<AssetStudio.GameObject>? sceneObjects,
        IEnumerable<AssetStudio.AnimationClip> explicitClips)
    {
        var clips = explicitClips.Distinct().ToList();
        if (clips.Count > 0 || sceneObjects == null)
            return clips;

        foreach (var gameObject in sceneObjects)
        {
            foreach (var pointer in gameObject.m_Components)
            {
                if (!pointer.TryGet(out var component)
                    || component is not AssetStudio.Animator animator
                    || !animator.m_Controller.TryGet(out var controller))
                {
                    continue;
                }

                switch (controller)
                {
                    case AssetStudio.AnimatorController assetController:
                        foreach (var clipPointer in assetController.m_AnimationClips)
                        {
                            if (clipPointer.TryGet(out var clip))
                                clips.Add(clip);
                        }
                        break;
                    case AssetStudio.AnimatorOverrideController overrideController:
                        foreach (var mapping in overrideController.m_Clips)
                        {
                            if (mapping.m_OverrideClip.TryGet(out var clip))
                                clips.Add(clip);
                            else if (mapping.m_OriginalClip.TryGet(out clip))
                                clips.Add(clip);
                        }
                        break;
                }
            }
        }
        return clips.Distinct().ToList();
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
            StatusLabel.Text = scope == "Selected assets"
                ? "No Object selected for export."
                : "No exportable assets loaded";
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
            await PresentExportResultAsync(
                "AssetStudio asset list",
                outputPath,
                $"Exported XML asset list · {snapshot.Count} item(s) · {scope}");
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "XML asset-list export cancelled";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"XML export failed: {ex.Message}";
            await ShowErrorAsync("XML export error", ex.Message);
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
            StatusLabel.Text = scope == "Selected assets"
                ? "No Object selected for export."
                : "No exportable assets loaded";
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
            var status = rawFallbackCount == 0
                ? $"Exported {count} item(s) · {exportKind} · {scope}"
                : rawFallbackCount == 1 && fallbackWarning != null
                    ? $"Exported {count} item(s) · raw fallback: {fallbackWarning}"
                    : $"Exported {count} item(s) · {rawFallbackCount} raw fallback(s); see ZIP report";
            await PresentExportResultAsync($"AssetStudio {exportKind} export", outputPath, status);
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Export cancelled";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Export failed: {ex.Message}";
            await ShowErrorAsync("Export error", ex.Message);
        }
        finally
        {
            _loadCancellation = null;
            CancelLoadButton.Text = "Cancel load";
            SetBusy(false, StatusLabel.Text);
        }
    }

    private void OnFilterMenuClicked(object sender, EventArgs e)
    {
        _menuAnchor = sender as Button;
        // Desktop builds Filter Type from exportableAssets, not all parsed objects.
        var types = _allAssets.Where(x => x.IsExportable).Select(x => x.Type)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.CurrentCulture).ToArray();
        if (types.Length == 0)
        {
            StatusLabel.Text = "Load assets before choosing asset types";
            return;
        }

        _filterMenuTypes = types;
        // The desktop clears type items and rechecks All whenever assets are
        // reloaded; stale selections must not survive a different asset set.
        if (_selectedTypes != null && !_selectedTypes.All(types.Contains))
            _selectedTypes = null;
        ShowFilterTypeMenu();
    }

    // Native version of the WinForms Filter Type drop-down: "All" plus one
    // checkable item per ClassIDType, with the filter applied on every tap.
    private void ShowFilterTypeMenu()
    {
        _filterMenuOpen = true;
        MenuOptionsHost.Children.Clear();
        _filterMenuLabels.Clear();

        var options = new string[_filterMenuTypes.Length + 1];
        options[0] = "All";
        Array.Copy(_filterMenuTypes, 0, options, 1, _filterMenuTypes.Length);
        foreach (var option in options)
            AddFilterMenuItem(option);

        PositionMenuPopup("Filter Type", options);
        MenuOverlay.IsVisible = true;
    }

    private void AddFilterMenuItem(string option)
    {
        var label = new Label
        {
            Text = IsFilterTypeChecked(option) ? $"✓ {option}" : option,
            TextColor = Color.FromArgb("#202020"),
            FontSize = 12,
            FontAutoScalingEnabled = false,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Start,
            LineBreakMode = LineBreakMode.NoWrap
        };
        _filterMenuLabels[option] = label;
        var item = new Border
        {
            Style = (Style)Resources["PopupMenuItem"],
            Content = label
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => OnFilterTypeTapped(option);
        item.GestureRecognizers.Add(tap);
        MenuOptionsHost.Children.Add(item);
    }

    private bool IsFilterTypeChecked(string option) =>
        option == "All"
            ? _selectedTypes == null
            : _selectedTypes != null && _selectedTypes.Contains(option);

    private void OnFilterTypeTapped(string option)
    {
        if (option == "All")
        {
            // WinForms CheckOnClick: clicking All toggles it; checking All
            // clears every type item, unchecking it leaves no types selected.
            _selectedTypes = _selectedTypes == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : null;
        }
        else if (_selectedTypes == null)
        {
            // With All active the type items are unchecked, so the first type
            // tap starts a new selection containing only that type.
            _selectedTypes = new HashSet<string>(StringComparer.Ordinal) { option };
        }
        else if (!_selectedTypes.Add(option))
        {
            _selectedTypes.Remove(option);
        }

        foreach (var pair in _filterMenuLabels)
            pair.Value.Text = IsFilterTypeChecked(pair.Key) ? $"✓ {pair.Key}" : pair.Key;

        _browserTab = "Asset List";
        RefreshBrowserTab();
        ClearAssetListSelectionForFilterChange();
        ApplyAssetFilter();
    }

    private void CloseFilterMenu()
    {
        if (!_filterMenuOpen) return;
        _filterMenuOpen = false;
        MenuOverlay.IsVisible = false;
        MenuOptionsHost.Children.Clear();
        _filterMenuLabels.Clear();
    }

    private async void OnDebugMenuClicked(object sender, EventArgs e)
    {
        _menuAnchor = sender as Button;
        var choice = await Choose(
            "Debug",
            _showErrorMessages ? new[] { "Show error message" } : Array.Empty<string>(),
            "Show error message",
            "Export class structures",
            "Export class structures (text files)");
        if (choice == "Show error message")
        {
            _showErrorMessages = !_showErrorMessages;
            Preferences.Set("AssetStudio.ShowErrorMessages", _showErrorMessages);
            StatusLabel.Text = $"Show error message: {(_showErrorMessages ? "On" : "Off")}";
        }
        else if (choice == "Export class structures")
            await ExportClassStructuresAsync();
        else if (choice == "Export class structures (text files)")
            await ExportClassStructureTextFilesAsync();
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
            await PresentExportResultAsync(
                "AssetStudio class structures",
                outputPath,
                $"Exported {_classes.Count} class structures as XML");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Class export failed: {ex.Message}";
            await ShowErrorAsync("Class export failed", ex.Message);
        }
        finally
        {
            SetBusy(false, StatusLabel.Text);
        }
    }

    // Desktop writes one .txt per class into a folder per Unity version.
    // On mobile the same layout is exported as a shareable ZIP archive.
    private async Task ExportClassStructureTextFilesAsync()
    {
        if (_classes.Count == 0)
        {
            StatusLabel.Text = "No class structures are loaded";
            return;
        }

        try
        {
            SetBusy(true, $"Exporting {_classes.Count} class structures…");
            var outputPath = await Task.Run(() =>
            {
                var path = Path.Combine(FileSystem.CacheDirectory,
                    $"assetstudio-class-structures-{Guid.NewGuid():N}.zip");
                using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
                {
                    foreach (var group in _classes.GroupBy(
                        item => string.IsNullOrWhiteSpace(item.Version) ? "unknown-version" : item.Version,
                        StringComparer.OrdinalIgnoreCase))
                    {
                        var versionFolder = SafeClassName(group.Key);
                        foreach (var item in group)
                        {
                            var entryName = $"{versionFolder}/{SafeClassName(item.Id)} {SafeClassName(item.Name)}.txt";
                            var entry = archive.CreateEntry(entryName);
                            using var stream = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                            stream.Write(item.StructureText);
                        }
                    }
                }
                return path;
            });
            await PresentExportResultAsync(
                "AssetStudio class structures",
                outputPath,
                "Finished exporting class structures");
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Class export failed: {ex.Message}";
            await ShowErrorAsync("Class export failed", ex.Message);
        }
        finally
        {
            SetBusy(false, StatusLabel.Text);
        }
    }

    private static string SafeClassName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        cleaned = cleaned.Trim().Trim('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "unnamed" : cleaned;
    }

    // iOS equivalent of desktop "Open folder after export": when enabled the
    // share sheet opens; otherwise the result is copied to the Files-visible
    // Documents/AssetStudio/Exports folder and the location is reported.
    private async Task PresentExportResultAsync(string shareTitle, string outputPath, string status)
    {
        if (_shareAfterExport)
        {
            await Share.Default.RequestAsync(new ShareFileRequest(shareTitle, new ShareFile(outputPath)));
            StatusLabel.Text = status;
            return;
        }

        var savedPath = CopyExportToDocuments(outputPath);
        StatusLabel.Text = $"{status} Saved to Files → AssetStudio → Exports ({Path.GetFileName(savedPath)}).";
    }

    private static string CopyExportToDocuments(string sourcePath)
    {
        var exportDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AssetStudio", "Exports");
        Directory.CreateDirectory(exportDirectory);
        var fileName = Path.GetFileName(sourcePath);
        var destination = Path.Combine(exportDirectory, fileName);
        if (File.Exists(destination))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            destination = Path.Combine(exportDirectory,
                $"{stem}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{extension}");
        }
        File.Copy(sourcePath, destination, false);
        return destination;
    }

    private Task<string?> Choose(string title, params string[] options) =>
        Choose(title, Array.Empty<string>(), null, options);

    private Task<string?> Choose(
        string title,
        IReadOnlyCollection<string> checkedOptions,
        params string[] options) =>
        Choose(title, checkedOptions, null, options);

    private Task<string?> Choose(
        string title,
        IReadOnlyCollection<string> checkedOptions,
        IReadOnlyDictionary<string, string>? hints,
        params string[] options)
    {
        if (options.Length == 0)
            return Task.FromResult<string?>(null);

        CloseFilterMenu();
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
                Text = checkedOptions.Contains(option, StringComparer.Ordinal)
                    ? $"✓ {option}"
                    : option,
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
            if (hints != null && hints.TryGetValue(option, out var hint))
            {
                ToolTipProperties.SetText(item, hint);
                SemanticProperties.SetHint(item, hint);
            }
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => FinishMenuChoice(option);
            item.GestureRecognizers.Add(tap);
            MenuOptionsHost.Children.Add(item);

            // Match the desktop menu's visual grouping with thin separators.
            if (MenuSeparatorIndices.TryGetValue(title, out var separators) && separators.Contains(index))
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
        var separators = MenuSeparatorIndices.TryGetValue(title, out var indices)
            ? indices.Count(index => index < visibleItems)
            : 0;
        MenuOptionsScroll.HeightRequest = visibleItems * 24 + separators;
    }

    private void OnMenuOverlayBackgroundTapped(object? sender, TappedEventArgs e)
    {
        if (_filterMenuOpen)
            CloseFilterMenu();
        else
            FinishMenuChoice(null);
    }

    private void FinishMenuChoice(string? choice)
    {
        CloseFilterMenu();
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
        // Desktop ListSearchTextChanged restarts an 800 ms timer on every
        // keystroke instead of filtering the ListView immediately.
        if (_suppressFilterTextChanged) return;
        if (_assetFilterTimer == null)
        {
            ClearAssetListSelectionForFilterChange();
            ApplyAssetFilter();
            return;
        }

        _assetFilterTimer.Stop();
        _assetFilterTimer.Start();
    }

    private void OnAssetFilterTimerTick(object? sender, EventArgs e)
    {
        _assetFilterTimer?.Stop();
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
        // The desktop menu title never changes when types are checked.
        FilterTypeMenuButton.Text = "Filter Type";
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
            StatusLabel.Text = $"{_visibleAssets.Count} / {_allAssets.Count} assets";
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
            StatusLabel.Text = $"{_hierarchy.Count} visible / {_hierarchyById.Count} scene objects";
            return;
        }
        // Search the full tree in stable depth-first order, independent of the
        // current expansion state. A visited guard protects malformed graphs.
        var visited = new HashSet<HierarchyRow>();
        foreach (var root in _hierarchyRoots) CollectHierarchyMatches(root, query, visited);
        StatusLabel.Text = _hierarchySearchResults.Count == 0
            ? "No matching scene objects"
            : $"{_hierarchySearchResults.Count} scene object matches · press Enter to select next";
    }

    private void CollectHierarchyMatches(HierarchyRow node, string query, HashSet<HierarchyRow> visited)
    {
        if (!visited.Add(node)) return;
        // Desktop tree search matches only the visible node text.
        if (Contains(node.DisplayName, query))
            _hierarchySearchResults.Add(node);
        foreach (var child in node.Children) CollectHierarchyMatches(child, query, visited);
    }

    private void OnHierarchySearchButtonPressed(object sender, EventArgs e) => SelectNextHierarchySearchResult();

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
        // Desktop propagates the changed state only downward to descendants.
        SetHierarchyCheckedRecursive(row, e.Value);
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

        // Desktop context menu order: Copy text, Export selected assets,
        // conditional Animator export, Go to scene hierarchy, Show original file.
        var actions = new List<string> { "Copy text", "Export selected assets" };
        var hasAnimator = targets.Any(row => row.SourceAsset is AssetStudio.Animator);
        var hasAnimationClip = targets.Any(row => row.SourceAsset is AssetStudio.AnimationClip);
        if (hasAnimator && hasAnimationClip)
            actions.Add("Export Animator + selected AnimationClips");
        if (targets.Count == 1)
        {
            actions.Add("Go to scene hierarchy");
            actions.Add("Show original file");
        }

        _menuAnchor = null;
        var action = await Choose(row.Name, actions.ToArray());
        if (action == null) return;

        if (action == "Copy text")
        {
            await Clipboard.Default.SetTextAsync(row.Name);
            StatusLabel.Text = "Copied text";
            return;
        }

        if (action == "Go to scene hierarchy")
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

        if (action == "Export Animator + selected AnimationClips")
        {
            var animatorAndClips = targets
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
        if (!_previewEnabled)
            return;

        try
        {
            switch (row.SourceAsset)
            {
                case AssetStudio.TextAsset text:
                    ShowTextPreview(row.Name, "TextAsset", Encoding.UTF8.GetString(text.m_Script));
                    break;
                case AssetStudio.Texture2D texture:
                    PreviewDetails.Text = "Decoding texture…";
                    string? exportedPath = null;
                    try
                    {
                        exportedPath = await Task.Run(() => MobileAssetExporter.Export(texture));
                    }
                    catch
                    {
                        // Desktop reports unsupported images instead of a generic preview error.
                    }
                    if (!IsActivePreview(row)) return;
                    if (string.IsNullOrWhiteSpace(exportedPath))
                    {
                        StatusLabel.Text = "Unsupported image for preview";
                        break;
                    }
                    var (previewPath, temporaryPreview) = ApplyTextureChannelMask(exportedPath);
                    if (!IsActivePreview(row))
                    {
                        if (temporaryPreview) TryDeletePreviewFile(previewPath);
                        return;
                    }
                    ShowTexturePreview(row.Name, ImageSource.FromFile(previewPath), texture.m_Width,
                        texture.m_Height, texture.m_TextureFormat.ToString(), imagePath: previewPath,
                        temporaryPreviewPath: temporaryPreview ? previewPath : null,
                        supportsChannelToggles: true,
                        metadata: CreateTextureMetadataRows(
                            texture.m_Width,
                            texture.m_Height,
                            texture.m_TextureFormat.ToString(),
                            DescribeTextureFilterMode(texture.m_TextureSettings?.m_FilterMode),
                            texture.m_TextureSettings?.m_Aniso ?? 0,
                            texture.m_TextureSettings?.m_MipBias ?? 0f,
                            DescribeTextureWrapMode(texture.m_TextureSettings?.m_WrapMode),
                            DescribeEnabledTextureChannels()));
                    StatusLabel.Text = "'Ctrl'+'R'/'G'/'B'/'A' for Channel Toggle · or tap Channels";
                    break;
                case AssetStudio.Mesh mesh:
                    ShowModelPreview(row.Name,
                        $"Mesh · {mesh.m_VertexCount:N0} vertices · Open the wireframe preview to inspect it.");
                    if (mesh.m_VertexCount <= 0 || mesh.m_Vertices is not { Length: > 0 })
                    {
                        StatusLabel.Text = "Mesh can't be previewed.";
                        break;
                    }
                    StatusLabel.Text = InlineMeshView.IsVisible
                        ? "Drag to rotate · Pinch to zoom · Can be exported to FBX file."
                        : "Unable to preview this mesh";
                    break;
                case AssetStudio.MonoBehaviour behaviour:
                    var conversion = await Task.Run(() => _monoBehaviourAssemblyService.Convert(behaviour));
                    if (!IsActivePreview(row)) return;
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
                    ShowAudioPreview(row.Name, DescribeAudioCompressionFormat(audio),
                        $"Length: {audio.m_Length:N2} seconds · {audio.m_Channels} channel(s)\nPCM, Vorbis/Ogg and managed ADPCM/VAG convert to WAV; MP3 and AAC use native playback. Other codecs remain exportable as original data.");
                    break;
                case AssetStudio.Font font:
                    ShowFontPreview(row.Name,
                        font.m_FontData is { Length: > 0 }
                            ? $"Embedded font data · {font.m_FontData.Length:N0} bytes."
                            : "This font has no embedded data to preview or export.");
#if IOS
                    if (font.m_FontData is { Length: > 0 })
                        await ApplyEmbeddedFontPreviewAsync(font, row);
                    else
                        StatusLabel.Text = "Unsupported font for preview. Try to export.";
#else
                    StatusLabel.Text = "Unsupported font for preview. Try to export.";
#endif
                    break;
                case AssetStudio.Shader shader:
                    var shaderSource = await Task.Run(() => AssetStudio.ShaderConverter.Convert(shader));
                    if (!IsActivePreview(row)) return;
                    ShowTextPreview(row.Name, "Shader source",
                        shaderSource.Replace("\n", "\r\n").Replace("\0", ""));
                    break;
                case AssetStudio.Sprite sprite:
                    PreviewDetails.Text = "Cropped sprite preview with packed rotation, tight-mesh masking, and separate alpha texture applied when present.";
                    string? spriteImage = null;
                    try
                    {
                        spriteImage = await Task.Run(() => MobileAssetExporter.Export(sprite));
                    }
                    catch
                    {
                        // Desktop reports unsupported sprites instead of a generic preview error.
                    }
                    if (!IsActivePreview(row)) return;
                    if (string.IsNullOrWhiteSpace(spriteImage))
                    {
                        StatusLabel.Text = "Unsupported sprite for preview.";
                        break;
                    }
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
                    StatusLabel.Text = "Only supported export.";
                    break;
                case AssetStudio.Animator:
                    await ShowAssetDumpOrSummaryAsync(row,
                        "Animator clips can be exported with the selected Animator or scene objects.");
                    StatusLabel.Text = "Can be exported to FBX file.";
                    break;
                case AssetStudio.AnimationClip:
                    await ShowAssetDumpOrSummaryAsync(row,
                        "AnimationClip data can be exported with an Animator or selected scene objects.");
                    StatusLabel.Text = "Can be exported with Animator or Objects";
                    break;
                default:
                    await ShowAssetDumpOrSummaryAsync(row,
                        "Use the Dump tab to inspect serialized fields when available, or Export to save this asset.");
                    break;
            }
        }
        catch (Exception ex)
        {
            if (!IsActivePreview(row)) return;
            ShowUnsupportedPreview(row.Name, row.Type, $"Preview failed: {ex.Message}");
        }
    }

    private bool IsActivePreview(AssetRow row) =>
        _previewEnabled && _selectedAsset?.Id == row.Id;

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
            await LogErrorAsync("Mesh preview failed", ex.Message);
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
            ShowModelPreview(
                node.DisplayName,
                $"Scene object · {geometries.Count} renderable mesh(es) · Drag to rotate, pinch to zoom.",
                drawable,
                sceneObject: true);
            StatusLabel.Text = "Drag to rotate · Pinch to zoom · Preview selected scene object";
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Scene object preview failed: {exception.Message}";
            await LogErrorAsync("Scene object preview failed", exception.Message);
        }
    }

    private async void OnTextureChannelsClicked(object sender, EventArgs e)
    {
        try
        {
            await Navigation.PushModalAsync(CreateModalNavigationPage(
                new TextureChannelsPage(_textureChannels, ApplyTextureChannels)));
        }
        catch (Exception exception)
        {
            StatusLabel.Text = $"Could not open texture channels: {exception.Message}";
            await ShowErrorAsync("Texture channels", exception.Message);
        }
    }

    private async void ApplyTextureChannels(IReadOnlyList<bool> channels)
    {
        if (channels.Count != _textureChannels.Length)
            return;

        for (var index = 0; index < _textureChannels.Length; index++)
            _textureChannels[index] = channels[index];
        TextureChannelsButton.Text = $"Channels: {DescribeEnabledTextureChannels()}";
        StatusLabel.Text = "'Ctrl'+'R'/'G'/'B'/'A' for Channel Toggle";
        if (_selectedAsset?.SourceAsset is AssetStudio.Texture2D)
            await PresentAssetPreviewAsync(_selectedAsset);
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
            await LogErrorAsync("Texture preview failed", exception.Message);
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
            await ShowErrorAsync("Video playback unavailable", exception.Message);
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

        FontSpecimenTiny.FontFamily = fontName;
        FontSpecimenSmall.FontFamily = fontName;
        FontSpecimenMedium.FontFamily = fontName;
        FontSpecimenLarge.FontFamily = fontName;
        FontSpecimenXLarge.FontFamily = fontName;
        FontSpecimenHuge.FontFamily = fontName;
        FontSpecimenSize60.FontFamily = fontName;
        FontSpecimenSize72.FontFamily = fontName;
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
            await LogErrorAsync("Audio playback failed", ex.Message);
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
            await LogErrorAsync("Audio playback failed", ex.Message);
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
            await LogErrorAsync("Audio playback failed", exception.Message);
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
        DumpScroll.IsVisible = _showingDump && !_showingEmptyPreview;
        EmptyPreviewSurface.IsVisible = _showingDump
            ? false
            : !_previewEnabled || _showingEmptyPreview;
        PreviewEmptyLabel.Text = _previewEnabled
            ? "Select an asset\nto preview"
            : "Preview disabled\nEnable it in Options to view assets";
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

    private void OnClassSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_suppressFilterTextChanged) ApplyClassFilter();
    }

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
        RefreshClassGroups();
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

    // Desktop uses one ListViewGroup per Unity version. MAUI's grouped
    // CollectionView gets the same headers without WinForms ListViewGroup.
    private void RefreshClassGroups()
    {
        _visibleClassGroups.Clear();
        foreach (var group in _visibleClasses.GroupBy(
            item => string.IsNullOrWhiteSpace(item.Version) ? "(unknown version)" : item.Version,
            StringComparer.OrdinalIgnoreCase))
        {
            _visibleClassGroups.Add(new ClassGroup(group.Key, group));
        }
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
            set { if (_depth == value) return; _depth = value; OnPropertyChanged(); }
        }
        // WinForms TreeView-style guide columns for the ancestor path.
        private string _treePrefix = "";
        public string TreePrefix
        {
            get => _treePrefix;
            set { if (_treePrefix == value) return; _treePrefix = value; OnPropertyChanged(); }
        }
        public List<HierarchyRow> Children { get; } = new();
        public HierarchyRow? Parent { get; set; }
        public bool HasChildren => Children.Count > 0;
        public string ExpandGlyph => IsExpanded ? "−" : "+";
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

    private sealed class MobileLogger : AssetStudio.ILogger
    {
        private readonly MainPage _page;

        public MobileLogger(MainPage page) => _page = page;

        public void Log(AssetStudio.LoggerEvent loggerEvent, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (loggerEvent == AssetStudio.LoggerEvent.Error)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                    await _page.ShowErrorAsync("AssetStudio", message));
                return;
            }

            MainThread.BeginInvokeOnMainThread(() => _page.StatusLabel.Text = message);
        }
    }

    // One MAUI CollectionView group per Unity version, matching the desktop
    // ListViewGroup headers produced by BuildClassStructure().
    public sealed class ClassGroup : List<ClassRow>
    {
        public string Version { get; }

        public ClassGroup(string version, IEnumerable<ClassRow> rows) : base(rows) => Version = version;

        public override string ToString() => Version;
    }

    public sealed record LoadMetadata(
        string ProductName,
        string UnityVersion,
        string TargetPlatform,
        int FileCount,
        int ExportableCount,
        int FailedReadCount);
}
