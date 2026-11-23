using Microsoft.Maui.Storage;

namespace AssetStudioMobile;

/// <summary>On-device library manager for persistent source files.</summary>
public sealed class AssetLibraryPage : ContentPage
{
    private readonly AssetDocumentLibrary _library;
    private readonly Func<IReadOnlyList<FileResult>, Task> _load;
    private readonly CollectionView _items = new();
    private readonly SearchBar _search = new()
    {
        Placeholder = "Search saved files…",
        FontSize = 14,
        TextColor = Color.FromArgb("#202020"),
        PlaceholderColor = Color.FromArgb("#777777"),
        BackgroundColor = Colors.White
    };
    private readonly Label _summary = new() { FontSize = 12, TextColor = Color.FromArgb("#666666") };
    private readonly List<LibraryFileEntry> _all = new();
    private List<LibraryFileEntry> _visible = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private HashSet<string> _visiblePaths = new(StringComparer.Ordinal);
    private bool _updatingSelection;

    public AssetLibraryPage(AssetDocumentLibrary library, Func<IReadOnlyList<FileResult>, Task> load)
    {
        _library = library;
        _load = load;
        Title = "Asset Library";
        BackgroundColor = Colors.White;
        _items.SelectionMode = SelectionMode.Multiple;
        _items.EmptyView = new Label
        {
            Text = "No saved files match this search.",
            TextColor = Color.FromArgb("#666666"),
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center
        };
        _items.ItemTemplate = new DataTemplate(() =>
        {
            var name = new Label { FontSize = 13, TextColor = Color.FromArgb("#202020"), LineBreakMode = LineBreakMode.MiddleTruncation };
            name.SetBinding(Label.TextProperty, nameof(LibraryFileEntry.StoredName));
            var detail = new Label { FontSize = 11, TextColor = Color.FromArgb("#666666") };
            detail.SetBinding(Label.TextProperty, new MultiBinding
            {
                StringFormat = "{0} · {1}",
                Bindings = { new Binding(nameof(LibraryFileEntry.DisplaySize)), new Binding(nameof(LibraryFileEntry.DisplayDate)) }
            });
            var stack = new VerticalStackLayout { Spacing = 3, Padding = new Thickness(8, 6) };
            stack.Add(name);
            stack.Add(detail);
            var row = new Grid { RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(1) } };
            row.Add(stack);
            var separator = new BoxView { Color = Color.FromArgb("#D6D6D6") };
            Grid.SetRow(separator, 1);
            row.Add(separator);
            return row;
        });
        _items.SelectionChanged += (_, e) =>
        {
            if (_updatingSelection) return;

            var current = e.CurrentSelection.OfType<LibraryFileEntry>()
                .Select(item => item.File.FullPath)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var item in e.PreviousSelection.OfType<LibraryFileEntry>())
            {
                var path = item.File.FullPath;
                if (_visiblePaths.Contains(path) && !current.Contains(path))
                    _selected.Remove(path);
            }
            foreach (var path in current)
                _selected.Add(path);
            UpdateSummary();
        };
        _search.TextChanged += (_, _) => ApplyFilter();

        var loadButton = MakeButton("Open selected", async () =>
        {
            var files = _all.Where(entry => _selected.Contains(entry.File.FullPath)).Select(entry => entry.File).ToList();
            if (files.Count == 0) { await DisplayAlert("Asset Library", "Select one or more saved files first.", "OK"); return; }
            await Navigation.PopModalAsync();
            await _load(files);
        });
        var deleteButton = MakeButton("Delete selected", async () =>
        {
            var targets = _all.Where(entry => _selected.Contains(entry.File.FullPath)).ToList();
            if (targets.Count == 0) { await DisplayAlert("Asset Library", "Select files to delete.", "OK"); return; }
            if (!await DisplayAlert("Delete saved files?", $"Permanently delete {targets.Count} saved file(s)?", "Delete", "Cancel")) return;
            var failures = new List<string>();
            foreach (var entry in targets)
            {
                try
                {
                    if (!_library.Delete(entry.File))
                        failures.Add($"{entry.StoredName}: file no longer exists or is outside the library.");
                }
                catch (Exception exception)
                {
                    failures.Add($"{entry.StoredName}: {exception.Message}");
                }
            }
            Refresh();
            if (failures.Count > 0)
                await DisplayAlert("Some files could not be deleted", string.Join(Environment.NewLine, failures), "OK");
        });
        var closeButton = MakeButton("Close", async () => await Navigation.PopModalAsync());
        var actions = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 8 };
        actions.Add(loadButton); actions.Add(deleteButton, 1); actions.Add(closeButton, 2);
        Content = new Grid
        {
            Padding = new Thickness(14, 10), RowSpacing = 10,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            Children = { new Label { Text = "Saved Unity files", FontSize = 16, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#202020") }, _search, _summary, _items, actions }
        };
        Grid.SetRow(_search, 1); Grid.SetRow(_summary, 2); Grid.SetRow(_items, 3); Grid.SetRow(actions, 4);
        Refresh();
    }

    private static Button MakeButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 12,
            Padding = new Thickness(5, 6),
            MinimumHeightRequest = 36,
            CornerRadius = 0,
            BorderWidth = 1,
            BorderColor = Color.FromArgb("#B7B7B7"),
            BackgroundColor = Color.FromArgb("#E2E2E2"),
            TextColor = Color.FromArgb("#202020")
        };
        button.Clicked += async (_, _) => await action();
        return button;
    }

    private void Refresh()
    {
        _all.Clear(); _all.AddRange(_library.GetEntries());
        var existingPaths = _all.Select(entry => entry.File.FullPath).ToHashSet(StringComparer.Ordinal);
        _selected.RemoveWhere(path => !existingPaths.Contains(path));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        _visible = _all.Where(entry =>
                query.Length == 0
                || entry.StoredName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _visiblePaths = _visible.Select(entry => entry.File.FullPath).ToHashSet(StringComparer.Ordinal);
        _updatingSelection = true;
        try
        {
            _items.ItemsSource = _visible;
            _items.SelectedItems.Clear();
            foreach (var entry in _visible)
            {
                if (_selected.Contains(entry.File.FullPath))
                    _items.SelectedItems.Add(entry);
            }
        }
        finally
        {
            _updatingSelection = false;
        }
        UpdateSummary();
    }

    private void UpdateSummary() =>
        _summary.Text = $"{_all.Count} saved · {_visible.Count} shown · {_selected.Count} selected";
}
