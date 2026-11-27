using Microsoft.Maui.Storage;

namespace AssetStudioMobile;

internal enum MobileFbxFormat
{
    Binary,
    Ascii
}

/// <summary>
/// Persisted mobile equivalents of the desktop ExportOptions dialog.
/// Values not honored by the static iOS FBX exporter are retained so the UI
/// keeps desktop parity without importing native desktop FBX dependencies.
/// </summary>
internal sealed class MobileFbxExportOptions
{
    public MobileFbxFormat Format { get; init; } = MobileFbxFormat.Binary;
    public bool BakeSkinPose { get; init; } = true;
    public bool BakeBlendShapePose { get; init; } = true;
    public bool EulerFilter { get; init; } = true;
    public decimal FilterPrecision { get; init; } = 0.25m;
    public bool ExportAllNodes { get; init; } = true;
    public bool ExportSkins { get; init; } = true;
    public bool ExportAnimations { get; init; } = true;
    public bool ExportBlendShape { get; init; } = true;
    public bool CastToBone { get; init; } = false;
    public decimal BoneSize { get; init; } = 10m;
    public bool ExportAllUvsAsDiffuseMaps { get; init; } = false;
    public decimal ScaleFactor { get; init; } = 1m;
    public int FbxVersionIndex { get; init; } = 3;

    public static MobileFbxExportOptions Load()
    {
        var format = ParseEnum(
            Preferences.Get("AssetStudio.FbxFormat", nameof(MobileFbxFormat.Binary)),
            MobileFbxFormat.Binary);
        var version = Preferences.Get("AssetStudio.FbxVersionIndex", "3");
        return new MobileFbxExportOptions
        {
            Format = format,
            BakeSkinPose = Preferences.Get("AssetStudio.FbxBakeSkinPose", true),
            BakeBlendShapePose = Preferences.Get("AssetStudio.FbxBakeBlendShapePose", true),
            EulerFilter = Preferences.Get("AssetStudio.FbxEulerFilter", true),
            FilterPrecision = ParseDecimal(
                Preferences.Get("AssetStudio.FbxFilterPrecision", "0.25"), 0.25m),
            ExportAllNodes = Preferences.Get("AssetStudio.FbxExportAllNodes", true),
            ExportSkins = Preferences.Get("AssetStudio.FbxExportSkins", true),
            ExportAnimations = Preferences.Get("AssetStudio.FbxExportAnimations", true),
            ExportBlendShape = Preferences.Get("AssetStudio.FbxExportBlendShape", true),
            CastToBone = Preferences.Get("AssetStudio.FbxCastToBone", false),
            BoneSize = ParseDecimal(Preferences.Get("AssetStudio.FbxBoneSize", "10"), 10m),
            ExportAllUvsAsDiffuseMaps = Preferences.Get("AssetStudio.FbxAllUvsAsDiffuseMaps", false),
            ScaleFactor = ParseDecimal(Preferences.Get("AssetStudio.FbxScaleFactor", "1"), 1m),
            FbxVersionIndex = int.TryParse(version, out var parsedVersion)
                && parsedVersion is >= 0 and <= 5
                    ? parsedVersion
                    : 3
        };
    }

    public static void Save(MobileFbxExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Preferences.Set("AssetStudio.FbxFormat", options.Format.ToString());
        Preferences.Set("AssetStudio.FbxBakeSkinPose", options.BakeSkinPose);
        Preferences.Set("AssetStudio.FbxBakeBlendShapePose", options.BakeBlendShapePose);
        Preferences.Set("AssetStudio.FbxEulerFilter", options.EulerFilter);
        Preferences.Set("AssetStudio.FbxFilterPrecision", FormatDecimal(options.FilterPrecision));
        Preferences.Set("AssetStudio.FbxExportAllNodes", options.ExportAllNodes);
        Preferences.Set("AssetStudio.FbxExportSkins", options.ExportSkins);
        Preferences.Set("AssetStudio.FbxExportAnimations", options.ExportAnimations);
        Preferences.Set("AssetStudio.FbxExportBlendShape", options.ExportBlendShape);
        Preferences.Set("AssetStudio.FbxCastToBone", options.CastToBone);
        Preferences.Set("AssetStudio.FbxBoneSize", FormatDecimal(options.BoneSize));
        Preferences.Set("AssetStudio.FbxAllUvsAsDiffuseMaps", options.ExportAllUvsAsDiffuseMaps);
        Preferences.Set("AssetStudio.FbxScaleFactor", FormatDecimal(options.ScaleFactor));
        Preferences.Set("AssetStudio.FbxVersionIndex", options.FbxVersionIndex.ToString());
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static decimal ParseDecimal(string value, decimal fallback) =>
        decimal.TryParse(
            value,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
                ? parsed
                : fallback;

    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out T parsed) && Enum.IsDefined(typeof(T), parsed)
            ? parsed
            : fallback;
}

internal sealed class MobileExportSettings
{
    public MobileExportKind DefaultExportKind { get; init; } = MobileExportKind.Converted;
    public MobileImageFormat TextureFormat { get; init; } = MobileImageFormat.Png;
    public bool ConvertTextures { get; init; } = true;
    public bool ConvertAudio { get; init; } = true;
    public bool RestoreTextAssetExtension { get; init; } = true;
    public MobileArchiveGrouping ArchiveGrouping { get; init; } = MobileArchiveGrouping.TypeName;
    public MobileFbxExportOptions Fbx { get; init; } = new();

    public static MobileExportSettings Load()
    {
        var defaultKind = ParseEnum(
            Preferences.Get("AssetStudio.DefaultExportKind", nameof(MobileExportKind.Converted)),
            MobileExportKind.Converted);
        var textureFormat = ParseEnum(
            Preferences.Get("AssetStudio.TextureExportFormat", nameof(MobileImageFormat.Png)),
            MobileImageFormat.Png);
        var grouping = ParseEnum(
            Preferences.Get("AssetStudio.ArchiveGrouping", nameof(MobileArchiveGrouping.TypeName)),
            MobileArchiveGrouping.TypeName);
        return new MobileExportSettings
        {
            DefaultExportKind = defaultKind,
            TextureFormat = textureFormat,
            ConvertTextures = Preferences.Get("AssetStudio.ConvertTextures", true),
            ConvertAudio = Preferences.Get("AssetStudio.ConvertAudio", true),
            RestoreTextAssetExtension = Preferences.Get("AssetStudio.RestoreTextAssetExtension", true),
            ArchiveGrouping = grouping,
            Fbx = MobileFbxExportOptions.Load()
        };
    }

    public static void Save(MobileExportSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Preferences.Set("AssetStudio.DefaultExportKind", settings.DefaultExportKind.ToString());
        Preferences.Set("AssetStudio.TextureExportFormat", settings.TextureFormat.ToString());
        Preferences.Set("AssetStudio.ConvertTextures", settings.ConvertTextures);
        Preferences.Set("AssetStudio.ConvertAudio", settings.ConvertAudio);
        Preferences.Set("AssetStudio.RestoreTextAssetExtension", settings.RestoreTextAssetExtension);
        Preferences.Set("AssetStudio.ArchiveGrouping", settings.ArchiveGrouping.ToString());
        MobileFbxExportOptions.Save(settings.Fbx);
    }

    private static T ParseEnum<T>(string value, T fallback) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out T parsed) && Enum.IsDefined(typeof(T), parsed)
            ? parsed
            : fallback;
}
