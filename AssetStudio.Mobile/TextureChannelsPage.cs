using Microsoft.Maui.Controls;
using Color = Microsoft.Maui.Graphics.Color;

namespace AssetStudioMobile;

/// <summary>
/// Desktop-style channel visibility dialog for decoded texture previews.
/// The array order matches the exporter's BGRA byte order.
/// </summary>
internal sealed class TextureChannelsPage : ContentPage
{
    internal static readonly string[] PreferenceKeys =
    {
        "AssetStudio.TextureChannel.B",
        "AssetStudio.TextureChannel.G",
        "AssetStudio.TextureChannel.R",
        "AssetStudio.TextureChannel.A"
    };

    private static readonly string[] ChannelLabels = { "Blue (B)", "Green (G)", "Red (R)", "Alpha (A)" };
    private readonly CheckBox[] _checks = new CheckBox[4];
    private readonly Action<bool[]> _apply;

    public TextureChannelsPage(IReadOnlyList<bool> channels, Action<bool[]> apply)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count != _checks.Length)
            throw new ArgumentException("Expected four BGRA channel flags.", nameof(channels));

        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        Title = "Texture channels";
        BackgroundColor = Color.FromArgb("#FFFFFF");

        var layout = new VerticalStackLayout { Padding = 16, Spacing = 12 };
        layout.Children.Add(new Label
        {
            Text = "Choose which decoded channels are visible in the preview:",
            TextColor = Color.FromArgb("#202020"),
            FontSize = 13
        });

        for (var index = 0; index < _checks.Length; index++)
        {
            _checks[index] = new CheckBox
            {
                IsChecked = channels[index],
                Color = Color.FromArgb("#2878C8"),
                MinimumWidthRequest = 24,
                MinimumHeightRequest = 24
            };
            layout.Children.Add(new HorizontalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    _checks[index],
                    new Label
                    {
                        Text = ChannelLabels[index],
                        TextColor = Color.FromArgb("#202020"),
                        FontSize = 13,
                        VerticalTextAlignment = TextAlignment.Center
                    }
                }
            });
        }

        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End };
        var cancel = new Button { Text = "Cancel", FontSize = 12, MinimumHeightRequest = 36 };
        cancel.Clicked += OnCancelClicked;
        var applyButton = new Button { Text = "Apply", FontSize = 12, MinimumHeightRequest = 36 };
        applyButton.Clicked += OnApplyClicked;
        buttons.Children.Add(cancel);
        buttons.Children.Add(applyButton);
        layout.Children.Add(buttons);

        Content = new ScrollView { Content = layout };
    }

    private async void OnCancelClicked(object? sender, EventArgs e) =>
        await Navigation.PopModalAsync();

    private async void OnApplyClicked(object? sender, EventArgs e)
    {
        var channels = _checks.Select(check => check.IsChecked).ToArray();
        for (var index = 0; index < channels.Length; index++)
            Preferences.Set(PreferenceKeys[index], channels[index]);
        _apply(channels);
        await Navigation.PopModalAsync();
    }
}
