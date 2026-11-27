using System.Globalization;
using Microsoft.Maui.Controls;
#if IOS
using Microsoft.Maui.ApplicationModel;
using UIKit;
#endif
using Color = Microsoft.Maui.Graphics.Color;

namespace AssetStudioMobile;

/// <summary>
/// Desktop ExportOptions-equivalent page. The general options are applied by the
/// mobile exporter; FBX values are persisted with desktop-equivalent keys, while
/// the static iOS FBX path applies format, skin-pose and blend-shape-pose settings.
/// </summary>
internal sealed class ExportOptionsPage : ContentPage
{
    // Desktop ExportOptions uses these exact combobox item strings.
    private static readonly string[] ImageFormatLabels = { "Bmp", "Png", "Jpeg", "Tga" };
    private static readonly string[] GroupingLabels =
        { "type name", "container path", "source file name", "do not group" };
    private static readonly string[] FbxVersionLabels = { "6.1", "7.1", "7.2", "7.3", "7.4", "7.5" };
    private static readonly string[] FbxFormatLabels = { "Binary", "Ascii" };

    private readonly MobileExportSettings _source;
    private readonly Action<MobileExportSettings> _apply;
    private readonly Picker _imageFormat = new();
    private readonly Picker _grouping = new();
    private readonly Switch _restoreExtension = new();
    private readonly Switch _convertTextures = new();
    private readonly Switch _convertAudio = new();
    private readonly Switch _shareAfterExport = new();
    private readonly Switch _eulerFilter = new();
    private readonly Entry _filterPrecision = CreateNumberEntry();
    private readonly Switch _exportAllNodes = new();
    private readonly Switch _exportSkins = new();
    private readonly Switch _exportAnimations = new();
    private readonly Switch _exportBlendShape = new();
    private readonly Switch _castToBone = new();
    private readonly Entry _boneSize = CreateNumberEntry();
    private readonly Switch _allUvsAsDiffuse = new();
    private readonly Entry _scaleFactor = CreateNumberEntry();
    private readonly Picker _fbxVersion = new();
    private readonly Picker _fbxFormat = new();
    private readonly Switch _bakeSkinPose = new();
    private readonly Switch _bakeBlendShapePose = new();
#if IOS
    private readonly Label _exportFolderValue = new()
    {
        TextColor = Color.FromArgb("#202020"),
        FontSize = 12,
        LineBreakMode = LineBreakMode.TailTruncation,
        HorizontalTextAlignment = TextAlignment.End
    };
#endif

    public ExportOptionsPage(MobileExportSettings settings, Action<MobileExportSettings> apply)
    {
        _source = settings ?? throw new ArgumentNullException(nameof(settings));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        Title = "Export options";
        BackgroundColor = Color.FromArgb("#FFFFFF");

        ConfigurePicker(_imageFormat, ImageFormatLabels, ImageIndex(_source.TextureFormat));
        ConfigurePicker(_grouping, GroupingLabels, (int)_source.ArchiveGrouping);
        ConfigurePicker(_fbxVersion, FbxVersionLabels, _source.Fbx.FbxVersionIndex);
        ConfigurePicker(_fbxFormat, FbxFormatLabels, (int)_source.Fbx.Format);
        _restoreExtension.IsToggled = _source.RestoreTextAssetExtension;
        _convertTextures.IsToggled = _source.ConvertTextures;
        _convertTextures.Toggled += OnConvertTexturesToggled;
        _imageFormat.IsEnabled = _source.ConvertTextures;
        _convertAudio.IsToggled = _source.ConvertAudio;
        _shareAfterExport.IsToggled = _source.ShareAfterExport;
        _eulerFilter.IsToggled = _source.Fbx.EulerFilter;
        _filterPrecision.Text = Format(_source.Fbx.FilterPrecision);
        _exportAllNodes.IsToggled = _source.Fbx.ExportAllNodes;
        _exportSkins.IsToggled = _source.Fbx.ExportSkins;
        _exportAnimations.IsToggled = _source.Fbx.ExportAnimations;
        _exportBlendShape.IsToggled = _source.Fbx.ExportBlendShape;
        _castToBone.IsToggled = _source.Fbx.CastToBone;
        _boneSize.Text = Format(_source.Fbx.BoneSize);
        _allUvsAsDiffuse.IsToggled = _source.Fbx.ExportAllUvsAsDiffuseMaps;
        _scaleFactor.Text = Format(_source.Fbx.ScaleFactor);
        _bakeSkinPose.IsToggled = _source.Fbx.BakeSkinPose;
        _bakeBlendShapePose.IsToggled = _source.Fbx.BakeBlendShapePose;

        var layout = new VerticalStackLayout { Padding = 14, Spacing = 3 };
        layout.Children.Add(Heading("Export"));
        AddPicker(layout, "Group exported assets by", _grouping);
        AddSwitch(layout, "Restore TextAsset extension name", _restoreExtension);
        AddSwitch(layout, "Convert Texture2D", _convertTextures);
        AddPicker(layout, "    Texture image format", _imageFormat);
        AddSwitch(layout, "Convert AudioClip to WAV(PCM)", _convertAudio);
        layout.Children.Add(new Label
        {
            Text = "Mobile converts PCM, Vorbis/Ogg, IMA ADPCM, VAG and DSP ADPCM to WAV. MP3/AAC use native playback; other codecs stay as original embedded data. Desktop FMOD conversion is not available on iOS.",
            TextColor = Color.FromArgb("#666666"),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 8)
        });
#if IOS
        var exportFolderRow = CreateRow(GridLength.Auto);
        exportFolderRow.Add(CreateRowLabel("Export folder"));
        exportFolderRow.Add(_exportFolderValue, 1);
        layout.Children.Add(exportFolderRow);
        layout.Children.Add(new Label
        {
            Text = "Choose a folder in Files to set the export destination. Cross-app drag and drop is iPad-only; iPhone uses this picker.",
            TextColor = Color.FromArgb("#666666"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 4)
        });
        var chooseExportFolder = new Button
        {
            Text = "Choose export folder",
            FontSize = 12,
            MinimumHeightRequest = 38,
            HorizontalOptions = LayoutOptions.Start
        };
        chooseExportFolder.Clicked += OnChooseExportFolderClicked;
        layout.Children.Add(chooseExportFolder);
        RefreshExportFolderLabel();
        Appearing += OnExportOptionsAppearing;
        Disappearing += OnExportOptionsDisappearing;
#endif
        AddSwitch(layout, "Open folder after export", _shareAfterExport,
            "On mobile this also opens the share sheet. Exports are saved to the dropped export folder, or Files → AssetStudio → Exports on iPhone.");
        layout.Children.Add(new Label
        {
            Text = "On mobile this also opens the share sheet. Exports are saved to the dropped export folder, or Files → AssetStudio → Exports on iPhone.",
            TextColor = Color.FromArgb("#666666"),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 6)
        });

        layout.Children.Add(Heading("Fbx"));
        AddSwitch(layout, "EulerFilter", _eulerFilter);
        AddNumber(layout, "FilterPrecision", _filterPrecision);
        AddSwitch(layout, "Export all nodes", _exportAllNodes);
        AddSwitch(layout, "Export skins", _exportSkins);
        AddSwitch(layout, "Export animations", _exportAnimations);
        AddSwitch(layout, "Export blendshape", _exportBlendShape);
        AddSwitch(layout, "All nodes cast to bone", _castToBone);
        AddNumber(layout, "BoneSize", _boneSize);
        AddSwitch(
            layout,
            "Export all UVs as diffuse maps",
            _allUvsAsDiffuse,
            "Unchecked: UV1 exported as normal map. Check this if your export is missing a UV map.");
        AddNumber(layout, "ScaleFactor", _scaleFactor);
        AddPicker(layout, "FBXVersion", _fbxVersion);
        AddPicker(layout, "FBXFormat", _fbxFormat);
        AddSwitch(layout, "Bake skin pose (static FBX)", _bakeSkinPose);
        AddSwitch(layout, "Bake blendshape pose (static FBX)", _bakeBlendShapePose);
        layout.Children.Add(new Label
        {
            Text = "The iOS static FBX exporter applies format and baked-pose options. Animation, Euler and skeleton values are stored for desktop-equivalent settings parity.",
            TextColor = Color.FromArgb("#666666"),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 8)
        });

        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.End };
        var save = new Button { Text = "OK", FontSize = 12, MinimumHeightRequest = 38 };
        save.Clicked += OnSaveClicked;
        var cancel = new Button { Text = "Cancel", FontSize = 12, MinimumHeightRequest = 38 };
        cancel.Clicked += OnCancelClicked;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        layout.Children.Add(buttons);

        Content = new ScrollView { Content = layout };
    }

    private static Label Heading(string text) => new()
    {
        Text = text,
        TextColor = Color.FromArgb("#202020"),
        FontAttributes = FontAttributes.Bold,
        FontSize = 14,
        Margin = new Thickness(0, 10, 0, 4)
    };

    private static Entry CreateNumberEntry() => new()
    {
        Keyboard = Keyboard.Numeric,
        FontSize = 12,
        WidthRequest = 110,
        HorizontalTextAlignment = TextAlignment.End,
        MinimumHeightRequest = 34
    };

    private static void ConfigurePicker(Picker picker, string[] labels, int selectedIndex)
    {
        foreach (var label in labels)
            picker.Items.Add(label);
        picker.SelectedIndex = Math.Clamp(selectedIndex, 0, labels.Length - 1);
        picker.FontSize = 12;
        picker.MinimumHeightRequest = 34;
    }

    private static void AddPicker(VerticalStackLayout layout, string label, Picker picker)
    {
        var row = CreateRow(new GridLength(130, GridUnitType.Absolute));
        row.Add(CreateRowLabel(label));
        row.Add(picker, 1);
        layout.Children.Add(row);
    }

    private static void AddSwitch(
        VerticalStackLayout layout,
        string label,
        Switch toggle,
        string? hint = null)
    {
        var row = CreateRow(GridLength.Auto);
        var rowLabel = CreateRowLabel(label);
        row.Add(rowLabel);
        row.Add(toggle, 1);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            ToolTipProperties.SetText(row, hint);
            ToolTipProperties.SetText(rowLabel, hint);
            SemanticProperties.SetHint(row, hint);
        }
        layout.Children.Add(row);
    }

    private static void AddNumber(VerticalStackLayout layout, string label, Entry entry)
    {
        var row = CreateRow(new GridLength(110, GridUnitType.Absolute));
        row.Add(CreateRowLabel(label));
        row.Add(entry, 1);
        layout.Children.Add(row);
    }

    private static Grid CreateRow(GridLength secondColumn) => new()
    {
        ColumnDefinitions =
        {
            new ColumnDefinition(GridLength.Star),
            new ColumnDefinition(secondColumn)
        },
        ColumnSpacing = 8,
        Padding = new Thickness(0, 3)
    };

    private static Label CreateRowLabel(string text) => new()
    {
        Text = text,
        TextColor = Color.FromArgb("#202020"),
        FontSize = 12,
        VerticalTextAlignment = TextAlignment.Center
    };

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static int ImageIndex(MobileImageFormat format) => format switch
    {
        MobileImageFormat.Bmp => 0,
        MobileImageFormat.Png => 1,
        MobileImageFormat.Jpeg => 2,
        MobileImageFormat.Tga => 3,
        _ => 1
    };

    private static MobileImageFormat ImageFromIndex(int index) => index switch
    {
        0 => MobileImageFormat.Bmp,
        2 => MobileImageFormat.Jpeg,
        3 => MobileImageFormat.Tga,
        _ => MobileImageFormat.Png
    };

    private void OnConvertTexturesToggled(object? sender, ToggledEventArgs e) =>
        _imageFormat.IsEnabled = e.Value;

#if IOS
    private void OnExportOptionsAppearing(object? sender, EventArgs e)
    {
        AssetStudioDropBridge.ExportDropTargetActive = true;
        MainPage.ExportFolderDropped += OnMainExportFolderDropped;
        RefreshExportFolderLabel();
        MainThread.BeginInvokeOnMainThread(AttachExportFolderDropTarget);
    }

    private void OnExportOptionsDisappearing(object? sender, EventArgs e)
    {
        AssetStudioDropBridge.ExportDropTargetActive = false;
        MainPage.ExportFolderDropped -= OnMainExportFolderDropped;
    }

    private void OnMainExportFolderDropped(string bookmark, string displayName) =>
        RefreshExportFolderLabel();

    private async void OnChooseExportFolderClicked(object? sender, EventArgs e)
    {
        try
        {
            if (await IOSExportFolder.PickAsync())
                RefreshExportFolderLabel();
        }
        catch (Exception exception)
        {
            await DisplayAlert("Export folder", exception.Message, "OK");
        }
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (AssetStudioDropBridge.ExportDropTargetActive)
            MainThread.BeginInvokeOnMainThread(AttachExportFolderDropTarget);
    }

    private void AttachExportFolderDropTarget()
    {
        if (!AssetStudioDropBridge.ExportDropTargetActive)
            return;

        if (Handler?.PlatformView is UIView nativeView)
            AssetStudioDropBridge.Attach(nativeView, AssetStudioDropTargetMode.ExportFolder);
        else if (Handler?.PlatformView is UIViewController controller &&
                 controller.View is UIView controllerView)
            AssetStudioDropBridge.Attach(controllerView, AssetStudioDropTargetMode.ExportFolder);
    }

    private void RefreshExportFolderLabel() =>
        _exportFolderValue.Text = IOSExportFolder.DisplayName ?? "Files → AssetStudio → Exports";
#endif

    private async void OnCancelClicked(object? sender, EventArgs e) =>
        await Navigation.PopModalAsync();

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (!TryParse(_filterPrecision.Text, out var filterPrecision)
            || !TryParse(_boneSize.Text, out var boneSize)
            || !TryParse(_scaleFactor.Text, out var scaleFactor))
        {
            await DisplayAlert("Export options", "FilterPrecision, BoneSize and ScaleFactor must be numbers.", "OK");
            return;
        }
        if (!IsDesktopNumericRange(filterPrecision)
            || !IsDesktopIntegerRange(boneSize)
            || !IsDesktopNumericRange(scaleFactor))
        {
            await DisplayAlert("Export options",
                "FilterPrecision and ScaleFactor must be between 0 and 100 with at most 2 decimal places; BoneSize must be a whole number from 0 to 100.", "OK");
            return;
        }

        var updated = new MobileExportSettings
        {
            DefaultExportKind = _source.DefaultExportKind,
            TextureFormat = ImageFromIndex(_imageFormat.SelectedIndex),
            ConvertTextures = _convertTextures.IsToggled,
            ConvertAudio = _convertAudio.IsToggled,
            ShareAfterExport = _shareAfterExport.IsToggled,
            RestoreTextAssetExtension = _restoreExtension.IsToggled,
            ArchiveGrouping = (MobileArchiveGrouping)Math.Clamp(_grouping.SelectedIndex, 0, GroupingLabels.Length - 1),
            Fbx = new MobileFbxExportOptions
            {
                Format = (MobileFbxFormat)Math.Clamp(_fbxFormat.SelectedIndex, 0, FbxFormatLabels.Length - 1),
                BakeSkinPose = _bakeSkinPose.IsToggled,
                BakeBlendShapePose = _bakeBlendShapePose.IsToggled,
                EulerFilter = _eulerFilter.IsToggled,
                FilterPrecision = filterPrecision,
                ExportAllNodes = _exportAllNodes.IsToggled,
                ExportSkins = _exportSkins.IsToggled,
                ExportAnimations = _exportAnimations.IsToggled,
                ExportBlendShape = _exportBlendShape.IsToggled,
                CastToBone = _castToBone.IsToggled,
                BoneSize = boneSize,
                ExportAllUvsAsDiffuseMaps = _allUvsAsDiffuse.IsToggled,
                ScaleFactor = scaleFactor,
                FbxVersionIndex = Math.Clamp(_fbxVersion.SelectedIndex, 0, FbxVersionLabels.Length - 1)
            }
        };

        MobileExportSettings.Save(updated);
        _apply(updated);
        await Navigation.PopModalAsync();
    }

    private static bool TryParse(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    // Desktop NumericUpDown controls use the default 0-100 range and 2 decimals.
    private static bool IsDesktopNumericRange(decimal value) =>
        value >= 0m && value <= 100m && decimal.Round(value, 2) == value;

    // BoneSize uses a NumericUpDown with DecimalPlaces = 0 in the desktop dialog.
    private static bool IsDesktopIntegerRange(decimal value) =>
        value >= 0m && value <= 100m && decimal.Truncate(value) == value;
}
