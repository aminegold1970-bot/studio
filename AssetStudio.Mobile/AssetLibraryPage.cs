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
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    public AssetLibraryPage(AssetDocumentLibrary library, Func<IReadOnlyList<FileResult>, Task> load)
    {
        _library = library;
        _load = load;
        Title = "Asset Library";
        BackgroundColor = Colors.White;
        _items.SelectionMode = SelectionMode.Multiple;
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
            _selected.Clear();
            foreach (var item in e.CurrentSelection.OfType<LibraryFileEntry>()) _selected.Add(item.File.FullPath);
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
            foreach (var entry in targets) _library.Delete(entry.File);
            _selected.Clear(); Refresh();
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
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _search.Text?.Trim() ?? string.Empty;
        _items.ItemsSource = _all.Where(entry => query.Length == 0 || entry.StoredName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        UpdateSummary();
    }

    private void UpdateSummary() => _summary.Text = $"{_all.Count} saved file(s) · {_selected.Count} selected";
}
