using System.Globalization;
using System.Text;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Newtonsoft.Json.Linq;
using Color = Microsoft.Maui.Graphics.Color;

namespace AssetStudioMobile;

/// <summary>
/// Static atlas/skeleton viewer for exported Live2D-style (Spine) models.
/// The proprietary Cubism runtime is not available in this port, so this page
/// shows the selected texture, parses atlas regions and summarizes skeleton
/// data instead of performing runtime mesh deformation or animation playback.
/// </summary>
internal sealed class Live2DViewerPage : ContentPage
{
    private enum Part
    {
        Texture,
        Atlas,
        Skeleton
    }

    private sealed record SelectedPart(string Path, string DisplayName);

    private sealed record AtlasRegion(
        string Name, int X, int Y, int Width, int Height, bool Rotated);

    private sealed record AtlasInfo(
        string PageName, int PageWidth, int PageHeight, IReadOnlyList<AtlasRegion> Regions);

    private sealed record SkeletonInfo(
        string Kind, int BoneCount, int SlotCount, int AnimationCount,
        string BoneSummary, string SlotSummary, string AnimationSummary);

    private readonly Dictionary<Part, SelectedPart> _selected = new();
    private readonly Label _textureLabel = CreateSelectionLabel();
    private readonly Label _atlasLabel = CreateSelectionLabel();
    private readonly Label _skeletonLabel = CreateSelectionLabel();
    private readonly Label _matchLabel = new()
    {
        TextColor = AssetStudioTheme.FromHex("#666666"),
        FontSize = 11
    };
    private readonly Label _statusLabel = new()
    {
        TextColor = AssetStudioTheme.FromHex("#2878C8"),
        FontSize = 12
    };
    private readonly Label _detailsLabel = new()
    {
        TextColor = AssetStudioTheme.FromHex("#202020"),
        FontSize = 11
    };
    private readonly Image _textureImage = new()
    {
        Aspect = Aspect.AspectFit,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
        MinimumHeightRequest = 320
    };
    private readonly Button _showButton = new()
    {
        Text = "Show model",
        FontSize = 13,
        HorizontalOptions = LayoutOptions.Start,
        MinimumHeightRequest = 40
    };

    public Live2DViewerPage()
    {
        Title = "Live2D viewer";
        BackgroundColor = AssetStudioTheme.FromHex("#FFFFFF");
        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));

        var textureButton = CreatePickButton("Choose PNG", Part.Texture);
        var atlasButton = CreatePickButton("Choose atlas", Part.Atlas);
        var skeletonButton = CreatePickButton("Choose skeleton", Part.Skeleton);
        _showButton.Clicked += OnShowClicked;

        var layout = new VerticalStackLayout { Padding = 14, Spacing = 8 };
        layout.Children.Add(new Label
        {
            Text = "Select a PNG texture, atlas and .skel skeleton whose file names match. Names such as model.png, model.atlas.txt and model.skel are accepted.",
            TextColor = AssetStudioTheme.FromHex("#666666"),
            FontSize = 11
        });
        layout.Children.Add(CreateSelectionRow("Texture", textureButton, _textureLabel));
        layout.Children.Add(CreateSelectionRow("Atlas", atlasButton, _atlasLabel));
        layout.Children.Add(CreateSelectionRow("Skeleton", skeletonButton, _skeletonLabel));
        layout.Children.Add(_matchLabel);
        layout.Children.Add(_showButton);
        layout.Children.Add(_statusLabel);

        var imageFrame = new Border
        {
            BackgroundColor = AssetStudioTheme.FromHex("#E8E8E8"),
            Stroke = AssetStudioTheme.FromHex("#B7B7B7"),
            StrokeThickness = 1,
            Padding = 8,
            HeightRequest = 360,
            Content = _textureImage
        };
        layout.Children.Add(imageFrame);
        layout.Children.Add(new Label
        {
            Text = "Static preview: the atlas texture and parsed region/skeleton data are shown. Runtime deformation and animation playback require the proprietary Live2D/Cubism engine and are not included.",
            TextColor = AssetStudioTheme.FromHex("#888888"),
            FontSize = 10
        });
        layout.Children.Add(_detailsLabel);

        Content = new ScrollView { Content = layout };
        RefreshSelectionLabels();
    }

    private Button CreatePickButton(string text, Part part)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 12,
            HorizontalOptions = LayoutOptions.Start,
            MinimumHeightRequest = 38
        };
        button.Clicked += (_, _) => _ = PickAsync(part);
        return button;
    }

    private static Label CreateSelectionLabel() => new()
    {
        TextColor = AssetStudioTheme.FromHex("#202020"),
        FontSize = 12,
        LineBreakMode = LineBreakMode.TailTruncation
    };

    private static Grid CreateSelectionRow(string title, Button button, Label label)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(110, GridUnitType.Absolute)),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            }
        };
        grid.Add(new Label
        {
            Text = title,
            TextColor = AssetStudioTheme.FromHex("#444444"),
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center
        });
        grid.Add(button, 1);
        grid.Add(label, 2, 0);
        return grid;
    }

    private async Task PickAsync(Part part)
    {
        try
        {
            var title = part switch
            {
                Part.Texture => "Select PNG texture",
                Part.Atlas => "Select atlas file",
                _ => "Select skeleton file"
            };
            var file = await PickFileAsync(title);
            if (file == null)
                return;

            var displayName = string.IsNullOrWhiteSpace(file.FileName)
                ? Path.GetFileName(file.FullPath)
                : file.FileName;
            var extension = Path.GetExtension(displayName);
            var expected = part switch
            {
                Part.Texture => new[] { ".png", ".jpg", ".jpeg" },
                Part.Atlas => new[] { ".atlas", ".txt" },
                _ => new[] { ".skel", ".txt", ".json", ".bytes" }
            };
            if (!expected.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                _statusLabel.Text = $"{title}: expected {string.Join(" or ", expected)}, got {extension}.";
                return;
            }
            if (string.IsNullOrWhiteSpace(file.FullPath) || !File.Exists(file.FullPath))
            {
                _statusLabel.Text = $"{title}: the selected file is no longer available.";
                return;
            }

            _selected[part] = new SelectedPart(file.FullPath, displayName);
            RefreshSelectionLabels();
            _statusLabel.Text = $"Selected {displayName}.";
        }
        catch (Exception exception)
        {
            _statusLabel.Text = $"Selection failed: {exception.Message}";
        }
    }

    private static async Task<FileResult?> PickFileAsync(string title)
    {
#if IOS
        var progress = new Progress<MainPage.AssetLoadProgress>(_ => { });
        var files = await AssetStudioNativeFilePicker.PickAsync(
            allowMultiple: false, progress, CancellationToken.None);
        return files.FirstOrDefault();
#else
        return await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = title });
#endif
    }

    private void RefreshSelectionLabels()
    {
        _textureLabel.Text = FormatSelection(Part.Texture, "No PNG selected");
        _atlasLabel.Text = FormatSelection(Part.Atlas, "No atlas selected");
        _skeletonLabel.Text = FormatSelection(Part.Skeleton, "No skeleton selected");

        if (!HasAllFiles())
        {
            _matchLabel.Text = "Select all three files to check name matching.";
            _matchLabel.TextColor = AssetStudioTheme.FromHex("#666666");
            return;
        }

        var identities = _selected.Values
            .Select(file => ModelIdentity(file.DisplayName))
            .ToArray();
        if (identities.All(id => !string.IsNullOrWhiteSpace(id)
                                 && id.Equals(identities[0], StringComparison.OrdinalIgnoreCase)))
        {
            _matchLabel.Text = $"Names match: {identities[0]}";
            _matchLabel.TextColor = AssetStudioTheme.FromHex("#2D7D32");
        }
        else
        {
            _matchLabel.Text = $"Names do not match: {string.Join(", ", identities)}";
            _matchLabel.TextColor = AssetStudioTheme.FromHex("#A33A3A");
        }
    }

    private string FormatSelection(Part part, string emptyText) =>
        _selected.TryGetValue(part, out var file) ? file.DisplayName : emptyText;

    private bool HasAllFiles() =>
        _selected.ContainsKey(Part.Texture)
        && _selected.ContainsKey(Part.Atlas)
        && _selected.ContainsKey(Part.Skeleton);

    private async void OnShowClicked(object? sender, EventArgs e)
    {
        if (!HasAllFiles())
        {
            _statusLabel.Text = "Choose a PNG, atlas and skeleton before showing the model.";
            return;
        }

        var identities = _selected.Values
            .Select(file => ModelIdentity(file.DisplayName))
            .ToArray();
        if (!identities.All(id => !string.IsNullOrWhiteSpace(id)
                                 && id.Equals(identities[0], StringComparison.OrdinalIgnoreCase)))
        {
            _statusLabel.Text = "The PNG, atlas and skeleton names must match. Rename or select the matching files and try again.";
            return;
        }

        _statusLabel.Text = "Loading atlas and skeleton…";
        _showButton.IsEnabled = false;
        try
        {
            var texture = _selected[Part.Texture];
            var atlas = _selected[Part.Atlas];
            var skeleton = _selected[Part.Skeleton];

            var imageSize = await Task.Run(() =>
            {
                using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(texture.Path);
                return (image.Width, image.Height);
            });
            var atlasInfo = await Task.Run(() => ParseAtlas(File.ReadAllText(atlas.Path)));
            var skeletonInfo = await Task.Run(() => DescribeSkeleton(skeleton.Path));

            _textureImage.Source = ImageSource.FromFile(texture.Path);
            _detailsLabel.Text = BuildDetails(texture.DisplayName, imageSize, atlasInfo, skeletonInfo);
            _statusLabel.Text = $"Showing {identities[0]} · {atlasInfo.Regions.Count} atlas region(s) · {skeletonInfo.BoneCount} bone(s).";
        }
        catch (Exception exception)
        {
            _statusLabel.Text = $"Could not show model: {exception.Message}";
        }
        finally
        {
            _showButton.IsEnabled = true;
        }
    }

    private static string BuildDetails(
        string textureName,
        (int Width, int Height) imageSize,
        AtlasInfo atlas,
        SkeletonInfo skeleton)
    {
        var builder = new StringBuilder();
        builder.Append("Texture: ").Append(textureName)
            .Append(" (").Append(imageSize.Width).Append('×').Append(imageSize.Height).AppendLine(")");
        builder.Append("Atlas: ").Append(atlas.PageName)
            .Append(" (").Append(atlas.PageWidth).Append('×').Append(atlas.PageHeight)
            .Append("), ").Append(atlas.Regions.Count).AppendLine(" region(s)");
        builder.Append("Skeleton: ").Append(skeleton.Kind)
            .Append(" · ").Append(skeleton.BoneCount).Append(" bone(s), ")
            .Append(skeleton.SlotCount).Append(" slot(s), ")
            .Append(skeleton.AnimationCount).AppendLine(" animation(s)");
        builder.AppendLine();
        builder.Append("Bones: ").AppendLine(skeleton.BoneSummary);
        builder.Append("Slots: ").AppendLine(skeleton.SlotSummary);
        builder.Append("Animations: ").AppendLine(skeleton.AnimationSummary);
        builder.AppendLine();
        builder.AppendLine("Atlas regions:");
        foreach (var region in atlas.Regions.Take(60))
        {
            builder.Append("• ").Append(region.Name)
                .Append(" @ ").Append(region.X).Append(',').Append(region.Y)
                .Append(' ').Append(region.Width).Append('×').Append(region.Height)
                .AppendLine(region.Rotated ? " (rotated)" : string.Empty);
        }
        if (atlas.Regions.Count > 60)
            builder.Append("… and ").Append(atlas.Regions.Count - 60).AppendLine(" more region(s).");
        return builder.ToString();
    }

    // Repeatedly remove known container/document extensions so names such as
    // model.atlas.txt and model.skel.bytes compare as "model". Role suffixes
    // like model_atlas and model_skel are also treated as the same model.
    private static string ModelIdentity(string fileName)
    {
        var name = Path.GetFileName(fileName);
        string[] extensions = { ".txt", ".json", ".bytes", ".asset", ".atlas", ".skel", ".png", ".jpg", ".jpeg", ".tga" };
        string previous;
        do
        {
            previous = name;
            var extension = Path.GetExtension(name);
            if (!string.IsNullOrEmpty(extension)
                && extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                name = Path.GetFileNameWithoutExtension(name);
        } while (!string.Equals(previous, name, StringComparison.Ordinal));

        foreach (var suffix in new[] { "_atlas", "_skel", "_skeleton", "_texture", "_tex", "_png" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }
        return name.Trim();
    }

    private static AtlasInfo ParseAtlas(string text)
    {
        var pageName = string.Empty;
        var pageWidth = 0;
        var pageHeight = 0;
        var regions = new List<AtlasRegion>();
        AtlasRegion? current = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
                continue;
            var indented = char.IsWhiteSpace(rawLine[0]);
            var line = rawLine.Trim();
            if (!indented)
            {
                if (current != null)
                {
                    regions.Add(current);
                    current = null;
                }
                if (LooksLikeImage(line) || string.IsNullOrEmpty(pageName))
                {
                    pageName = line;
                    pageWidth = pageHeight = 0;
                }
                else
                {
                    current = new AtlasRegion(line, 0, 0, 0, 0, false);
                }
                continue;
            }

            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (current == null)
            {
                if (key.Equals("size", StringComparison.OrdinalIgnoreCase)
                    && TryParsePair(value, out var width, out var height))
                {
                    pageWidth = width;
                    pageHeight = height;
                }
                continue;
            }

            switch (key.ToLowerInvariant())
            {
                case "xy" when TryParsePair(value, out var x, out var y):
                    current = current with { X = x, Y = y };
                    break;
                case "size" when TryParsePair(value, out var width, out var height):
                    current = current with { Width = width, Height = height };
                    break;
                case "rotate":
                    current = current with { Rotated = !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0" };
                    break;
            }
        }
        if (current != null)
            regions.Add(current);

        return new AtlasInfo(pageName, pageWidth, pageHeight, regions);
    }

    private static bool LooksLikeImage(string line)
    {
        var extension = Path.GetExtension(line);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".tga", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParsePair(string value, out int first, out int second)
    {
        first = second = 0;
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out first)
               && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out second);
    }

    private static SkeletonInfo DescribeSkeleton(string path)
    {
        var text = File.ReadAllText(path);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            var root = JToken.Parse(text);
            var bones = (root["bones"] as JArray)?.Select(token => NameOf(token, "bone")).ToArray()
                        ?? Array.Empty<string>();
            var slots = (root["slots"] as JArray)?.Select(token => NameOf(token, "slot")).ToArray()
                        ?? Array.Empty<string>();
            var animations = root["animations"] is JObject animationsObject
                ? animationsObject.Properties().Select(property => property.Name).ToArray()
                : Array.Empty<string>();
            return new SkeletonInfo(
                "JSON skeleton data",
                bones.Length,
                slots.Length,
                animations.Length,
                JoinNames(bones),
                JoinNames(slots),
                JoinNames(animations));
        }

        var bytes = File.ReadAllBytes(path);
        var headerLength = Math.Min(4, bytes.Length);
        var header = Encoding.ASCII.GetString(bytes, 0, headerLength);
        return new SkeletonInfo(
            $"Binary skeleton data ({bytes.Length:N0} bytes, header \"{header}\")",
            0, 0, 0,
            "Binary skeleton bones are not decoded by the static viewer.",
            "Binary skeleton slots are not decoded by the static viewer.",
            "Binary skeleton animations are not decoded by the static viewer.");
    }

    private static string NameOf(JToken token, string fallbackPrefix)
    {
        if (token is JValue value)
            return value.Value?.ToString() ?? fallbackPrefix;
        var name = token["name"]?.ToString();
        return string.IsNullOrWhiteSpace(name) ? $"{fallbackPrefix}#{token.Path}" : name;
    }

    private static string JoinNames(IReadOnlyList<string> names) =>
        names.Count == 0
            ? "None"
            : string.Join(", ", names.Take(20)) + (names.Count > 20 ? $" … (+{names.Count - 20})" : string.Empty);
}
