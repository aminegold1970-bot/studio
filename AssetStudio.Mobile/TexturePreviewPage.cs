namespace AssetStudioMobile;

/// <summary>Full-screen viewer for decoded Unity Texture2D and Sprite PNG previews.</summary>
internal sealed class TexturePreviewPage : ContentPage
{
    private const double MinScale = 1d;
    private const double MaxScale = 8d;
    private readonly Image _image;
    private readonly Grid _surface;
    private readonly Label _status;
    private double _scale = 1d;
    private double _startScale = 1d;
    private double _offsetX;
    private double _offsetY;
    private double _startX;
    private double _startY;

    public TexturePreviewPage(string title, string imagePath, int width, int height, string format)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Texture Preview" : title;
        BackgroundColor = Color.FromArgb("#202020");

        _image = new Image
        {
            Source = ImageSource.FromFile(imagePath),
            Aspect = Aspect.AspectFit,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            AnchorX = 0.5,
            AnchorY = 0.5
        };

        _surface = new Grid
        {
            BackgroundColor = Color.FromArgb("#202020"),
            Padding = 8,
            Children = { _image }
        };

        _status = new Label
        {
            Text = BuildInfo(imagePath, width, height, format),
            TextColor = Color.FromArgb("#E8E8E8"),
            FontSize = 12,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            Padding = new Thickness(8, 7),
            LineBreakMode = LineBreakMode.WordWrap
        };

        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += OnPinchUpdated;
        _surface.GestureRecognizers.Add(pinch);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPanUpdated;
        _surface.GestureRecognizers.Add(pan);

        ToolbarItems.Add(new ToolbarItem("−", null, () => ChangeZoom(0.8)));
        ToolbarItems.Add(new ToolbarItem("+", null, () => ChangeZoom(1.25)));
        ToolbarItems.Add(new ToolbarItem("Reset", null, ResetZoom));
        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));

        var layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            RowSpacing = 0
        };
        layout.Add(_surface, 0, 0);
        layout.Add(_status, 0, 1);
        Content = layout;

        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            _image.IsVisible = false;
            _status.Text = "Preview image is unavailable. Re-export the asset and try again.";
        }
    }

    private static string BuildInfo(string path, int width, int height, string format)
    {
        var size = string.Empty;
        try
        {
            if (File.Exists(path))
                size = $"  ·  {new FileInfo(path).Length / 1024d:N0} KB";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var dimensions = width > 0 && height > 0 ? $"{width:N0} × {height:N0}" : "Unknown dimensions";
        var formatText = string.IsNullOrWhiteSpace(format) ? "Unknown format" : format;
        return $"{dimensions}  ·  {formatText}{size}
Pinch or use +/− to zoom · Drag to move · Reset to fit";
    }

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started)
        {
            _startScale = _scale;
            return;
        }
        if (e.Status == GestureStatus.Running)
        {
            SetScale(_startScale * e.Scale);
            return;
        }
        if (e.Status == GestureStatus.Completed)
            _startScale = _scale;
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started)
        {
            _startX = _offsetX;
            _startY = _offsetY;
        }
        else if (e.StatusType == GestureStatus.Running && _scale > MinScale)
        {
            _offsetX = _startX + e.TotalX;
            _offsetY = _startY + e.TotalY;
            ApplyTransform();
        }
        else if (e.StatusType == GestureStatus.Completed)
        {
            _startX = _offsetX;
            _startY = _offsetY;
        }
    }

    private void ChangeZoom(double factor)
    {
        SetScale(_scale * factor);
        _startScale = _scale;
    }

    private void SetScale(double value)
    {
        var next = Math.Clamp(value, MinScale, MaxScale);
        if (next <= MinScale)
        {
            _offsetX = 0;
            _offsetY = 0;
        }
        _scale = next;
        ApplyTransform();
    }

    private void ApplyTransform()
    {
        _image.Scale = _scale;
        _image.TranslationX = _offsetX;
        _image.TranslationY = _offsetY;
    }

    private void ResetZoom()
    {
        _scale = MinScale;
        _startScale = MinScale;
        _offsetX = 0;
        _offsetY = 0;
        ApplyTransform();
    }
}
