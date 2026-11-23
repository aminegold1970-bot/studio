using System.Collections.ObjectModel;
using System.Linq;

namespace AssetStudioMobile;

// UI-only reconstruction. The host application must populate these collections
// from its existing AssetStudio services; no sample/fake asset data is inserted.
public partial class MainPage : ContentPage
{
    private readonly ObservableCollection<AssetRow> _allAssets = new();
    private readonly ObservableCollection<AssetRow> _visibleAssets = new();
    private readonly ObservableCollection<HierarchyRow> _hierarchy = new();
    private readonly ObservableCollection<ClassRow> _classes = new();
    private string _browserTab = "Asset List";
    private bool _showAllAssets;
    private int _sortColumn = -1;
    private bool _reverseSort;
    private bool _updatingHierarchyChecks;

    public MainPage()
    {
        InitializeComponent();
        AssetCollection.ItemsSource = _visibleAssets;
        HierarchyCollection.ItemsSource = _hierarchy;
        ClassesCollection.ItemsSource = _classes;
        RefreshBrowserTab();
    }

    // Integration points for the real mobile asset-loading layer.
    public void SetAssets(IEnumerable<AssetRow> assets)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _allAssets.Clear();
            foreach (var asset in assets) _allAssets.Add(asset);
            ApplyAssetFilter();
            StatusLabel.Text = $"{_allAssets.Count} assets";
        });
    }

    public void SetHierarchy(IEnumerable<HierarchyRow> nodes)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _hierarchy.Clear();
            foreach (var node in nodes) _hierarchy.Add(node);
        });
    }

    public void SetClasses(IEnumerable<ClassRow> classes)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _classes.Clear();
            foreach (var item in classes) _classes.Add(item);
        });
    }

    public void SetSelectedAssetDetails(string title, string details, string dump)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            PreviewTitle.Text = title;
            PreviewDetails.Text = details;
            DumpText.Text = dump;
        });
    }

    private bool _previewEnabled = true;
    private bool _displayAssetInformation = true;
    private bool _showErrorMessages = true;

    private async void OnFileMenuClicked(object sender, EventArgs e)
    {
        var choice = await Choose("File", "Load file", "Load folder", "Extract file", "Extract folder");
        if (choice != null) ReportUnwired(choice);
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
                PreviewScroll.IsVisible = _previewEnabled && PreviewScroll.IsVisible;
                StatusLabel.Text = $"Preview: {(_previewEnabled ? "Enabled" : "Disabled")}";
                break;
            case "Display asset information":
                _displayAssetInformation = !_displayAssetInformation;
                PreviewDetails.IsVisible = _displayAssetInformation;
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
        if (choice is "Raw" or "Dump" or "Asset list to XML")
        {
            var scope = await Choose(choice, "All assets", "Selected assets", "Filtered assets");
            if (scope != null) ReportUnwired($"{choice}: {scope}");
            return;
        }
        ReportUnwired(choice);
    }

    private async void OnFilterMenuClicked(object sender, EventArgs e)
    {
        var types = _allAssets.Select(x => x.Type).Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct().OrderBy(x => x).ToArray();
        var options = new[] { "All" }.Concat(types).ToArray();
        var choice = await Choose("Filter Type", options);
        if (choice == null) return;
        _selectedType = choice == "All" ? null : choice;
        ApplyAssetFilter();
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

    private string? _selectedType;

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
        RefreshBrowserTab();
    }

    private void RefreshBrowserTab()
    {
        AssetCollection.IsVisible = _browserTab == "Asset List";
        HierarchyCollection.IsVisible = _browserTab == "Scene Hierarchy";
        ClassesCollection.IsVisible = _browserTab == "Asset Classes";
        AssetSearch.IsVisible = _browserTab == "Asset List";
        AssetColumnHeaders.IsVisible = _browserTab == "Asset List";
        StatusLabel.Text = $"{_browserTab} · {(_browserTab == "Asset List" ? _visibleAssets.Count : _browserTab == "Scene Hierarchy" ? _hierarchy.Count : _classes.Count)} items";
    }

    private void OnAssetSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyAssetFilter();

    private void ApplyAssetFilter()
    {
        var query = AssetSearch?.Text?.Trim() ?? "";
        IEnumerable<AssetRow> filtered = _allAssets.Where(asset =>
            (_selectedType == null || asset.Type == _selectedType) &&
            (query.Length == 0 ||
             Contains(asset.Name, query) || Contains(asset.Container, query) ||
             Contains(asset.Type, query) || Contains(asset.PathId, query)));
        filtered = SortAssets(filtered);
        _visibleAssets.Clear();
        foreach (var asset in filtered) _visibleAssets.Add(asset);
        if (StatusLabel != null && _browserTab == "Asset List")
            StatusLabel.Text = $"{_visibleAssets.Count} / {_allAssets.Count} assets";
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
        AssetCollection.SelectedItem = null;
        ApplyAssetFilter();
        StatusLabel.Text = $"Sorted by {(new[] { "Name", "Container", "Type", "PathID", "Size" })[column]} · {(_reverseSort ? "descending" : "ascending")}";
    }
    private void OnSortName(object sender, EventArgs e) => SortBy(0);
    private void OnSortContainer(object sender, EventArgs e) => SortBy(1);
    private void OnSortType(object sender, EventArgs e) => SortBy(2);
    private void OnSortPathId(object sender, EventArgs e) => SortBy(3);
    private void OnSortSize(object sender, EventArgs e) => SortBy(4);

    private void OnHierarchyCheckChanged(object sender, CheckedChangedEventArgs e)
    {
        if (_updatingHierarchyChecks || sender is not CheckBox check || check.BindingContext is not HierarchyRow row) return;
        _updatingHierarchyChecks = true;
        row.IsChecked = e.Value;
        var index = _hierarchy.IndexOf(row);
        for (var i = index + 1; i < _hierarchy.Count && _hierarchy[i].Depth > row.Depth; i++)
            _hierarchy[i].IsChecked = e.Value;
        _updatingHierarchyChecks = false;
        StatusLabel.Text = $"{_hierarchy.Count(x => x.IsChecked)} scene objects checked";
    }

    private static bool Contains(string? value, string query) =>
        (value ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);

    private void OnAssetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not AssetRow row) return;
        PreviewTitle.Text = row.Name;
        PreviewDetails.Text = $"Container: {row.Container}\nType: {row.Type}\nPathID: {row.PathId}\nSize: {row.Size}";
        DumpText.Text = "Serialized dump is not connected.\n\nSelected asset:\n" +
                        $"Name: {row.Name}\nContainer: {row.Container}\nType: {row.Type}\nPathID: {row.PathId}\nSize: {row.Size}";
    }

    private void OnHierarchySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is HierarchyRow node)
            StatusLabel.Text = $"Selected scene object: {node.DisplayName}";
    }

    private void OnClassSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is ClassRow item)
            StatusLabel.Text = $"Selected class: {item.Name} ({item.Id})";
    }

    private void OnPreviewTabClicked(object sender, EventArgs e)
    {
        PreviewScroll.IsVisible = true;
        DumpScroll.IsVisible = false;
    }

    private void OnDumpTabClicked(object sender, EventArgs e)
    {
        PreviewScroll.IsVisible = false;
        DumpScroll.IsVisible = true;
    }

    public sealed class AssetRow
    {
        public string Name { get; init; } = "";
        public string Container { get; init; } = "";
        public string Type { get; init; } = "";
        public string PathId { get; init; } = "";
        public string Size { get; init; } = "";
    }
    public sealed class HierarchyRow : BindableObject
    {
        private bool _isChecked;
        public string DisplayName { get; init; } = "";
        public int Depth { get; init; }
        public Thickness IndentPadding => new(12 + Math.Max(0, Depth) * 16, 2, 4, 2);
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked == value) return; _isChecked = value; OnPropertyChanged(); }
        }
    }
    public sealed class ClassRow
    {
        public string Name { get; init; } = "";
        public string Id { get; init; } = "";
    }
}
