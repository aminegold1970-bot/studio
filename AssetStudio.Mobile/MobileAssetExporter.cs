using System.Buffers.Binary;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using AssetStudio;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using System.Xml.Linq;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsQuaternion = System.Numerics.Quaternion;
using NumericsVector3 = System.Numerics.Vector3;

namespace AssetStudioMobile;

internal static class MobileAssetExporter
{
    private const int MaxTexturePixels = 16_000_000;

    public static string Export(AssetStudio.Object asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var (data, extension) = ExportConverted(asset);
        return WriteAssetFile(asset, data, extension);
    }

    public static MobileSingleExportResult ExportForShare(
        AssetStudio.Object asset,
        MobileExportKind kind,
        string? container = null,
        CancellationToken cancellationToken = default,
        MobileMonoBehaviourAssemblyService? monoBehaviourAssemblyService = null,
        MobileImageFormat imageFormat = MobileImageFormat.Png,
        bool convertTextures = true,
        bool convertAudio = true,
        bool restoreTextAssetExtension = true)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var payload = CreateExportPayload(
            asset, kind, container, cancellationToken, monoBehaviourAssemblyService, imageFormat,
            convertTextures, convertAudio, restoreTextAssetExtension);
        return new MobileSingleExportResult(
            WriteAssetFile(asset, payload.Data, payload.Extension, cancellationToken),
            payload.UsedRawFallback,
            payload.Warning);
    }

    public static string ExportRaw(AssetStudio.Object asset) =>
        WriteAssetFile(asset, asset.GetRawData(), ".dat");

    public static string ExportDump(
        AssetStudio.Object asset,
        MobileMonoBehaviourAssemblyService? monoBehaviourAssemblyService = null)
    {
        var (data, extension) = GetDumpData(asset, monoBehaviourAssemblyService);
        return WriteAssetFile(asset, data, extension);
    }

    public static MobileArchiveExportResult ExportArchive(
        IEnumerable<AssetStudio.Object> assets,
        MobileExportKind kind,
        IProgress<MainPage.AssetLoadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        MobileMonoBehaviourAssemblyService? monoBehaviourAssemblyService = null,
        MobileImageFormat imageFormat = MobileImageFormat.Png,
        bool convertTextures = true,
        MobileArchiveGrouping archiveGrouping = MobileArchiveGrouping.None,
        bool convertAudio = true,
        bool restoreTextAssetExtension = true) =>
        ExportArchive(
            assets?.Select(asset => new MobileExportItem(asset, null))
                ?? throw new ArgumentNullException(nameof(assets)),
            kind,
            progress,
            cancellationToken,
            monoBehaviourAssemblyService,
            imageFormat,
            convertTextures,
            archiveGrouping,
            convertAudio,
            restoreTextAssetExtension);

    public static MobileArchiveExportResult ExportArchive(
        IEnumerable<MobileExportItem> assets,
        MobileExportKind kind,
        IProgress<MainPage.AssetLoadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        MobileMonoBehaviourAssemblyService? monoBehaviourAssemblyService = null,
        MobileImageFormat imageFormat = MobileImageFormat.Png,
        bool convertTextures = true,
        MobileArchiveGrouping archiveGrouping = MobileArchiveGrouping.None,
        bool convertAudio = true,
        bool restoreTextAssetExtension = true)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var sourceItems = assets.ToList();
        if (sourceItems.Count == 0)
            throw new InvalidOperationException("There are no assets to add to the export archive.");

        var archivePath = Path.Combine(FileSystem.CacheDirectory, $"AssetStudio_{Guid.NewGuid():N}.zip");
        try
        {
            var report = new List<MobileArchiveReportEntry>(sourceItems.Count);
            var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AssetStudio_Export_Report.json"
            };
            using (var archive = new ZipArchive(File.Create(archivePath), ZipArchiveMode.Create))
            {
                var animationPathBindingsByAnimator =
                    new Dictionary<Animator, IReadOnlyDictionary<uint, string>>();
                for (var index = 0; index < sourceItems.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = sourceItems[index]
                        ?? throw new InvalidDataException($"Asset {index + 1} is null.");
                    var asset = item.Asset
                        ?? throw new InvalidDataException($"Asset {index + 1} has no parsed object.");
                    IReadOnlyDictionary<uint, string>? animationPathBindings = null;
                    if (kind == MobileExportKind.Converted
                        && asset is AnimationClip { m_Legacy: false }
                        && item.AnimationContext is { } animationContext)
                    {
                        if (!animationPathBindingsByAnimator.TryGetValue(
                                animationContext, out animationPathBindings))
                        {
                            animationPathBindings = GetAnimationPathBindings(
                                animationContext, cancellationToken);
                            animationPathBindingsByAnimator.Add(animationContext, animationPathBindings);
                        }
                    }
                    var payload = CreateExportPayload(
                        asset, kind, item.Container, cancellationToken,
                        monoBehaviourAssemblyService, imageFormat,
                        convertTextures, convertAudio, restoreTextAssetExtension, animationPathBindings);
                    var entryName = GetUniqueArchiveEntryName(
                        entryNames,
                        GetArchiveAssetName(item, payload.Extension, archiveGrouping),
                        payload.Extension);
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                    using (var output = entry.Open())
                    {
                        const int chunkSize = 1024 * 1024;
                        for (var offset = 0; offset < payload.Data.Length; offset += chunkSize)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var length = Math.Min(chunkSize, payload.Data.Length - offset);
                            output.Write(payload.Data, offset, length);
                        }
                    }

                    report.Add(new MobileArchiveReportEntry(
                        entryName,
                        GetAssetName(asset),
                        asset.type.ToString(),
                        asset.m_PathID,
                        payload.UsedRawFallback,
                        payload.Warning));
                    progress?.Report(new MainPage.AssetLoadProgress(
                        (index + 1) * 100 / sourceItems.Count,
                        $"Exported {index + 1} of {sourceItems.Count} assets"));
                }

                cancellationToken.ThrowIfCancellationRequested();
                var reportEntry = archive.CreateEntry("AssetStudio_Export_Report.json", CompressionLevel.Fastest);
                using var reportStream = reportEntry.Open();
                using var writer = new StreamWriter(reportStream, new UTF8Encoding(false));
                writer.Write(JsonConvert.SerializeObject(report, Formatting.Indented));
            }

            return new MobileArchiveExportResult(
                archivePath,
                sourceItems.Count,
                report.Count(entry => entry.RawFallback));
        }
        catch
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            throw;
        }
    }

    public static string ExportMergedMeshes(
        IEnumerable<Mesh> sourceMeshes,
        CancellationToken cancellationToken = default)
    {
        var meshes = sourceMeshes?.ToList() ?? new List<Mesh>();
        if (meshes.Count == 0)
            throw new InvalidOperationException("No meshes were selected for model export.");

        var objectFiles = meshes.Select((mesh, index) => (
            Name: string.IsNullOrWhiteSpace(mesh.m_Name) ? $"mesh_{index + 1}" : mesh.m_Name,
            Contents: ExportObj(mesh, cancellationToken)));
        return WriteMergedObj(objectFiles, "MergedMeshes", cancellationToken);
    }

    public static string ExportMeshAssetsArchive(
        IEnumerable<Mesh> sourceMeshes,
        IEnumerable<GameObject> materialContexts,
        CancellationToken cancellationToken = default,
        IEnumerable<AnimationClip>? animationClips = null,
        Animator? animationContext = null)
    {
        var geometries = GetMeshAssetGeometries(sourceMeshes, materialContexts, cancellationToken);
        if (geometries.Count == 0)
            throw new InvalidOperationException("No meshes were selected for model export.");

        return WriteSceneObjArchive(
            geometries, mergeObjects: false, cancellationToken, animationClips, animationContext);
    }

    public static string ExportMergedMeshAssetsArchive(
        IEnumerable<Mesh> sourceMeshes,
        IEnumerable<GameObject> materialContexts,
        CancellationToken cancellationToken = default,
        IEnumerable<AnimationClip>? animationClips = null,
        Animator? animationContext = null)
    {
        var geometries = GetMeshAssetGeometries(sourceMeshes, materialContexts, cancellationToken);
        if (geometries.Count == 0)
            throw new InvalidOperationException("No meshes were selected for model export.");

        return WriteSceneObjArchive(
            geometries, mergeObjects: true, cancellationToken, animationClips, animationContext);
    }

    public static string ExportMergedSceneObjects(
        IEnumerable<GameObject> gameObjects,
        CancellationToken cancellationToken = default,
        IEnumerable<AnimationClip>? animationClips = null,
        Animator? animationContext = null)
    {
        var sceneMeshes = GetSceneMeshGeometries(gameObjects, cancellationToken);
        if (sceneMeshes.Count == 0)
            throw new InvalidOperationException("The selected scene objects contain no exportable meshes.");

        return WriteSceneObjArchive(
            sceneMeshes, mergeObjects: true, cancellationToken, animationClips, animationContext);
    }

    public static string ExportSceneObjectsArchive(
        IEnumerable<GameObject> gameObjects,
        CancellationToken cancellationToken = default,
        IEnumerable<AnimationClip>? animationClips = null,
        Animator? animationContext = null)
    {
        var sceneMeshes = GetSceneMeshGeometries(gameObjects, cancellationToken);
        if (sceneMeshes.Count == 0)
            throw new InvalidOperationException("The selected scene objects contain no exportable meshes.");

        return WriteSceneObjArchive(
            sceneMeshes, mergeObjects: false, cancellationToken, animationClips, animationContext);
    }

    private static string WriteSceneObjArchive(
        IReadOnlyList<SceneMeshGeometry> sceneMeshes,
        bool mergeObjects,
        CancellationToken cancellationToken,
        IEnumerable<AnimationClip>? animationClips = null,
        Animator? animationContext = null)
    {
        var entryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        entryNames.Add("materials.mtl");
        var objectEntryNames = mergeObjects
            ? new[] { GetUniqueArchiveEntryName(entryNames, "MergedSceneObjects", ".obj") }
            : sceneMeshes.Select(item => GetUniqueArchiveEntryName(
                entryNames,
                SanitizeFileName(item.Name),
                ".obj")).ToArray();
        var materialBundle = BuildObjMaterialBundle(sceneMeshes, entryNames, cancellationToken);
        Directory.CreateDirectory(FileSystem.CacheDirectory);
        var archivePath = Path.Combine(
            FileSystem.CacheDirectory,
            $"AssetStudio_{(mergeObjects ? "MergedSceneObjects" : "SceneObjects")}_{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = new ZipArchive(File.Create(archivePath), ZipArchiveMode.Create))
            {
                foreach (var texture in materialBundle.Textures)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteArchiveEntry(archive, texture.Name, texture.Data);
                }

                if (materialBundle.Materials.Count > 0)
                {
                    WriteArchiveEntry(
                        archive,
                        "materials.mtl",
                        Encoding.UTF8.GetBytes(materialBundle.MaterialLibrary));
                }

                if (mergeObjects)
                {
                    var objectFiles = sceneMeshes.Select(item => (
                        item.Name,
                        Contents: ExportObj(
                            item.Mesh,
                            item.WorldTransform,
                            cancellationToken,
                            item.Materials,
                            materialBundle.Materials,
                            item.SkinTransforms,
                            item.SkinNormalTransforms,
                            item.BlendShapePositionDeltas,
                            item.BlendShapeNormalDeltas)));
                    var contents = BuildMergedObjContents(
                        objectFiles,
                        materialBundle.Materials.Count > 0,
                        cancellationToken);
                    WriteArchiveEntry(
                        archive,
                        objectEntryNames[0],
                        Encoding.UTF8.GetBytes(contents));
                }
                else
                {
                    for (var index = 0; index < sceneMeshes.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var item = sceneMeshes[index];
                        var contents = ExportObj(
                            item.Mesh,
                            item.WorldTransform,
                            cancellationToken,
                            item.Materials,
                            materialBundle.Materials,
                            item.SkinTransforms,
                            item.SkinNormalTransforms,
                            item.BlendShapePositionDeltas,
                            item.BlendShapeNormalDeltas);
                        var entry = archive.CreateEntry(objectEntryNames[index], CompressionLevel.Fastest);
                        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                        writer.Write(contents);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (animationClips != null)
                {
                    IReadOnlyDictionary<uint, string>? animationPathBindings = null;
                    foreach (var clip in animationClips.Distinct())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (clip == null)
                            continue;
                        if (!clip.m_Legacy && animationContext != null)
                        {
                            animationPathBindings ??= GetAnimationPathBindings(
                                animationContext, cancellationToken);
                        }
                        var payload = ExportAnimationClip(clip, cancellationToken, animationPathBindings);
                        var entryName = GetUniqueArchiveEntryName(
                            entryNames,
                            SanitizeFileName(string.IsNullOrWhiteSpace(clip.m_Name)
                                ? "AnimationClip"
                                : clip.m_Name),
                            ".anim.json");
                        WriteArchiveEntry(archive, entryName, payload.Data);
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return archivePath;
        }
        catch
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            throw;
        }
    }

    private static void WriteArchiveEntry(
        ZipArchive archive,
        string entryName,
        byte[] data)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var output = entry.Open();
        output.Write(data, 0, data.Length);
    }

    private static ObjMaterialBundle BuildObjMaterialBundle(
        IEnumerable<SceneMeshGeometry> sceneMeshes,
        HashSet<string> usedArchiveNames,
        CancellationToken cancellationToken)
    {
        var materialMap = new Dictionary<Material, ObjMaterialInfo>();
        var textures = new Dictionary<Texture2D, (string Name, byte[] Data)>();
        var materialNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        materialNames.Add("AssetStudio_Default");
        var library = new StringBuilder();
        var materialIndex = 0;
        library.AppendLine("newmtl AssetStudio_Default")
            .AppendLine("Ka 0.2 0.2 0.2")
            .AppendLine("Kd 1 1 1")
            .AppendLine("Ke 0 0 0")
            .AppendLine("Ks 0.2 0.2 0.2")
            .AppendLine("Ns 20")
            .AppendLine("d 1")
            .AppendLine("illum 2")
            .AppendLine();

        foreach (var material in sceneMeshes
            .SelectMany(item => item.Materials)
            .OfType<Material>()
            .Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeName = SanitizeObjIdentifier(
                $"{material.m_Name}_{material.m_PathID}",
                materialIndex++);
            var materialName = GetUniqueArchiveEntryName(materialNames, safeName, string.Empty);
            var diffuse = GetMaterialColor(
                material, ["_Color", "_BaseColor"], new AssetStudio.Color(0.8f, 0.8f, 0.8f, 1));
            var ambient = GetMaterialColor(
                material, ["_SColor"], new AssetStudio.Color(0.2f, 0.2f, 0.2f, 1));
            var emissive = GetMaterialColor(
                material, ["_EmissionColor"], new AssetStudio.Color(0, 0, 0, 1));
            var specular = GetMaterialColor(
                material, ["_SpecularColor"], new AssetStudio.Color(0.2f, 0.2f, 0.2f, 1));
            var shininess = GetMaterialFloat(material, ["_Shininess"], 20f);
            var transparency = GetMaterialFloat(material, ["_Transparency"], 0f);
            ValidateMaterialColor(material, diffuse);
            ValidateMaterialColor(material, ambient);
            ValidateMaterialColor(material, emissive);
            ValidateMaterialColor(material, specular);
            if (!float.IsFinite(shininess) || !float.IsFinite(transparency))
                throw new InvalidDataException($"Material '{material.m_Name}' contains a non-finite property.");
            var opacity = diffuse.A * (1 - Math.Clamp(transparency, 0, 1));

            library.Append("newmtl ").AppendLine(materialName)
                .Append("Ka ").Append(FormatColor(ambient)).AppendLine()
                .Append("Kd ").Append(FormatColor(diffuse)).AppendLine()
                .Append("Ke ").Append(FormatColor(emissive)).AppendLine()
                .Append("Ks ").Append(FormatColor(specular)).AppendLine()
                .Append("Ns ").Append(Math.Clamp(shininess, 0, 1000).ToString("R", CultureInfo.InvariantCulture)).AppendLine()
                .Append("d ").Append(opacity.ToString("R", CultureInfo.InvariantCulture)).AppendLine()
                .AppendLine("illum 2");
            foreach (var textureEnvironment in material.m_SavedProperties.m_TexEnvs)
            {
                var mapKeyword = textureEnvironment.Key switch
                {
                    "_MainTex" or "_BaseMap" => "map_Kd",
                    "_BumpMap" or "_NormalMap" => "map_Bump",
                    "_SpecularMap" or "_SpecGlossMap" or "_MetallicGlossMap" => "map_Ks",
                    "_EmissionMap" => "map_Ke",
                    _ => null
                };
                if (mapKeyword == null
                    || !textureEnvironment.Value.m_Texture.TryGet<Texture2D>(out var texture))
                {
                    continue;
                }

                var textureName = GetObjTextureName(
                    texture,
                    textures,
                    usedArchiveNames,
                    cancellationToken);
                var scale = textureEnvironment.Value.m_Scale;
                var offset = textureEnvironment.Value.m_Offset;
                if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y)
                    || !float.IsFinite(offset.X) || !float.IsFinite(offset.Y))
                {
                    throw new InvalidDataException(
                        $"Material '{material.m_Name}' has non-finite texture coordinates.");
                }

                library.Append(mapKeyword);
                if (scale.X != 1 || scale.Y != 1)
                {
                    library.Append(" -s ")
                        .Append(scale.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                        .Append(scale.Y.ToString("R", CultureInfo.InvariantCulture)).Append(" 1");
                }
                if (offset.X != 0 || offset.Y != 0)
                {
                    library.Append(" -o ")
                        .Append(offset.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                        .Append(offset.Y.ToString("R", CultureInfo.InvariantCulture)).Append(" 0");
                }
                library.Append(' ').AppendLine(textureName);
            }
            library.AppendLine();
            materialMap.Add(material, new ObjMaterialInfo(materialName));
        }

        return new ObjMaterialBundle(
            materialMap,
            library.ToString(),
            textures.Values.Select(texture => new ObjTextureEntry(texture.Name, texture.Data)).ToList());
    }

    private static string GetObjTextureName(
        Texture2D texture,
        Dictionary<Texture2D, (string Name, byte[] Data)> textures,
        HashSet<string> usedArchiveNames,
        CancellationToken cancellationToken)
    {
        if (textures.TryGetValue(texture, out var existing))
            return existing.Name;

        var (data, extension) = ExportTexture(texture, cancellationToken);
        var textureBaseName = SanitizeObjIdentifier(
            string.IsNullOrWhiteSpace(texture.m_Name) ? "Texture" : texture.m_Name,
            textures.Count);
        var textureName = GetUniqueArchiveEntryName(
            usedArchiveNames,
            $"{textureBaseName}_{texture.m_PathID}",
            extension);
        textures.Add(texture, (textureName, data));
        return textureName;
    }

    private static AssetStudio.Color GetMaterialColor(
        Material material,
        string[] propertyNames,
        AssetStudio.Color fallback)
    {
        foreach (var property in material.m_SavedProperties.m_Colors)
        {
            if (propertyNames.Contains(property.Key, StringComparer.Ordinal))
                return property.Value;
        }
        return fallback;
    }

    private static float GetMaterialFloat(Material material, string[] propertyNames, float fallback)
    {
        foreach (var property in material.m_SavedProperties.m_Floats)
        {
            if (propertyNames.Contains(property.Key, StringComparer.Ordinal))
                return property.Value;
        }
        return fallback;
    }

    private static void ValidateMaterialColor(Material material, AssetStudio.Color color)
    {
        if (!float.IsFinite(color.R) || !float.IsFinite(color.G)
            || !float.IsFinite(color.B) || !float.IsFinite(color.A))
        {
            throw new InvalidDataException($"Material '{material.m_Name}' contains a non-finite color.");
        }
    }

    private static string FormatColor(AssetStudio.Color color) =>
        string.Join(' ',
            color.R.ToString("R", CultureInfo.InvariantCulture),
            color.G.ToString("R", CultureInfo.InvariantCulture),
            color.B.ToString("R", CultureInfo.InvariantCulture));

    private static string WriteMergedObj(
        IEnumerable<(string Name, string Contents)> objectFiles,
        string outputName,
        CancellationToken cancellationToken)
    {
        var contents = BuildMergedObjContents(objectFiles, includeMaterialLibrary: false, cancellationToken);
        return WriteOutputFile(
            outputName,
            Encoding.UTF8.GetBytes(contents),
            ".obj",
            cancellationToken);
    }

    private static string BuildMergedObjContents(
        IEnumerable<(string Name, string Contents)> objectFiles,
        bool includeMaterialLibrary,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Merged by AssetStudio Mobile");
        if (includeMaterialLibrary)
            builder.AppendLine("mtllib materials.mtl");
        var vertexOffset = 0;
        var uvOffset = 0;
        var normalOffset = 0;
        var groupIndex = 0;
        foreach (var objectFile in objectFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vertices = 0;
            var uvs = 0;
            var normals = 0;
            builder.Append("g ")
                .Append(SanitizeObjGroupName(objectFile.Name, groupIndex++))
                .AppendLine();
            foreach (var rawLine in objectFile.Contents.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = rawLine.TrimEnd('\r');
                if (line.StartsWith("v ", StringComparison.Ordinal))
                {
                    builder.AppendLine(line);
                    vertices++;
                }
                else if (line.StartsWith("vt ", StringComparison.Ordinal))
                {
                    builder.AppendLine(line);
                    uvs++;
                }
                else if (line.StartsWith("vn ", StringComparison.Ordinal))
                {
                    builder.AppendLine(line);
                    normals++;
                }
                else if (line.StartsWith("usemtl ", StringComparison.Ordinal))
                {
                    builder.AppendLine(line);
                }
                else if (line.StartsWith("f ", StringComparison.Ordinal))
                {
                    builder.Append("f");
                    foreach (var reference in line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var indices = reference.Split('/');
                        builder.Append(' ').Append(OffsetObjReference(indices[0], vertexOffset));
                        if (indices.Length > 1)
                        {
                            builder.Append('/');
                            if (indices[1].Length > 0)
                                builder.Append(OffsetObjReference(indices[1], uvOffset));
                        }
                        if (indices.Length > 2)
                        {
                            builder.Append('/');
                            if (indices[2].Length > 0)
                                builder.Append(OffsetObjReference(indices[2], normalOffset));
                        }
                    }
                    builder.AppendLine();
                }
            }
            vertexOffset = checked(vertexOffset + vertices);
            uvOffset = checked(uvOffset + uvs);
            normalOffset = checked(normalOffset + normals);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return builder.ToString();
    }

    private static string SanitizeObjGroupName(string name, int fallbackIndex)
    {
        var groupName = string.Concat(name.Select(character =>
            char.IsWhiteSpace(character) || char.IsControl(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(groupName) ? $"object_{fallbackIndex}" : groupName;
    }

    private static string SanitizeObjIdentifier(string name, int fallbackIndex)
    {
        var identifier = string.Concat(name.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'
                ? character
                : '_'));
        return string.IsNullOrWhiteSpace(identifier) ? $"material_{fallbackIndex}" : identifier;
    }

    internal static List<SceneMeshGeometry> GetSceneMeshGeometries(
        IEnumerable<GameObject> gameObjects,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gameObjects);
        var geometries = new List<SceneMeshGeometry>();
        var transformCache = new Dictionary<AssetStudio.Transform, NumericsMatrix4x4>();
        var activeTransforms = new HashSet<AssetStudio.Transform>();
        foreach (var gameObject in gameObjects.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gameObject.m_Transform is not { } transform)
                continue;

            var worldTransform = GetWorldTransform(
                transform,
                transformCache,
                activeTransforms,
                cancellationToken);
            foreach (var pointer in gameObject.m_Components)
            {
                if (!pointer.TryGet(out var component))
                    continue;

                Mesh? mesh;
                Renderer? renderer;
                SkinnedMeshRenderer? skinnedRenderer;
                switch (component)
                {
                    case MeshFilter meshFilter when meshFilter.m_Mesh.TryGet(out var value):
                        mesh = value;
                        renderer = gameObject.m_MeshRenderer;
                        skinnedRenderer = null;
                        break;
                    case SkinnedMeshRenderer skinnedMeshRenderer
                        when skinnedMeshRenderer.m_Mesh.TryGet(out var value):
                        mesh = value;
                        renderer = skinnedMeshRenderer;
                        skinnedRenderer = skinnedMeshRenderer;
                        break;
                    default:
                        continue;
                }

                if (mesh == null)
                    continue;

                var materials = renderer?.m_Materials?
                    .Select(materialPointer =>
                        materialPointer.TryGet(out var material) ? material : null)
                    .ToArray() ?? Array.Empty<Material>();
                var objectName = string.IsNullOrWhiteSpace(gameObject.m_Name)
                    ? string.IsNullOrWhiteSpace(mesh.m_Name) ? "Mesh" : mesh.m_Name
                    : string.IsNullOrWhiteSpace(mesh.m_Name)
                        ? gameObject.m_Name
                        : $"{gameObject.m_Name}_{mesh.m_Name}";
                var (skinTransforms, skinNormalTransforms) = skinnedRenderer == null
                    ? (null, null)
                    : GetSkinTransforms(
                        mesh,
                        skinnedRenderer,
                        transform,
                        transformCache,
                        activeTransforms,
                        cancellationToken);
                var (blendShapePositionDeltas, blendShapeNormalDeltas) = skinnedRenderer == null
                    ? (null, null)
                    : GetBlendShapeDeltas(mesh, skinnedRenderer, cancellationToken);
                geometries.Add(new SceneMeshGeometry(
                    mesh,
                    worldTransform,
                    objectName,
                    materials,
                    skinTransforms,
                    skinNormalTransforms,
                    blendShapePositionDeltas,
                    blendShapeNormalDeltas));
            }
        }

        return geometries;
    }

    internal static List<SceneMeshGeometry> GetMeshAssetGeometries(
        IEnumerable<Mesh> sourceMeshes,
        IEnumerable<GameObject> materialContexts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceMeshes);
        ArgumentNullException.ThrowIfNull(materialContexts);
        var meshes = sourceMeshes.Distinct().ToList();
        if (meshes.Count == 0)
            return new List<SceneMeshGeometry>();

        var selectedMeshes = meshes.ToHashSet();
        var materialBindings = new Dictionary<Mesh, Material?[]>();
        foreach (var gameObject in materialContexts.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var pointer in gameObject.m_Components)
            {
                if (!pointer.TryGet(out var component))
                    continue;

                Mesh? mesh;
                Renderer? renderer;
                switch (component)
                {
                    case MeshFilter meshFilter when meshFilter.m_Mesh.TryGet(out var value):
                        mesh = value;
                        renderer = gameObject.m_MeshRenderer;
                        break;
                    case SkinnedMeshRenderer skinnedRenderer
                        when skinnedRenderer.m_Mesh.TryGet(out var value):
                        mesh = value;
                        renderer = skinnedRenderer;
                        break;
                    default:
                        continue;
                }

                if (mesh == null || !selectedMeshes.Contains(mesh) || materialBindings.ContainsKey(mesh))
                    continue;
                var materials = renderer?.m_Materials?
                    .Select(materialPointer =>
                        materialPointer.TryGet(out var material) ? material : null)
                    .ToArray() ?? Array.Empty<Material?>();
                if (materials.Any(material => material != null))
                    materialBindings.Add(mesh, materials);
            }
        }

        var result = new List<SceneMeshGeometry>(meshes.Count);
        for (var index = 0; index < meshes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mesh = meshes[index];
            result.Add(new SceneMeshGeometry(
                mesh,
                NumericsMatrix4x4.Identity,
                string.IsNullOrWhiteSpace(mesh.m_Name) ? $"mesh_{index + 1}" : mesh.m_Name,
                materialBindings.TryGetValue(mesh, out var materials)
                    ? materials
                    : Array.Empty<Material?>(),
                null,
                null,
                null,
                null));
        }

        return result;
    }

    private static NumericsMatrix4x4 GetWorldTransform(
        AssetStudio.Transform transform,
        Dictionary<AssetStudio.Transform, NumericsMatrix4x4> transformCache,
        HashSet<AssetStudio.Transform> activeTransforms,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (transformCache.TryGetValue(transform, out var cached))
            return cached;
        if (!activeTransforms.Add(transform))
            throw new InvalidDataException("The scene contains a cycle in its transform hierarchy.");

        try
        {
            var position = transform.m_LocalPosition;
            var scale = transform.m_LocalScale;
            var rotation = transform.m_LocalRotation;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
                || !float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z)
                || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y)
                || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
            {
                throw new InvalidDataException("The scene contains a non-finite transform.");
            }

            var targetRotation = new NumericsQuaternion(rotation.X, -rotation.Y, -rotation.Z, rotation.W);
            var rotationLengthSquared = targetRotation.LengthSquared();
            if (!float.IsFinite(rotationLengthSquared) || rotationLengthSquared <= 1e-12f)
                throw new InvalidDataException("The scene contains an invalid transform rotation.");
            targetRotation = NumericsQuaternion.Normalize(targetRotation);

            var localTransform =
                NumericsMatrix4x4.CreateScale(scale.X, scale.Y, scale.Z)
                * NumericsMatrix4x4.CreateFromQuaternion(targetRotation)
                * NumericsMatrix4x4.CreateTranslation(-position.X, position.Y, position.Z);
            var worldTransform = transform.m_Father.TryGet(out var parent)
                ? localTransform * GetWorldTransform(parent, transformCache, activeTransforms, cancellationToken)
                : localTransform;
            transformCache.Add(transform, worldTransform);
            return worldTransform;
        }
        finally
        {
            activeTransforms.Remove(transform);
        }
    }

    private static (NumericsMatrix4x4[]? PositionTransforms, NumericsMatrix4x4[]? NormalTransforms)
        GetSkinTransforms(
            Mesh mesh,
            SkinnedMeshRenderer renderer,
            AssetStudio.Transform rendererTransform,
            Dictionary<AssetStudio.Transform, NumericsMatrix4x4> transformCache,
            HashSet<AssetStudio.Transform> activeTransforms,
            CancellationToken cancellationToken)
    {
        var skin = mesh.m_Skin;
        if (skin is not { Length: > 0 })
            return (null, null);
        if (skin.Length != mesh.m_VertexCount)
            throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' has an invalid bone-weight count.");

        var hasInfluences = false;
        foreach (var vertexWeights in skin)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var influence = 0; influence < 4; influence++)
            {
                var weight = vertexWeights.weight[influence];
                if (!float.IsFinite(weight) || weight < 0)
                    throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' has invalid bone weights.");
                hasInfluences |= weight > 0;
            }
        }
        if (!hasInfluences)
            return (null, null);

        if (mesh.m_BindPose is not { Length: > 0 } bindPoses)
        {
            throw new NotSupportedException(
                $"Skinned mesh '{mesh.m_Name}' has weights but no usable bind poses.");
        }

        var bones = renderer.m_Bones ?? Array.Empty<PPtr<AssetStudio.Transform>>();
        var hasBoneHashes = mesh.m_BoneNameHashes is { } boneNameHashes
            && boneNameHashes.Length == bindPoses.Length;
        if (bones.Length == 0 && !hasBoneHashes)
        {
            throw new NotSupportedException(
                $"Skinned mesh '{mesh.m_Name}' has weights but no usable bind poses or renderer bones.");
        }

        Dictionary<uint, AssetStudio.Transform>? hashLookup = null;
        var reflection = NumericsMatrix4x4.CreateScale(-1, 1, 1);
        var positionTransforms = new NumericsMatrix4x4[bindPoses.Length];
        var normalTransforms = new NumericsMatrix4x4[bindPoses.Length];
        for (var index = 0; index < bindPoses.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssetStudio.Transform? bone = null;
            if (index < bones.Length && bones[index].TryGet(out var resolvedBone))
                bone = resolvedBone;
            if (bone == null
                && mesh.m_BoneNameHashes is { } boneHashes
                && boneHashes.Length == bindPoses.Length)
            {
                hashLookup ??= BuildTransformHashLookup(rendererTransform, cancellationToken);
                if (hashLookup.TryGetValue(boneHashes[index], out var hashBone))
                    bone = hashBone;
            }
            if (bone == null)
            {
                throw new NotSupportedException(
                    $"Skinned mesh '{mesh.m_Name}' has an unresolved bone at index {index}.");
            }

            var bindPose = ToNumericsRowMatrix(bindPoses[index]);
            var skinTransform = reflection * bindPose * reflection
                * GetWorldTransform(bone, transformCache, activeTransforms, cancellationToken);
            if (!IsFinite(skinTransform)
                || !NumericsMatrix4x4.Invert(skinTransform, out var inverseTransform))
            {
                throw new InvalidDataException(
                    $"Skinned mesh '{mesh.m_Name}' has a singular or non-finite bone transform.");
            }
            positionTransforms[index] = skinTransform;
            normalTransforms[index] = NumericsMatrix4x4.Transpose(inverseTransform);
        }

        return (positionTransforms, normalTransforms);
    }

    private static (NumericsVector3[]? PositionDeltas, NumericsVector3[]? NormalDeltas)
        GetBlendShapeDeltas(
            Mesh mesh,
            SkinnedMeshRenderer renderer,
            CancellationToken cancellationToken)
    {
        var blendShapes = mesh.m_Shapes;
        if (blendShapes?.channels is not { Length: > 0 } channels)
            return (null, null);

        NumericsVector3[]? positionDeltas = null;
        NumericsVector3[]? normalDeltas = null;
        for (var channelIndex = 0; channelIndex < channels.Length; channelIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var weight = renderer.m_BlendShapeWeights is { } weights && channelIndex < weights.Length
                ? weights[channelIndex]
                : 0;
            if (!float.IsFinite(weight))
                throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' has a non-finite blend-shape weight.");
            if (weight == 0)
                continue;

            var channel = channels[channelIndex];
            if (channel.frameCount <= 0
                || channel.frameIndex < 0
                || (long)channel.frameIndex + channel.frameCount > blendShapes.shapes.Length
                || (long)channel.frameIndex + channel.frameCount > blendShapes.fullWeights.Length)
            {
                throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' has an invalid blend-shape frame range.");
            }

            var previousWeight = 0f;
            NumericsVector3[]? previousPositions = null;
            NumericsVector3[]? previousNormals = null;
            var applied = false;
            for (var frameOffset = 0; frameOffset < channel.frameCount; frameOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frameIndex = channel.frameIndex + frameOffset;
                var fullWeight = blendShapes.fullWeights[frameIndex];
                if (!float.IsFinite(fullWeight) || fullWeight <= previousWeight)
                    throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' has invalid blend-shape frame weights.");

                var framePositions = new NumericsVector3[mesh.m_VertexCount];
                NumericsVector3[]? frameNormals = null;
                var shape = blendShapes.shapes[frameIndex];
                if ((ulong)shape.firstVertex + shape.vertexCount > (ulong)blendShapes.vertices.Length)
                    throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' has an invalid blend-shape vertex range.");
                if (shape.hasNormals && mesh.m_Normals is { Length: > 0 })
                    frameNormals = new NumericsVector3[mesh.m_VertexCount];

                var shapeEnd = checked((int)(shape.firstVertex + shape.vertexCount));
                for (var deltaIndex = (int)shape.firstVertex; deltaIndex < shapeEnd; deltaIndex++)
                {
                    if ((deltaIndex & 1023) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var delta = blendShapes.vertices[deltaIndex];
                    if (delta.index >= mesh.m_VertexCount)
                        throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' has an out-of-range blend-shape vertex.");
                    if (!IsFinite(delta.vertex)
                        || (frameNormals != null && !IsFinite(delta.normal)))
                    {
                        throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' has a non-finite blend-shape delta.");
                    }
                    framePositions[delta.index] += new NumericsVector3(
                        -delta.vertex.X,
                        delta.vertex.Y,
                        delta.vertex.Z);
                    if (frameNormals != null)
                    {
                        frameNormals[delta.index] += new NumericsVector3(
                            -delta.normal.X,
                            delta.normal.Y,
                            delta.normal.Z);
                    }
                }

                if (weight <= fullWeight || frameOffset == channel.frameCount - 1)
                {
                    var amount = (weight - previousWeight) / (fullWeight - previousWeight);
                    amount = frameOffset == 0
                        ? Math.Min(amount, 1)
                        : Math.Clamp(amount, 0, 1);
                    positionDeltas ??= new NumericsVector3[mesh.m_VertexCount];
                    AccumulateBlendShapeFrame(
                        positionDeltas,
                        previousPositions,
                        framePositions,
                        amount,
                        cancellationToken);
                    if (frameNormals != null || previousNormals != null)
                    {
                        normalDeltas ??= new NumericsVector3[mesh.m_VertexCount];
                        AccumulateBlendShapeFrame(
                            normalDeltas,
                            previousNormals,
                            frameNormals,
                            amount,
                            cancellationToken);
                    }
                    applied = true;
                    break;
                }

                previousWeight = fullWeight;
                previousPositions = framePositions;
                previousNormals = frameNormals;
            }

            if (!applied)
                throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' has an unapplied blend-shape weight.");
        }

        return (positionDeltas, normalDeltas);
    }

    private static void AccumulateBlendShapeFrame(
        NumericsVector3[] destination,
        NumericsVector3[]? previousFrame,
        NumericsVector3[]? currentFrame,
        float amount,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            if ((index & 2047) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var previous = previousFrame == null ? NumericsVector3.Zero : previousFrame[index];
            var current = currentFrame == null ? NumericsVector3.Zero : currentFrame[index];
            destination[index] += previous + (current - previous) * amount;
        }
    }

    private static bool IsFinite(AssetStudio.Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);

    private static Dictionary<uint, AssetStudio.Transform> BuildTransformHashLookup(
        AssetStudio.Transform rendererTransform,
        CancellationToken cancellationToken)
    {
        var root = rendererTransform;
        var ancestors = new HashSet<AssetStudio.Transform>();
        while (root.m_Father.TryGet(out var parent))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ancestors.Add(root))
                throw new InvalidDataException("The scene contains a cycle in its transform hierarchy.");
            root = parent;
        }

        var lookup = new Dictionary<uint, AssetStudio.Transform>();
        var ambiguousHashes = new HashSet<uint>();
        var visited = new HashSet<AssetStudio.Transform>();
        var pending = new Stack<AssetStudio.Transform>();
        pending.Push(root);
        while (pending.TryPop(out var transform))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(transform))
                continue;

            var path = GetTransformPathByFather(transform);
            if (path != null)
            {
                var hash = ComputeBonePathHash(path);
                if (lookup.TryGetValue(hash, out var existing) && existing != transform)
                {
                    lookup.Remove(hash);
                    ambiguousHashes.Add(hash);
                }
                else if (!ambiguousHashes.Contains(hash))
                {
                    lookup[hash] = transform;
                }
            }

            foreach (var childPointer in transform.m_Children)
            {
                if (childPointer.TryGet(out var child))
                    pending.Push(child);
            }
        }
        return lookup;
    }

    private static string? GetTransformPathByFather(AssetStudio.Transform transform)
    {
        var names = new List<string>();
        var visited = new HashSet<AssetStudio.Transform>();
        var current = transform;
        while (true)
        {
            if (!visited.Add(current)
                || !current.m_GameObject.TryGet(out var gameObject)
                || string.IsNullOrWhiteSpace(gameObject.m_Name))
            {
                return null;
            }
            names.Add(gameObject.m_Name);
            if (!current.m_Father.TryGet(out var parent))
                break;
            current = parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    private static uint ComputeBonePathHash(string path)
    {
        var crc = 0xffffffffu;
        foreach (var value in Encoding.UTF8.GetBytes(path))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        return ~crc;
    }

    private static NumericsMatrix4x4 ToNumericsRowMatrix(AssetStudio.Matrix4x4 matrix) =>
        new(
            matrix.M00, matrix.M10, matrix.M20, matrix.M30,
            matrix.M01, matrix.M11, matrix.M21, matrix.M31,
            matrix.M02, matrix.M12, matrix.M22, matrix.M32,
            matrix.M03, matrix.M13, matrix.M23, matrix.M33);

    private static bool IsFinite(NumericsMatrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12)
        && float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14)
        && float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22)
        && float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24)
        && float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32)
        && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34)
        && float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42)
        && float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

    internal static NumericsVector3 TransformVertexPosition(
        Mesh mesh,
        int vertexIndex,
        NumericsVector3 position,
        NumericsMatrix4x4 worldTransform,
        NumericsMatrix4x4[]? skinTransforms)
    {
        if (skinTransforms == null)
            return NumericsVector3.Transform(position, worldTransform);

        var result = NumericsVector3.Zero;
        var totalWeight = 0f;
        var weights = mesh.m_Skin![vertexIndex];
        for (var influence = 0; influence < 4; influence++)
        {
            var weight = weights.weight[influence];
            if (weight <= 0)
                continue;
            var boneIndex = weights.boneIndex[influence];
            if ((uint)boneIndex >= (uint)skinTransforms.Length)
                throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' references an invalid bone index.");
            result += NumericsVector3.Transform(position, skinTransforms[boneIndex]) * weight;
            totalWeight += weight;
        }
        return totalWeight > 1e-6f
            ? result / totalWeight
            : NumericsVector3.Transform(position, worldTransform);
    }

    internal static NumericsVector3 TransformVertexNormal(
        Mesh mesh,
        int vertexIndex,
        NumericsVector3 normal,
        NumericsMatrix4x4 worldNormalTransform,
        NumericsMatrix4x4[]? skinNormalTransforms)
    {
        if (skinNormalTransforms == null)
            return NumericsVector3.TransformNormal(normal, worldNormalTransform);

        var result = NumericsVector3.Zero;
        var totalWeight = 0f;
        var weights = mesh.m_Skin![vertexIndex];
        for (var influence = 0; influence < 4; influence++)
        {
            var weight = weights.weight[influence];
            if (weight <= 0)
                continue;
            var boneIndex = weights.boneIndex[influence];
            if ((uint)boneIndex >= (uint)skinNormalTransforms.Length)
                throw new InvalidDataException($"Skinned mesh '{mesh.m_Name}' references an invalid bone index.");
            result += NumericsVector3.TransformNormal(normal, skinNormalTransforms[boneIndex]) * weight;
            totalWeight += weight;
        }
        return totalWeight > 1e-6f
            ? result / totalWeight
            : NumericsVector3.TransformNormal(normal, worldNormalTransform);
    }

    internal readonly record struct SceneMeshGeometry(
        Mesh Mesh,
        NumericsMatrix4x4 WorldTransform,
        string Name,
        Material?[] Materials,
        NumericsMatrix4x4[]? SkinTransforms,
        NumericsMatrix4x4[]? SkinNormalTransforms,
        NumericsVector3[]? BlendShapePositionDeltas,
        NumericsVector3[]? BlendShapeNormalDeltas);

    private static string WriteAssetFile(
        AssetStudio.Object asset,
        byte[] data,
        string extension,
        CancellationToken cancellationToken = default)
    {
        return WriteOutputFile(GetAssetFileName(asset, extension), data, extension, cancellationToken);
    }

    private static string WriteOutputFile(
        string name,
        byte[] data,
        string extension,
        CancellationToken cancellationToken = default)
    {
        var safeName = SanitizeFileName(name);
        var outputDirectory = Path.Combine(FileSystem.CacheDirectory, $"AssetStudio_Export_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, $"{safeName}{extension}");
        try
        {
            using var output = File.Create(path);
            const int chunkSize = 1024 * 1024;
            for (var offset = 0; offset < data.Length; offset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(chunkSize, data.Length - offset);
                output.Write(data, offset, length);
            }
            return path;
        }
        catch
        {
            if (File.Exists(path))
                File.Delete(path);
            if (Directory.Exists(outputDirectory)
                && !Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            {
                Directory.Delete(outputDirectory);
            }
            throw;
        }
    }

    private static string GetAssetName(AssetStudio.Object asset) =>
        asset is NamedObject named && !string.IsNullOrWhiteSpace(named.m_Name)
            ? named.m_Name
            : asset.type.ToString();

    private static string GetAssetFileName(AssetStudio.Object asset, string extension)
    {
        var name = GetAssetName(asset);
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length];
        return SanitizeFileName(name);
    }

    private static string GetArchiveAssetName(
        MobileExportItem item,
        string extension,
        MobileArchiveGrouping archiveGrouping)
    {
        var fileName = GetAssetFileName(item.Asset, extension);
        var groupPath = archiveGrouping switch
        {
            MobileArchiveGrouping.TypeName => SanitizeGroupPath(item.Asset.type.ToString()),
            MobileArchiveGrouping.ContainerPath => GetContainerGroupPath(item.Container),
            MobileArchiveGrouping.SourceFileName => GetSourceFileGroupPath(item.SourceFile),
            MobileArchiveGrouping.None => string.Empty,
            _ => throw new ArgumentOutOfRangeException(
                nameof(archiveGrouping), archiveGrouping, "Unsupported archive grouping option.")
        };
        return string.IsNullOrEmpty(groupPath) ? fileName : $"{groupPath}/{fileName}";
    }

    private static string GetContainerGroupPath(string? container)
    {
        if (string.IsNullOrWhiteSpace(container))
            return "Unassigned";

        var normalized = container.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0)
            segments = segments[..^1];
        return SanitizeGroupPath(string.Join('/', segments));
    }

    private static string GetSourceFileGroupPath(string? sourceFile)
    {
        var sourceName = string.IsNullOrWhiteSpace(sourceFile)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(sourceFile);
        return SanitizeGroupPath(sourceName);
    }

    private static string SanitizeGroupPath(string groupPath)
    {
        var segments = groupPath.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment is not ("." or ".."))
            .Select(SanitizeFileName)
            .ToArray();
        return segments.Length == 0 ? "Unassigned" : string.Join('/', segments);
    }

    private static string GetUniqueArchiveEntryName(
        HashSet<string> entryNames,
        string assetName,
        string extension)
    {
        var candidate = $"{assetName}{extension}";
        for (var duplicateIndex = 2; !entryNames.Add(candidate); duplicateIndex++)
            candidate = $"{assetName}_{duplicateIndex}{extension}";
        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var safeName = string.Concat(name.Select(character =>
            char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character))
            .TrimEnd('.', ' ');
        if (safeName.Length > 120)
            safeName = safeName[..120].TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(safeName) ? "AssetStudioExport" : safeName;
    }

    private static ExportPayload CreateExportPayload(
        AssetStudio.Object asset,
        MobileExportKind kind,
        string? container,
        CancellationToken cancellationToken,
        MobileMonoBehaviourAssemblyService? monoBehaviourAssemblyService,
        MobileImageFormat imageFormat,
        bool convertTextures,
        bool convertAudio,
        bool restoreTextAssetExtension,
        IReadOnlyDictionary<uint, string>? animationPathBindings = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var (data, extension) = kind switch
            {
                MobileExportKind.Raw => (asset.GetRawData(), ".dat"),
                MobileExportKind.Dump => GetDumpData(asset, monoBehaviourAssemblyService),
                _ => ExportConverted(
                    asset, container, cancellationToken, monoBehaviourAssemblyService, imageFormat,
                    convertTextures, convertAudio, restoreTextAssetExtension, animationPathBindings)
            };
            cancellationToken.ThrowIfCancellationRequested();
            return new ExportPayload(data, extension, false, null);
        }
        catch (Exception exception) when (
            kind != MobileExportKind.Raw
            && exception is (NotSupportedException or InvalidDataException))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestedFormat = kind == MobileExportKind.Dump ? "Dump" : "Converted";
            return new ExportPayload(
                asset.GetRawData(),
                ".dat",
                true,
                $"{requestedFormat} export unavailable for {asset.type}: {exception.Message}");
        }
    }

    private static (byte[] Data, string Extension) GetDumpData(
        AssetStudio.Object asset,
        MobileMonoBehaviourAssemblyService? monoBehaviourAssemblyService)
    {
        string? dump;
        if (asset is MonoBehaviour behaviour && monoBehaviourAssemblyService != null)
        {
            dump = monoBehaviourAssemblyService.GetDump(behaviour, out var error);
            if (dump is null)
                throw new NotSupportedException(
                    error ?? "The assembly-derived MonoBehaviour dump could not be read.");
        }
        else
        {
            dump = asset.Dump();
        }

        if (dump is null)
            throw new NotSupportedException($"Asset type {asset.type} does not provide a text dump.");
        return (Encoding.UTF8.GetBytes(dump), ".txt");
    }

    private static (byte[] Data, string Extension) ExportAnimationClip(
        AnimationClip clip,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<uint, string>? animationPathBindings = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!clip.m_Legacy)
            return ExportMuscleStreamAnimationClip(clip, cancellationToken, animationPathBindings);

        var exportData = new
        {
            schemaVersion = 1,
            name = clip.m_Name,
            coordinateSpace = "Unity",
            isLegacy = clip.m_Legacy,
            animationType = clip.m_AnimationType.ToString(),
            compressed = clip.m_Compressed,
            sampleRate = RequireFinite(clip.m_SampleRate, "sample rate"),
            wrapMode = clip.m_WrapMode,
            rotationCurves = (clip.m_RotationCurves ?? Array.Empty<QuaternionCurve>())
                .Select(curve =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new
                    {
                        path = curve.path,
                        preInfinity = curve.curve.m_PreInfinity,
                        postInfinity = curve.curve.m_PostInfinity,
                        rotationOrder = curve.curve.m_RotationOrder,
                        keys = curve.curve.m_Curve
                            .Select(key => ExportQuaternionKey(key, cancellationToken))
                    };
                }),
            compressedRotationCurves = (clip.m_CompressedRotationCurves ?? Array.Empty<CompressedAnimationCurve>())
                .Select(curve => ExportCompressedRotationCurve(curve, cancellationToken)),
            eulerCurves = (clip.m_EulerCurves ?? Array.Empty<Vector3Curve>())
                .Select(curve => ExportVector3Curve(curve, cancellationToken)),
            positionCurves = (clip.m_PositionCurves ?? Array.Empty<Vector3Curve>())
                .Select(curve => ExportVector3Curve(curve, cancellationToken)),
            scaleCurves = (clip.m_ScaleCurves ?? Array.Empty<Vector3Curve>())
                .Select(curve => ExportVector3Curve(curve, cancellationToken)),
            floatCurves = (clip.m_FloatCurves ?? Array.Empty<FloatCurve>())
                .Select(curve =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new
                    {
                        path = curve.path,
                        attribute = curve.attribute,
                        classId = (int)curve.classID,
                        preInfinity = curve.curve.m_PreInfinity,
                        postInfinity = curve.curve.m_PostInfinity,
                        rotationOrder = curve.curve.m_RotationOrder,
                        keys = curve.curve.m_Curve
                            .Select(key => ExportFloatKey(key, cancellationToken))
                    };
                }),
            objectReferenceCurves = (clip.m_PPtrCurves ?? Array.Empty<PPtrCurve>())
                .Select(curve =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new
                    {
                        path = curve.path,
                        attribute = curve.attribute,
                        classId = curve.classID,
                        keys = curve.curve.Select(key =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return new
                            {
                                time = RequireFinite(key.time, "object-reference key time"),
                                fileId = key.value.m_FileID,
                                pathId = key.value.m_PathID
                            };
                        })
                    };
                }),
            events = (clip.m_Events ?? Array.Empty<AnimationEvent>())
                .Select(animationEvent =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new
                    {
                        time = RequireFinite(animationEvent.time, "event time"),
                        functionName = animationEvent.functionName,
                        data = animationEvent.data,
                        floatParameter = RequireFinite(animationEvent.floatParameter, "event float parameter"),
                        intParameter = animationEvent.intParameter,
                        messageOptions = animationEvent.messageOptions,
                        objectReferenceFileId = animationEvent.objectReferenceParameter.m_FileID,
                        objectReferencePathId = animationEvent.objectReferenceParameter.m_PathID
                    };
                })
        };

        cancellationToken.ThrowIfCancellationRequested();
        return (
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(exportData, Formatting.Indented)),
            ".anim.json");
    }

    private static (byte[] Data, string Extension) ExportMuscleStreamAnimationClip(
        AnimationClip clip,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<uint, string>? animationPathBindings)
    {
        var muscleClip = clip.m_MuscleClip
            ?? throw new NotSupportedException("This AnimationClip has no decoded muscle-stream data.");
        var serializedClip = muscleClip.m_Clip
            ?? throw new NotSupportedException("This AnimationClip has no decoded curve stream.");
        var bindingConstant = clip.m_ClipBindingConstant;
        if (bindingConstant == null)
        {
            if (serializedClip.m_Binding == null)
                throw new NotSupportedException("This AnimationClip has no serialized curve bindings.");
            bindingConstant = serializedClip.ConvertValueArrayToGenericBinding();
        }
        if (bindingConstant.genericBindings is not { } genericBindings)
            throw new InvalidDataException("This AnimationClip has no usable curve bindings.");

        var (bindingRows, bindingByValueIndex) =
            BuildMuscleCurveBindings(genericBindings, animationPathBindings);
        var streamedClip = serializedClip.m_StreamedClip
            ?? throw new InvalidDataException("This AnimationClip has no streamed-curve table.");
        var decodedFrames = ReadMuscleStreamFrames(streamedClip, cancellationToken);
        var streamedFrames = new object[decodedFrames.Count];
        for (var frameIndex = 0; frameIndex < decodedFrames.Count; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = decodedFrames[frameIndex];
            var keys = new object[frame.keyList.Length];
            for (var keyIndex = 0; keyIndex < frame.keyList.Length; keyIndex++)
            {
                if ((keyIndex & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var key = frame.keyList[keyIndex];
                if (float.IsNaN(key.inSlope) || float.IsNegativeInfinity(key.inSlope))
                    throw new InvalidDataException("A streamed animation key has an invalid in-slope.");
                keys[keyIndex] = new
                {
                    valueIndex = key.index,
                    binding = bindingByValueIndex.TryGetValue(key.index, out var binding) ? binding : null,
                    time = RequireFinite(frame.time, "streamed frame time"),
                    value = RequireFinite(key.value, "streamed key value"),
                    inSlope = float.IsFinite(key.inSlope) ? key.inSlope : (float?)null,
                    stepped = float.IsPositiveInfinity(key.inSlope),
                    outSlope = RequireFinite(key.outSlope, "streamed key out-slope"),
                    coefficients = key.coeff.Select(
                        coefficient => RequireFinite(coefficient, "streamed key coefficient")).ToArray()
                };
            }
            streamedFrames[frameIndex] = new { time = RequireFinite(frame.time, "streamed frame time"), keys };
        }

        var denseClip = serializedClip.m_DenseClip
            ?? throw new InvalidDataException("This AnimationClip has no dense-curve table.");
        if (denseClip.m_FrameCount < 0)
            throw new InvalidDataException("This AnimationClip has a negative dense frame count.");
        var denseSampleCount = (long)denseClip.m_FrameCount * denseClip.m_CurveCount;
        var denseSamples = denseClip.m_SampleArray
            ?? throw new InvalidDataException("This AnimationClip has no dense sample data.");
        if (denseSampleCount > int.MaxValue || denseSampleCount != denseSamples.Length)
            throw new InvalidDataException("This AnimationClip has an invalid dense sample count.");
        if (denseClip.m_FrameCount > 0
            && (!float.IsFinite(denseClip.m_SampleRate) || denseClip.m_SampleRate <= 0))
        {
            throw new InvalidDataException("This AnimationClip has an invalid dense sample rate.");
        }
        var constantBaseValueIndex = (long)streamedClip.curveCount + denseClip.m_CurveCount;
        if (constantBaseValueIndex > int.MaxValue)
            throw new InvalidDataException("This AnimationClip has an oversized curve binding index.");

        var denseFrames = new object[denseClip.m_FrameCount];
        var samplesPerFrame = checked((int)denseClip.m_CurveCount);
        for (var frameIndex = 0; frameIndex < denseClip.m_FrameCount; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameTime = denseClip.m_BeginTime + frameIndex / denseClip.m_SampleRate;
            var frameValues = new float[samplesPerFrame];
            var sampleOffset = frameIndex * samplesPerFrame;
            for (var sampleIndex = 0; sampleIndex < samplesPerFrame; sampleIndex++)
            {
                if ((sampleIndex & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                frameValues[sampleIndex] = RequireFinite(
                    denseSamples[sampleOffset + sampleIndex],
                    "dense animation sample");
            }
            denseFrames[frameIndex] = new
            {
                time = RequireFinite(frameTime, "dense frame time"),
                values = frameValues
            };
        }

        var constantValues = serializedClip.m_ConstantClip?.data ?? Array.Empty<float>();
        var constantFrameValues = new float[constantValues.Length];
        for (var index = 0; index < constantValues.Length; index++)
        {
            if ((index & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            constantFrameValues[index] = RequireFinite(constantValues[index], "constant animation value");
        }

        var exportData = new
        {
            schemaVersion = 1,
            name = clip.m_Name,
            coordinateSpace = "Unity",
            isLegacy = false,
            animationType = clip.m_AnimationType.ToString(),
            compressed = clip.m_Compressed,
            sampleRate = RequireFinite(clip.m_SampleRate, "sample rate"),
            wrapMode = clip.m_WrapMode,
            bindingPathEncoding = animationPathBindings == null
                ? "Unity CRC path hash; hierarchy names are not resolved"
                : "Unity CRC path hash; resolved paths are included when available",
            objectReferenceCurves = (clip.m_PPtrCurves ?? Array.Empty<PPtrCurve>())
                .Select(curve =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new
                    {
                        path = curve.path,
                        attribute = curve.attribute,
                        classId = curve.classID,
                        keys = curve.curve.Select(key =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            return new
                            {
                                time = RequireFinite(key.time, "object-reference key time"),
                                fileId = key.value.m_FileID,
                                pathId = key.value.m_PathID
                            };
                        })
                    };
                }),
            pointerCurveMapping = (bindingConstant.pptrCurveMapping ?? Array.Empty<PPtr<AssetStudio.Object>>())
                .Select(pointer => new
                {
                    fileId = pointer.m_FileID,
                    pathId = pointer.m_PathID
                }),
            events = (clip.m_Events ?? Array.Empty<AnimationEvent>())
                .Select(animationEvent =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new
                    {
                        time = RequireFinite(animationEvent.time, "event time"),
                        functionName = animationEvent.functionName,
                        data = animationEvent.data,
                        floatParameter = RequireFinite(animationEvent.floatParameter, "event float parameter"),
                        intParameter = animationEvent.intParameter,
                        messageOptions = animationEvent.messageOptions,
                        objectReferenceFileId = animationEvent.objectReferenceParameter.m_FileID,
                        objectReferencePathId = animationEvent.objectReferenceParameter.m_PathID
                    };
                }),
            bindings = bindingRows,
            muscleClip = new
            {
                startTime = RequireFinite(muscleClip.m_StartTime, "clip start time"),
                stopTime = RequireFinite(muscleClip.m_StopTime, "clip stop time"),
                cycleOffset = RequireFinite(muscleClip.m_CycleOffset, "clip cycle offset"),
                orientationOffsetY = RequireFinite(muscleClip.m_OrientationOffsetY, "orientation offset"),
                averageAngularSpeed = RequireFinite(muscleClip.m_AverageAngularSpeed, "average angular speed"),
                averageSpeed = ExportVector(muscleClip.m_AverageSpeed),
                level = RequireFinite(muscleClip.m_Level, "clip level"),
                mirror = muscleClip.m_Mirror,
                loopTime = muscleClip.m_LoopTime,
                loopBlend = muscleClip.m_LoopBlend,
                loopBlendOrientation = muscleClip.m_LoopBlendOrientation,
                loopBlendPositionY = muscleClip.m_LoopBlendPositionY,
                loopBlendPositionXZ = muscleClip.m_LoopBlendPositionXZ,
                startAtOrigin = muscleClip.m_StartAtOrigin,
                keepOriginalOrientation = muscleClip.m_KeepOriginalOrientation,
                keepOriginalPositionY = muscleClip.m_KeepOriginalPositionY,
                keepOriginalPositionXZ = muscleClip.m_KeepOriginalPositionXZ,
                heightFromFeet = muscleClip.m_HeightFromFeet,
                deltaPose = ExportHumanPose(muscleClip.m_DeltaPose, cancellationToken),
                startTransform = ExportXform(muscleClip.m_StartX),
                stopTransform = ExportXform(muscleClip.m_StopX),
                leftFootStartTransform = ExportXform(muscleClip.m_LeftFootStartX),
                rightFootStartTransform = ExportXform(muscleClip.m_RightFootStartX),
                motionStartTransform = ExportXform(muscleClip.m_MotionStartX),
                motionStopTransform = ExportXform(muscleClip.m_MotionStopX),
                indexArray = muscleClip.m_IndexArray ?? Array.Empty<int>(),
                valueDeltas = (muscleClip.m_ValueArrayDelta ?? Array.Empty<ValueDelta>())
                    .Select(delta => new
                    {
                        start = RequireFinite(delta.m_Start, "muscle value start"),
                        stop = RequireFinite(delta.m_Stop, "muscle value stop")
                    }),
                referencePose = (muscleClip.m_ValueArrayReferencePose ?? Array.Empty<float>())
                    .Select(value => RequireFinite(value, "muscle reference-pose value"))
            },
            streamedCurveCount = streamedClip.curveCount,
            streamedFrames,
            denseClip = new
            {
                frameCount = denseClip.m_FrameCount,
                curveCount = denseClip.m_CurveCount,
                baseValueIndex = streamedClip.curveCount,
                sampleRate = RequireFinite(denseClip.m_SampleRate, "dense sample rate"),
                beginTime = RequireFinite(denseClip.m_BeginTime, "dense begin time"),
                frames = denseFrames
            },
            constantValues = new
            {
                baseValueIndex = constantBaseValueIndex,
                values = constantFrameValues
            }
        };

        cancellationToken.ThrowIfCancellationRequested();
        return (
            Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(exportData, Formatting.Indented)),
            ".anim.json");
    }

    private static (object[] Rows, Dictionary<int, object> ByValueIndex) BuildMuscleCurveBindings(
        IReadOnlyList<GenericBinding> bindings,
        IReadOnlyDictionary<uint, string>? animationPathBindings)
    {
        var rows = new List<object>();
        var byValueIndex = new Dictionary<int, object>();
        var valueIndex = 0;
        for (var bindingIndex = 0; bindingIndex < bindings.Count; bindingIndex++)
        {
            var binding = bindings[bindingIndex];
            var componentCount = binding.typeID == ClassIDType.Transform
                ? binding.attribute switch
                {
                    1 or 3 or 4 => 3,
                    2 => 4,
                    _ => 1
                }
                : 1;
            for (var componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                var row = new
                {
                    valueIndex,
                    bindingIndex,
                    pathHash = binding.path,
                    resolvedPath = animationPathBindings != null
                        && animationPathBindings.TryGetValue(binding.path, out var resolvedPath)
                            ? resolvedPath
                            : null,
                    attribute = binding.attribute,
                    typeId = (int)binding.typeID,
                    type = binding.typeID.ToString(),
                    componentIndex,
                    customType = binding.customType,
                    isPointerCurve = binding.isPPtrCurve != 0,
                    isIntegerCurve = binding.isIntCurve != 0,
                    scriptFileId = binding.script?.m_FileID,
                    scriptPathId = binding.script?.m_PathID
                };
                rows.Add(row);
                byValueIndex.Add(valueIndex, row);
                valueIndex++;
            }
        }

        return (rows.ToArray(), byValueIndex);
    }

    private static IReadOnlyDictionary<uint, string> GetAnimationPathBindings(
        Animator animator,
        CancellationToken cancellationToken)
    {
        var pathBindings = new Dictionary<uint, string>();
        if (animator.m_GameObject.TryGet(out var gameObject)
            && gameObject.m_Transform is { } rootTransform)
        {
            var rootPath = GetTransformPathFromParents(rootTransform, cancellationToken);
            var pendingTransforms = new Stack<(Transform Transform, string Path)>();
            var visitedTransforms = new HashSet<Transform>();
            pendingTransforms.Push((rootTransform, rootPath));
            while (pendingTransforms.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (transform, path) = pendingTransforms.Pop();
                if (!visitedTransforms.Add(transform))
                    throw new InvalidDataException(
                        "The Animator transform hierarchy contains a cycle or duplicate child reference.");

                AddTransformPathBindings(path, pathBindings);
                var children = transform.m_Children ?? Array.Empty<PPtr<Transform>>();
                for (var childIndex = children.Length - 1; childIndex >= 0; childIndex--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (children[childIndex].TryGet(out var child)
                        && child.m_GameObject.TryGet(out var childGameObject))
                    {
                        pendingTransforms.Push((child, $"{path}/{childGameObject.m_Name}"));
                    }
                }
            }
        }

        if (animator.m_Avatar.TryGet(out var avatar))
        {
            foreach (var (hash, path) in avatar.m_TOS ?? Array.Empty<KeyValuePair<uint, string>>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                pathBindings.TryAdd(hash, path);
            }
        }

        return pathBindings;
    }

    private static string GetTransformPathFromParents(
        Transform transform,
        CancellationToken cancellationToken)
    {
        var segments = new List<string>();
        var visitedTransforms = new HashSet<Transform>();
        var current = transform;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visitedTransforms.Add(current))
                throw new InvalidDataException("The Animator transform parent hierarchy contains a cycle.");
            if (!current.m_GameObject.TryGet(out var gameObject))
                throw new InvalidDataException("An Animator transform has no resolvable GameObject.");
            segments.Add(gameObject.m_Name);
            if (!current.m_Father.TryGet(out var parent))
                break;
            current = parent;
        }

        segments.Reverse();
        return string.Join("/", segments);
    }

    private static void AddTransformPathBindings(
        string fullPath,
        Dictionary<uint, string> pathBindings)
    {
        var path = fullPath;
        while (true)
        {
            var bytes = Encoding.UTF8.GetBytes(path);
            var crc = new SevenZip.CRC();
            crc.Update(bytes, 0, (uint)bytes.Length);
            pathBindings[crc.GetDigest()] = path;

            var separatorIndex = path.IndexOf("/", StringComparison.Ordinal);
            if (separatorIndex < 0)
                return;
            path = path[(separatorIndex + 1)..];
        }
    }

    private static List<StreamedClip.StreamedFrame> ReadMuscleStreamFrames(
        StreamedClip streamedClip,
        CancellationToken cancellationToken)
    {
        var words = streamedClip.data
            ?? throw new InvalidDataException("This AnimationClip has no streamed-curve bytes.");
        var byteLength = (long)words.Length * sizeof(uint);
        if (byteLength > int.MaxValue)
            throw new InvalidDataException("This AnimationClip's streamed-curve data is too large.");

        var bytes = new byte[(int)byteLength];
        Buffer.BlockCopy(words, 0, bytes, 0, bytes.Length);
        using var reader = new BinaryReader(new MemoryStream(bytes, writable: false));
        var frames = new List<StreamedClip.StreamedFrame>();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameStart = reader.BaseStream.Position;
            if (reader.BaseStream.Length - frameStart < sizeof(float) + sizeof(int))
                throw new InvalidDataException("This AnimationClip has a truncated streamed frame header.");

            _ = reader.ReadSingle();
            var keyCount = reader.ReadInt32();
            var remainingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
            if (keyCount < 0 || (long)keyCount * 20 > remainingBytes)
                throw new InvalidDataException("This AnimationClip has an invalid streamed frame key count.");

            reader.BaseStream.Position = frameStart;
            try
            {
                frames.Add(new StreamedClip.StreamedFrame(reader));
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("This AnimationClip has truncated streamed-frame data.", exception);
            }
        }

        var previousKeys = new Dictionary<int, (StreamedClip.StreamedCurveKey Key, float Time)>();
        for (var frameIndex = 0; frameIndex < Math.Min(2, frames.Count); frameIndex++)
            UpdatePreviousStreamedKeys(frames[frameIndex], previousKeys, cancellationToken);

        for (var frameIndex = 2; frameIndex < frames.Count - 1; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = frames[frameIndex];
            for (var keyIndex = 0; keyIndex < frame.keyList.Length; keyIndex++)
            {
                if ((keyIndex & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var key = frame.keyList[keyIndex];
                if (previousKeys.TryGetValue(key.index, out var previous))
                    key.inSlope = previous.Key.CalculateNextInSlope(frame.time - previous.Time, key);
            }
            UpdatePreviousStreamedKeys(frame, previousKeys, cancellationToken);
        }

        return frames;
    }

    private static void UpdatePreviousStreamedKeys(
        StreamedClip.StreamedFrame frame,
        Dictionary<int, (StreamedClip.StreamedCurveKey Key, float Time)> previousKeys,
        CancellationToken cancellationToken)
    {
        var updatedIndices = new HashSet<int>();
        foreach (var key in frame.keyList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (updatedIndices.Add(key.index))
                previousKeys[key.index] = (key, frame.time);
        }
    }

    private static object? ExportHumanPose(HumanPose? pose, CancellationToken cancellationToken)
    {
        if (pose == null)
            return null;

        return new
        {
            rootTransform = ExportXform(pose.m_RootX),
            lookAtPosition = ExportVector(pose.m_LookAtPosition),
            lookAtWeight = ExportVector4(pose.m_LookAtWeight),
            goals = (pose.m_GoalArray ?? Array.Empty<HumanGoal>())
                .Select(goal =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new
                    {
                        transform = ExportXform(goal.m_X),
                        translationWeight = RequireFinite(goal.m_WeightT, "human goal translation weight"),
                        rotationWeight = RequireFinite(goal.m_WeightR, "human goal rotation weight"),
                        hintTranslation = ExportVector(goal.m_HintT),
                        hintTranslationWeight = RequireFinite(
                            goal.m_HintWeightT,
                            "human goal hint translation weight")
                    };
                }),
            leftHand = ExportHandPose(pose.m_LeftHandPose, cancellationToken),
            rightHand = ExportHandPose(pose.m_RightHandPose, cancellationToken),
            degreesOfFreedom = ExportFiniteValues(pose.m_DoFArray, cancellationToken),
            translationDegreesOfFreedom = (pose.m_TDoFArray ?? Array.Empty<Vector3>())
                .Select(ExportVector)
        };
    }

    private static object? ExportHandPose(HandPose? pose, CancellationToken cancellationToken)
    {
        if (pose == null)
            return null;

        return new
        {
            grabTransform = ExportXform(pose.m_GrabX),
            degreesOfFreedom = ExportFiniteValues(pose.m_DoFArray, cancellationToken),
            overrideWeight = RequireFinite(pose.m_Override, "hand override"),
            closeOpen = RequireFinite(pose.m_CloseOpen, "hand close/open"),
            inOut = RequireFinite(pose.m_InOut, "hand in/out"),
            grab = RequireFinite(pose.m_Grab, "hand grab")
        };
    }

    private static object? ExportXform(xform? value) => value == null
        ? null
        : new
        {
            translation = ExportVector(value.t),
            rotation = ExportQuaternion(value.q),
            scale = ExportVector(value.s)
        };

    private static object ExportVector4(Vector4 value) => new
    {
        x = RequireFinite(value.X, "vector component"),
        y = RequireFinite(value.Y, "vector component"),
        z = RequireFinite(value.Z, "vector component"),
        w = RequireFinite(value.W, "vector component")
    };

    private static float[] ExportFiniteValues(float[]? values, CancellationToken cancellationToken)
    {
        if (values == null)
            return Array.Empty<float>();
        var result = new float[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            if ((index & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            result[index] = RequireFinite(values[index], "pose value");
        }
        return result;
    }

    private static object ExportVector3Curve(Vector3Curve curve, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new
        {
            path = curve.path,
            preInfinity = curve.curve.m_PreInfinity,
            postInfinity = curve.curve.m_PostInfinity,
            rotationOrder = curve.curve.m_RotationOrder,
            keys = curve.curve.m_Curve
                .Select(key => ExportVector3Key(key, cancellationToken))
        };
    }

    private static object ExportVector3Key(Keyframe<Vector3> key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new
        {
            time = RequireFinite(key.time, "vector key time"),
            value = ExportVector(key.value),
            inSlope = ExportVector(key.inSlope),
            outSlope = ExportVector(key.outSlope),
            key.weightedMode,
            inWeight = ExportVector(key.inWeight),
            outWeight = ExportVector(key.outWeight)
        };
    }

    private static object ExportQuaternionKey(Keyframe<Quaternion> key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new
        {
            time = RequireFinite(key.time, "rotation key time"),
            value = ExportQuaternion(key.value),
            inSlope = ExportQuaternion(key.inSlope),
            outSlope = ExportQuaternion(key.outSlope),
            key.weightedMode,
            inWeight = ExportQuaternion(key.inWeight),
            outWeight = ExportQuaternion(key.outWeight)
        };
    }

    private static object ExportFloatKey(Keyframe<float> key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new
        {
            time = RequireFinite(key.time, "float key time"),
            value = RequireFinite(key.value, "float key value"),
            inSlope = RequireFinite(key.inSlope, "float key in-slope"),
            outSlope = RequireFinite(key.outSlope, "float key out-slope"),
            key.weightedMode,
            inWeight = RequireFinite(key.inWeight, "float key in-weight"),
            outWeight = RequireFinite(key.outWeight, "float key out-weight")
        };
    }

    private static object ExportCompressedRotationCurve(
        CompressedAnimationCurve curve,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packedTimes = curve.m_Times.UnpackInts();
        var rotations = curve.m_Values.UnpackQuats();
        if (packedTimes.Length != rotations.Length)
            throw new InvalidDataException("A compressed rotation curve has mismatched time and value counts.");

        var cumulativeTime = 0L;
        var keys = new object[packedTimes.Length];
        for (var index = 0; index < packedTimes.Length; index++)
        {
            if ((index & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            cumulativeTime = checked(cumulativeTime + packedTimes[index]);
            keys[index] = new
            {
                time = RequireFinite(cumulativeTime * 0.01f, "compressed rotation key time"),
                value = ExportQuaternion(rotations[index])
            };
        }

        return new
        {
            path = curve.m_Path,
            preInfinity = curve.m_PreInfinity,
            postInfinity = curve.m_PostInfinity,
            packedTimes = new
            {
                itemCount = curve.m_Times.m_NumItems,
                bitSize = curve.m_Times.m_BitSize,
                dataBase64 = Convert.ToBase64String(curve.m_Times.m_Data)
            },
            packedValues = new
            {
                itemCount = curve.m_Values.m_NumItems,
                dataBase64 = Convert.ToBase64String(curve.m_Values.m_Data)
            },
            packedSlopes = new
            {
                itemCount = curve.m_Slopes.m_NumItems,
                range = RequireFinite(curve.m_Slopes.m_Range, "compressed rotation slope range"),
                start = RequireFinite(curve.m_Slopes.m_Start, "compressed rotation slope start"),
                bitSize = curve.m_Slopes.m_BitSize,
                dataBase64 = Convert.ToBase64String(curve.m_Slopes.m_Data)
            },
            keys
        };
    }

    private static object ExportVector(Vector3 value) => new
    {
        x = RequireFinite(value.X, "vector component"),
        y = RequireFinite(value.Y, "vector component"),
        z = RequireFinite(value.Z, "vector component")
    };

    private static object ExportQuaternion(Quaternion value) => new
    {
        x = RequireFinite(value.X, "quaternion component"),
        y = RequireFinite(value.Y, "quaternion component"),
        z = RequireFinite(value.Z, "quaternion component"),
        w = RequireFinite(value.W, "quaternion component")
    };

    private static float RequireFinite(float value, string fieldName)
    {
        if (!float.IsFinite(value))
            throw new InvalidDataException($"Animation clip contains a non-finite {fieldName}.");
        return value;
    }

    private static (byte[] Data, string Extension) ExportConverted(
        AssetStudio.Object asset,
        string? container = null,
        CancellationToken cancellationToken = default,
        MobileMonoBehaviourAssemblyService? monoBehaviourAssemblyService = null,
        MobileImageFormat imageFormat = MobileImageFormat.Png,
        bool convertTextures = true,
        bool convertAudio = true,
        bool restoreTextAssetExtension = true,
        IReadOnlyDictionary<uint, string>? animationPathBindings = null) =>
        asset switch
        {
            Texture2D texture => convertTextures
                ? ExportTexture(texture, cancellationToken, imageFormat)
                : ExportRawTexture(texture),
            TextAsset text => ExportTextAsset(text, container, restoreTextAssetExtension),
            AudioClip audio => ExportAudio(audio, cancellationToken, convertAudio),
            VideoClip video => ExportVideo(video, cancellationToken),
            MonoBehaviour behaviour => ExportMonoBehaviour(
                behaviour, cancellationToken, monoBehaviourAssemblyService),
            MonoScript script => ExportMonoScript(script),
            AnimationClip animationClip => ExportAnimationClip(
                animationClip, cancellationToken, animationPathBindings),
            Mesh mesh => (Encoding.UTF8.GetBytes(ExportObj(mesh, cancellationToken)), ".obj"),
            AssetStudio.Font font => ExportFont(font),
            MovieTexture movie => ExportMovieTexture(movie),
            Sprite sprite => ExportSprite(sprite, cancellationToken, imageFormat),
            Shader shader => (Encoding.UTF8.GetBytes(shader.Convert()), ".shader"),
            _ => throw new NotSupportedException($"No converted exporter is available for {asset.type}.")
        };

    private static (byte[] Data, string Extension) ExportRawTexture(Texture2D texture)
    {
        var data = texture.image_data?.GetData();
        if (data is not { Length: > 0 })
            throw new InvalidDataException("This texture contains no stored image data.");
        return (data, ".tex");
    }

    private static (byte[] Data, string Extension) ExportTextAsset(
        TextAsset text,
        string? container,
        bool restoreExtensionName)
    {
        var extension = restoreExtensionName ? Path.GetExtension(container ?? string.Empty) : string.Empty;
        if (extension.Length <= 1 || extension.Length > 16
            || extension.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            extension = ".txt";
        }
        return (text.m_Script, extension);
    }

    public static string ExportAssetListXml(IEnumerable<MainPage.AssetListExportRow> rows)
    {
        var document = new XDocument(
            new XElement("Assets",
                rows.Select(row =>
                    new XElement("Asset",
                        new XElement("Name", row.Name),
                        new XElement("Container", row.Container),
                        new XElement("Type", row.Type),
                        new XElement("TypeID", row.TypeId),
                        new XElement("PathID", row.PathId),
                        new XElement("SourceFile", Path.GetFileName(row.SourceFile)),
                        new XElement("Size", row.Size)))));
        Directory.CreateDirectory(FileSystem.CacheDirectory);
        var path = Path.Combine(FileSystem.CacheDirectory, $"AssetStudio_AssetList_{Guid.NewGuid():N}.xml");
        document.Save(path);
        return path;
    }

    private static int OffsetObjReference(string value, int offset)
    {
        var index = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (index <= 0)
            throw new InvalidDataException("A mesh OBJ contains an invalid relative vertex reference.");
        return checked(index + offset);
    }

    public static string ExportClassStructuresXml(IEnumerable<MainPage.ClassRow> classes)
    {
        var document = new XDocument(
            new XElement("AssetStudioClassStructures",
                classes.Select(item =>
                    new XElement("Class",
                        new XAttribute("id", item.Id),
                        new XAttribute("version", item.Version),
                        new XAttribute("name", item.Name),
                        new XElement("Summary", item.Summary),
                        new XElement("Structure", item.StructureText)))));
        Directory.CreateDirectory(FileSystem.CacheDirectory);
        var path = Path.Combine(FileSystem.CacheDirectory, $"AssetStudio_Classes_{Guid.NewGuid():N}.xml");
        document.Save(path);
        return path;
    }

    private static (byte[] Data, string Extension) ExportMonoScript(MonoScript script)
    {
        var data = JsonConvert.SerializeObject(new
        {
            name = script.m_Name,
            className = script.m_ClassName,
            namespaceName = script.m_Namespace,
            assemblyName = script.m_AssemblyName,
            serializedTypeTreeAvailable = script.serializedType?.m_Type != null
        }, Formatting.Indented);
        return (Encoding.UTF8.GetBytes(data), ".json");
    }

    private static (byte[] Data, string Extension) ExportFont(AssetStudio.Font font)
    {
        var data = font.m_FontData;
        if (data is not { Length: > 0 })
            throw new InvalidDataException("This font does not contain embedded font data.");

        var isOpenType = data.Length >= 4
            && data[0] == (byte)'O' && data[1] == (byte)'T'
            && data[2] == (byte)'T' && data[3] == (byte)'O';
        return (data, isOpenType ? ".otf" : ".ttf");
    }

    private static (byte[] Data, string Extension) ExportMonoBehaviour(
        MonoBehaviour behaviour,
        CancellationToken cancellationToken,
        MobileMonoBehaviourAssemblyService? monoBehaviourAssemblyService)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var conversion = monoBehaviourAssemblyService?.Convert(behaviour);
        var typeData = conversion?.Data ?? behaviour.ToType();
        if (typeData is null)
        {
            throw new NotSupportedException(
                conversion?.Error
                ?? "This MonoBehaviour has no serialized type tree. Load the game's managed DLL assemblies to convert it.");
        }

        var data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(typeData, Formatting.Indented));
        cancellationToken.ThrowIfCancellationRequested();
        return (data, ".json");
    }

    private static (byte[] Data, string Extension) ExportTexture(
        Texture2D texture,
        CancellationToken cancellationToken,
        MobileImageFormat imageFormat = MobileImageFormat.Png)
    {
        var bgra = DecodeTexture(texture, cancellationToken);
        return (
            EncodeImage(texture.m_Width, texture.m_Height, bgra, imageFormat, cancellationToken),
            GetImageExtension(imageFormat));
    }

    private static byte[] DecodeTexture(Texture2D texture, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (texture.m_Width <= 0 || texture.m_Height <= 0
            || (long)texture.m_Width * texture.m_Height > MaxTexturePixels)
            throw new InvalidDataException("This texture is too large or has invalid dimensions for decoding on this device.");

        if (texture.m_TextureFormat is TextureFormat.DXT1 or TextureFormat.DXT3 or TextureFormat.DXT5)
        {
            var compressedData = texture.image_data?.GetData()
                ?? throw new InvalidDataException("This compressed texture contains no image data.");
            if (texture.platform == BuildTarget.XBOX360)
                SwapDxtBytesForXbox(compressedData);

            return texture.m_TextureFormat switch
            {
                TextureFormat.DXT1 => ManagedDxtDecoder.DecodeDxt1(
                    compressedData, texture.m_Width, texture.m_Height, cancellationToken),
                TextureFormat.DXT3 => ManagedDxtDecoder.DecodeDxt3(
                    compressedData, texture.m_Width, texture.m_Height, cancellationToken),
                TextureFormat.DXT5 => ManagedDxtDecoder.DecodeDxt5(
                    compressedData, texture.m_Width, texture.m_Height, cancellationToken),
                _ => throw new InvalidOperationException("Unexpected DXT texture format.")
            };
        }

        try
        {
            var bgra = new byte[checked(texture.m_Width * texture.m_Height * 4)];
            if (!new Texture2DConverter(texture).DecodeTexture2D(bgra))
                throw new NotSupportedException($"Texture format {texture.m_TextureFormat} could not be decoded.");
            cancellationToken.ThrowIfCancellationRequested();
            return bgra;
        }
        catch (Exception exception) when (exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException)
        {
            throw new NotSupportedException(
                $"Texture format {texture.m_TextureFormat} requires a native decoder unavailable in this app build.",
                exception);
        }
        catch (TypeInitializationException exception) when (
            exception.InnerException is NotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException
                or BadImageFormatException)
        {
            throw new NotSupportedException(
                $"Texture format {texture.m_TextureFormat} requires a native decoder unavailable in this app build.",
                exception.InnerException);
        }
    }

    private static void SwapDxtBytesForXbox(byte[] data)
    {
        for (var index = 0; index + 1 < data.Length; index += 2)
            (data[index], data[index + 1]) = (data[index + 1], data[index]);
    }

    private static (byte[] Data, string Extension) ExportSprite(
        Sprite sprite,
        CancellationToken cancellationToken,
        MobileImageFormat imageFormat = MobileImageFormat.Png)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Texture2D texture;
        Texture2D? alphaTexture = null;
        Rectf textureRect;
        Vector2 textureRectOffset;
        SpriteSettings settings;
        float downscaleMultiplier;
        if (sprite.m_SpriteAtlas.TryGet(out var atlas)
            && atlas.m_RenderDataMap.TryGetValue(sprite.m_RenderDataKey, out var atlasData)
            && atlasData.texture.TryGet(out texture))
        {
            textureRect = atlasData.textureRect;
            textureRectOffset = atlasData.textureRectOffset;
            atlasData.alphaTexture?.TryGet(out alphaTexture);
            settings = atlasData.settingsRaw;
            downscaleMultiplier = atlasData.downscaleMultiplier;
        }
        else if (sprite.m_RD.texture.TryGet(out texture))
        {
            textureRect = sprite.m_RD.textureRect;
            textureRectOffset = sprite.m_RD.textureRectOffset;
            sprite.m_RD.alphaTexture?.TryGet(out alphaTexture);
            settings = sprite.m_RD.settingsRaw;
            downscaleMultiplier = sprite.m_RD.downscaleMultiplier;
        }
        else
        {
            throw new NotSupportedException("The sprite's source texture or atlas could not be resolved.");
        }

        var source = DecodeTexture(texture, cancellationToken);
        var sourceWidth = texture.m_Width;
        var sourceHeight = texture.m_Height;
        var alphaSource = alphaTexture == null ? null : DecodeTexture(alphaTexture, cancellationToken);
        var alphaWidth = alphaTexture?.m_Width ?? 0;
        var alphaHeight = alphaTexture?.m_Height ?? 0;
        if (downscaleMultiplier > 0 && downscaleMultiplier != 1f)
        {
            if (!float.IsFinite(downscaleMultiplier))
                throw new InvalidDataException("The sprite has an invalid downscale multiplier.");
            sourceWidth = GetDownscaledTextureDimension(sourceWidth, downscaleMultiplier);
            sourceHeight = GetDownscaledTextureDimension(sourceHeight, downscaleMultiplier);
            source = ResizeSpriteTexture(
                source, texture.m_Width, texture.m_Height, sourceWidth, sourceHeight, cancellationToken);
            if (alphaTexture != null && alphaSource != null)
            {
                alphaWidth = GetDownscaledTextureDimension(alphaWidth, downscaleMultiplier);
                alphaHeight = GetDownscaledTextureDimension(alphaHeight, downscaleMultiplier);
                alphaSource = ResizeSpriteTexture(
                    alphaSource,
                    alphaTexture.m_Width,
                    alphaTexture.m_Height,
                    alphaWidth,
                    alphaHeight,
                    cancellationToken);
            }
        }

        var rectLeft = textureRect.x;
        var rectTop = textureRect.y;
        var rectRight = textureRect.x + textureRect.width;
        var rectBottom = textureRect.y + textureRect.height;
        if (!float.IsFinite(rectLeft) || !float.IsFinite(rectTop)
            || !float.IsFinite(rectRight) || !float.IsFinite(rectBottom))
            throw new InvalidDataException("The sprite has invalid texture rectangle values.");
        var left = (int)Math.Clamp(MathF.Floor(rectLeft), 0, sourceWidth);
        var top = (int)Math.Clamp(MathF.Floor(rectTop), 0, sourceHeight);
        var right = (int)Math.Clamp(MathF.Ceiling(rectRight), 0, sourceWidth);
        var bottom = (int)Math.Clamp(MathF.Ceiling(rectBottom), 0, sourceHeight);
        var cropWidth = right - left;
        var cropHeight = bottom - top;
        if (cropWidth <= 0 || cropHeight <= 0)
            throw new InvalidDataException("The sprite has an empty or out-of-range texture rectangle.");

        var rotation = settings.packed == 1 ? settings.packingRotation : SpritePackingRotation.None;
        if (rotation is < SpritePackingRotation.None or > SpritePackingRotation.Rotate90)
            throw new NotSupportedException($"Sprite packing rotation {rotation} is not supported.");
        var outputWidth = rotation == SpritePackingRotation.Rotate90 ? cropHeight : cropWidth;
        var outputHeight = rotation == SpritePackingRotation.Rotate90 ? cropWidth : cropHeight;
        var output = new byte[checked(outputWidth * outputHeight * 4)];
        for (var y = 0; y < outputHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < outputWidth; x++)
            {
                var (sourceX, sourceY) = rotation switch
                {
                    SpritePackingRotation.FlipHorizontal => (cropWidth - x - 1, y),
                    SpritePackingRotation.FlipVertical => (x, cropHeight - y - 1),
                    SpritePackingRotation.Rotate180 => (cropWidth - x - 1, cropHeight - y - 1),
                    SpritePackingRotation.Rotate90 => (cropWidth - y - 1, x),
                    _ => (x, y)
                };
                var sourceOffset = checked(((top + sourceY) * sourceWidth + left + sourceX) * 4);
                var outputOffset = checked((y * outputWidth + x) * 4);
                Buffer.BlockCopy(source, sourceOffset, output, outputOffset, 4);
                if (alphaSource != null)
                {
                    var alphaX = Math.Clamp(
                        (int)((left + sourceX + 0.5) * alphaWidth / sourceWidth),
                        0, alphaWidth - 1);
                    var alphaY = Math.Clamp(
                        (int)((top + sourceY + 0.5) * alphaHeight / sourceHeight),
                        0, alphaHeight - 1);
                    var alphaOffset = checked((alphaY * alphaWidth + alphaX) * 4);
                    var alphaChannel = alphaTexture.m_TextureFormat == TextureFormat.Alpha8
                        ? alphaSource[alphaOffset + 3]
                        : alphaSource[alphaOffset + 2];
                    output[outputOffset + 3] =
                        (byte)(output[outputOffset + 3] * alphaChannel / 255);
                }
            }
        }

        if (settings.packingMode == SpritePackingMode.Tight)
        {
            ApplySpriteMeshMask(sprite, textureRectOffset, rotation, cropWidth, cropHeight,
                output, outputWidth, outputHeight, cancellationToken);
        }

        return (
            EncodeImage(outputWidth, outputHeight, output, imageFormat, cancellationToken),
            GetImageExtension(imageFormat));
    }

    private static int GetDownscaledTextureDimension(int dimension, float multiplier)
    {
        var scaledDimension = dimension / multiplier;
        if (!float.IsFinite(scaledDimension) || scaledDimension < 1 || scaledDimension > int.MaxValue)
            throw new InvalidDataException("The sprite downscale multiplier produces invalid texture dimensions.");
        return (int)scaledDimension;
    }

    private static byte[] ResizeSpriteTexture(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int targetWidth,
        int targetHeight,
        CancellationToken cancellationToken)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0
            || targetWidth <= 0 || targetHeight <= 0
            || (long)sourceWidth * sourceHeight > MaxTexturePixels
            || (long)targetWidth * targetHeight > MaxTexturePixels)
        {
            throw new InvalidDataException("The sprite resize dimensions are invalid or exceed the mobile limit.");
        }

        var sourceLength = checked((long)sourceWidth * sourceHeight * 4);
        var targetLength = checked((long)targetWidth * targetHeight * 4);
        if (source.LongLength != sourceLength)
        {
            throw new InvalidDataException("The sprite resize dimensions are invalid or exceed the mobile limit.");
        }

        using var image = ImageSharpImage.LoadPixelData<Bgra32>(source, sourceWidth, sourceHeight);
        image.Mutate(operation => operation.Resize(targetWidth, targetHeight));
        var resized = new byte[checked((int)targetLength)];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < targetHeight; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * targetWidth * 4;
                for (var x = 0; x < targetWidth; x++)
                {
                    var pixel = row[x];
                    var pixelOffset = rowOffset + x * 4;
                    resized[pixelOffset] = pixel.B;
                    resized[pixelOffset + 1] = pixel.G;
                    resized[pixelOffset + 2] = pixel.R;
                    resized[pixelOffset + 3] = pixel.A;
                }
            }
        });
        return resized;
    }

    private static void ApplySpriteMeshMask(
        Sprite sprite,
        Vector2 textureRectOffset,
        SpritePackingRotation rotation,
        int cropWidth,
        int cropHeight,
        byte[] pixels,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var renderData = sprite.m_RD;
        var triangles = ReadSpriteTriangles(renderData, cancellationToken);
        if (triangles.Count == 0)
            throw new NotSupportedException("The tight-packed sprite has no readable triangles.");

        var covered = new bool[checked(width * height)];
        foreach (var triangle in triangles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p1 = TransformSpritePoint(sprite, textureRectOffset, rotation, cropWidth, cropHeight, triangle.A);
            var p2 = TransformSpritePoint(sprite, textureRectOffset, rotation, cropWidth, cropHeight, triangle.B);
            var p3 = TransformSpritePoint(sprite, textureRectOffset, rotation, cropWidth, cropHeight, triangle.C);
            if (!double.IsFinite(p1.X) || !double.IsFinite(p1.Y)
                || !double.IsFinite(p2.X) || !double.IsFinite(p2.Y)
                || !double.IsFinite(p3.X) || !double.IsFinite(p3.Y))
                throw new InvalidDataException("The sprite mesh contains invalid vertex positions.");
            var area = Edge(p1, p2, p3.X, p3.Y);
            if (!double.IsFinite(area) || Math.Abs(area) < 1e-8)
                continue;

            var rawMinX = Math.Floor(Math.Min(p1.X, Math.Min(p2.X, p3.X)));
            var rawMaxX = Math.Ceiling(Math.Max(p1.X, Math.Max(p2.X, p3.X)));
            var rawMinY = Math.Floor(Math.Min(p1.Y, Math.Min(p2.Y, p3.Y)));
            var rawMaxY = Math.Ceiling(Math.Max(p1.Y, Math.Max(p2.Y, p3.Y)));
            if (rawMaxX < 0 || rawMinX >= width || rawMaxY < 0 || rawMinY >= height)
                continue;
            var minX = ClampPixel(rawMinX, width - 1);
            var maxX = ClampPixel(rawMaxX, width - 1);
            var minY = ClampPixel(rawMinY, height - 1);
            var maxY = ClampPixel(rawMaxY, height - 1);
            for (var y = minY; y <= maxY; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = minX; x <= maxX; x++)
                {
                    var pointX = x + 0.5;
                    var pointY = y + 0.5;
                    var edge1 = Edge(p1, p2, pointX, pointY);
                    var edge2 = Edge(p2, p3, pointX, pointY);
                    var edge3 = Edge(p3, p1, pointX, pointY);
                    if ((area > 0 && edge1 >= 0 && edge2 >= 0 && edge3 >= 0)
                        || (area < 0 && edge1 <= 0 && edge2 <= 0 && edge3 <= 0))
                    {
                        covered[y * width + x] = true;
                    }
                }
            }
        }

        for (var index = 0; index < covered.Length; index++)
        {
            if ((index & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            if (!covered[index])
                pixels[index * 4 + 3] = 0;
        }
    }

    private static List<(Vector3 A, Vector3 B, Vector3 C)> ReadSpriteTriangles(
        SpriteRenderData renderData,
        CancellationToken cancellationToken)
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        if (renderData.vertices is { Length: > 0 } && renderData.indices is { Length: > 0 })
        {
            for (var index = 0; index + 2 < renderData.indices.Length; index += 3)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (renderData.indices[index] >= renderData.vertices.Length
                    || renderData.indices[index + 1] >= renderData.vertices.Length
                    || renderData.indices[index + 2] >= renderData.vertices.Length)
                    throw new InvalidDataException("The sprite index buffer references a missing vertex.");
                triangles.Add((
                    renderData.vertices[renderData.indices[index]].pos,
                    renderData.vertices[renderData.indices[index + 1]].pos,
                    renderData.vertices[renderData.indices[index + 2]].pos));
            }
            return triangles;
        }

        var vertexData = renderData.m_VertexData;
        if (vertexData?.m_Channels is not { Length: > 0 }
            || vertexData.m_Streams is not { Length: > 0 }
            || vertexData.m_DataSize is not { Length: > 0 }
            || renderData.m_SubMeshes is not { Length: > 0 }
            || renderData.m_IndexBuffer is not { Length: > 0 })
        {
            return triangles;
        }

        var channel = vertexData.m_Channels[0];
        if (channel.stream >= vertexData.m_Streams.Length)
            throw new InvalidDataException("The sprite vertex channel references an invalid stream.");
        var stream = vertexData.m_Streams[channel.stream];
        if (stream.stride < channel.offset + 12)
            throw new InvalidDataException("The sprite vertex stream has an invalid stride.");

        foreach (var subMesh in renderData.m_SubMeshes)
        {
            if (subMesh.indexCount % 3 != 0)
                throw new NotSupportedException("The tight sprite mesh is not a triangle list.");
            var firstIndexByte = checked((int)subMesh.firstByte);
            var indexCount = checked((int)subMesh.indexCount);
            if (firstIndexByte < 0 || firstIndexByte > renderData.m_IndexBuffer.Length
                || indexCount > (renderData.m_IndexBuffer.Length - firstIndexByte) / 2)
                throw new InvalidDataException("The sprite index buffer is truncated.");

            for (var index = 0; index < indexCount; index += 3)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var a = ReadSpriteVertex(renderData, stream, channel, subMesh,
                    ReadUInt16(renderData.m_IndexBuffer, firstIndexByte + index * 2));
                var b = ReadSpriteVertex(renderData, stream, channel, subMesh,
                    ReadUInt16(renderData.m_IndexBuffer, firstIndexByte + (index + 1) * 2));
                var c = ReadSpriteVertex(renderData, stream, channel, subMesh,
                    ReadUInt16(renderData.m_IndexBuffer, firstIndexByte + (index + 2) * 2));
                triangles.Add((a, b, c));
                if (triangles.Count > 100_000)
                    throw new NotSupportedException("The sprite mesh exceeds the mobile preview limit.");
            }
        }
        return triangles;
    }

    private static Vector3 ReadSpriteVertex(
        SpriteRenderData renderData,
        StreamInfo stream,
        ChannelInfo channel,
        SubMesh subMesh,
        ushort index)
    {
        if (index < subMesh.firstVertex)
            throw new InvalidDataException("The sprite index buffer references a vertex before its submesh.");
        var vertexIndex = (uint)index - subMesh.firstVertex;
        if (vertexIndex >= subMesh.vertexCount)
            throw new InvalidDataException("The sprite index buffer references a vertex outside its submesh.");
        var positionOffset = (long)stream.offset + (long)subMesh.firstVertex * stream.stride
            + (long)vertexIndex * stream.stride + channel.offset;
        if (positionOffset < 0 || positionOffset + 12 > renderData.m_VertexData.m_DataSize.Length)
            throw new InvalidDataException("The sprite vertex buffer is truncated.");
        var position = (int)positionOffset;
        return new Vector3(
            BitConverter.ToSingle(renderData.m_VertexData.m_DataSize, position),
            BitConverter.ToSingle(renderData.m_VertexData.m_DataSize, position + 4),
            BitConverter.ToSingle(renderData.m_VertexData.m_DataSize, position + 8));
    }

    private static ushort ReadUInt16(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, sizeof(ushort)));

    private static int ClampPixel(double value, int maximum) =>
        (int)Math.Clamp(value, 0, maximum);

    private static (double X, double Y) TransformSpritePoint(
        Sprite sprite, Vector2 textureRectOffset, SpritePackingRotation rotation,
        int cropWidth, int cropHeight, Vector3 position)
    {
        var x = position.X * sprite.m_PixelsToUnits
            + sprite.m_Rect.width * sprite.m_Pivot.X - textureRectOffset.X;
        var y = position.Y * sprite.m_PixelsToUnits
            + sprite.m_Rect.height * sprite.m_Pivot.Y - textureRectOffset.Y;
        // Map mesh points into the same counterclockwise orientation as the pixel crop.
        return rotation switch
        {
            SpritePackingRotation.FlipHorizontal => (cropWidth - x, y),
            SpritePackingRotation.FlipVertical => (x, cropHeight - y),
            SpritePackingRotation.Rotate180 => (cropWidth - x, cropHeight - y),
            SpritePackingRotation.Rotate90 => (y, cropWidth - x),
            _ => (x, y)
        };
    }

    private static double Edge((double X, double Y) a, (double X, double Y) b, double x, double y) =>
        (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);

    private static byte[] EncodeImage(
        int width,
        int height,
        byte[] bgra,
        MobileImageFormat imageFormat,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (imageFormat == MobileImageFormat.Png)
            return EncodePng(width, height, bgra, cancellationToken);
        if (imageFormat is not (MobileImageFormat.Jpeg or MobileImageFormat.Bmp or MobileImageFormat.Tga))
            throw new ArgumentOutOfRangeException(nameof(imageFormat), imageFormat, "Unsupported image export format.");
        if (width <= 0 || height <= 0 || bgra.Length != checked(width * height * 4))
            throw new InvalidDataException("Decoded image dimensions do not match its pixel data.");

        var pixels = new Rgba32[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = (height - y - 1) * width * 4;
            var targetRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var source = sourceRow + x * 4;
                pixels[targetRow + x] = new Rgba32(
                    bgra[source + 2],
                    bgra[source + 1],
                    bgra[source],
                    bgra[source + 3]);
            }
        }

        using var image = ImageSharpImage.LoadPixelData<Rgba32>(pixels, width, height);
        using var output = new MemoryStream();
        switch (imageFormat)
        {
            case MobileImageFormat.Jpeg:
                image.SaveAsJpeg(output);
                break;
            case MobileImageFormat.Bmp:
                image.Save(output, new BmpEncoder
                {
                    BitsPerPixel = BmpBitsPerPixel.Pixel32,
                    SupportTransparency = true
                });
                break;
            case MobileImageFormat.Tga:
                image.Save(output, new TgaEncoder
                {
                    BitsPerPixel = TgaBitsPerPixel.Pixel32,
                    Compression = TgaCompression.None
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(imageFormat), imageFormat, "Unsupported image export format.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static string GetImageExtension(MobileImageFormat imageFormat) => imageFormat switch
    {
        MobileImageFormat.Jpeg => ".jpeg",
        MobileImageFormat.Png => ".png",
        MobileImageFormat.Bmp => ".bmp",
        MobileImageFormat.Tga => ".tga",
        _ => throw new ArgumentOutOfRangeException(nameof(imageFormat), imageFormat, "Unsupported image export format.")
    };

    private static byte[] EncodePng(
        int width,
        int height,
        byte[] bgra,
        CancellationToken cancellationToken = default)
    {
        using var imageData = new MemoryStream();
        using (var zlib = new ZLibStream(imageData, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[checked(width * 4 + 1)];
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                row[0] = 0;
                var sourceOffset = checked((height - y - 1) * width * 4);
                for (var x = 0; x < width; x++)
                {
                    var source = sourceOffset + x * 4;
                    var destination = 1 + x * 4;
                    row[destination] = bgra[source + 2];
                    row[destination + 1] = bgra[source + 1];
                    row[destination + 2] = bgra[source];
                    row[destination + 3] = bgra[source + 3];
                }
                zlib.Write(row);
            }
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;
        header[9] = 6;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", imageData.GetBuffer().AsSpan(0, checked((int)imageData.Length)));
        WritePngChunk(png, "IEND", ReadOnlySpan<byte>.Empty);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lengthBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, checked((uint)data.Length));
        output.Write(lengthBytes);

        Span<byte> typeBytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(type, typeBytes);
        output.Write(typeBytes);
        output.Write(data);

        var crc = 0xFFFFFFFFu;
        crc = UpdateCrc32(crc, typeBytes);
        crc = UpdateCrc32(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(lengthBytes, ~crc);
        output.Write(lengthBytes);
    }

    private static uint UpdateCrc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }
        return crc;
    }

    private static (byte[] Data, string Extension) ExportAudio(
        AudioClip audio,
        CancellationToken cancellationToken,
        bool convertAudio)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = audio.m_AudioData.GetData();
        cancellationToken.ThrowIfCancellationRequested();
        if (data is not { Length: > 0 })
        {
            throw new InvalidDataException("This audio clip contains no embedded audio data.");
        }

        if (convertAudio && TryDecodeKnownAudioContainer(data, out var container))
        {
            return container;
        }

        if (convertAudio
            && audio.version is { Length: > 0 }
            && audio.version[0] >= 5
            && TryDecodeFsb5Audio(data, cancellationToken, out var fsb5))
        {
            return fsb5;
        }

        if (convertAudio && TryDecodeOggVorbisWave(data, cancellationToken, out var ogg))
        {
            return ogg;
        }

        // Unity 5+ clips normally store ADPCM/VAG inside FSB5, but resource-
        // backed clips can expose raw codec blocks. Decode those in managed
        // code instead of falling back to the Windows-only FMOD path.
        if (convertAudio && TryDecodeRawCompressedAudio(audio, data, cancellationToken, out var compressed))
        {
            return compressed;
        }

        if (convertAudio
            && audio.version is { Length: > 0 }
            && audio.version[0] >= 5
            && audio.m_CompressionFormat == AudioCompressionFormat.PCM)
        {
            return (EncodePcmWave(
                data,
                audio.m_Frequency,
                audio.m_Channels,
                audio.m_BitsPerSample,
                cancellationToken), ".wav");
        }

        if (convertAudio
            && audio.version is { Length: > 0 }
            && audio.version[0] >= 5
            && audio.m_CompressionFormat == AudioCompressionFormat.Vorbis)
        {
            return (DecodeVorbisWave(data, cancellationToken), ".wav");
        }

        var extension = audio.version is { Length: > 0 } && audio.version[0] < 5
            ? audio.m_Type switch
            {
                FMODSoundType.ACC => ".m4a",
                FMODSoundType.AIFF => ".aif",
                FMODSoundType.IT => ".it",
                FMODSoundType.MOD => ".mod",
                FMODSoundType.MPEG => ".mp3",
                FMODSoundType.OGGVORBIS or FMODSoundType.VORBIS => ".ogg",
                FMODSoundType.S3M => ".s3m",
                FMODSoundType.WAV => ".wav",
                FMODSoundType.XM => ".xm",
                FMODSoundType.XMA => ".xma",
                FMODSoundType.VAG => ".vag",
                FMODSoundType.AUDIOQUEUE => ".fsb",
                _ => ".AudioClip"
            }
            : audio.m_CompressionFormat switch
            {
                AudioCompressionFormat.AAC => ".m4a",
                AudioCompressionFormat.MP3 => ".mp3",
                AudioCompressionFormat.Vorbis => ".ogg",
                _ => ".fsb"
            };

        return (data, extension);
    }

    private sealed class Fsb5Bank
    {
        public uint Codec;
        public uint Flags;
        public int Channels;
        public int SampleRate;
        public uint SampleCount;
        public byte[] SampleData = Array.Empty<byte>();
        public byte[]? DspCoefficients;
    }

    private static readonly int[] ImaStepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14,
        16, 17, 19, 21, 23, 25, 28, 31,
        34, 37, 41, 45, 50, 55, 60, 66,
        73, 80, 88, 97, 107, 118, 130, 143,
        157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658,
        724, 796, 876, 963, 1060, 1166, 1282, 1411,
        1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
        3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
        32767
    };

    private static readonly int[] ImaIndexTable =
    {
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8
    };

    private static readonly int[] VagCoefficient1 = { 0, 60, 115, 98, 122 };

    private static readonly int[] VagCoefficient2 = { 0, 0, -52, -55, -60 };

    private static bool MatchesAscii(byte[] data, int offset, string value)
    {
        if (offset < 0 || offset + value.Length > data.Length)
            return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (data[offset + index] != (byte)value[index])
                return false;
        }
        return true;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static ulong ReadUInt64(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));

    private static bool TryDecodeKnownAudioContainer(byte[] data, out (byte[] Data, string Extension) result)
    {
        result = default;
        if (MatchesAscii(data, 0, "RIFF") && MatchesAscii(data, 8, "WAVE"))
        {
            result = (data, ".wav");
            return true;
        }
        if (MatchesAscii(data, 0, "FORM")
            && (MatchesAscii(data, 8, "AIFF") || MatchesAscii(data, 8, "AIFC")))
        {
            result = (data, ".aif");
            return true;
        }
        if (MatchesAscii(data, 0, "ID3") || HasMpegAudioSync(data))
        {
            result = (data, ".mp3");
            return true;
        }
        if (HasAdtsAacSync(data))
        {
            result = (data, ".aac");
            return true;
        }
        return false;
    }

    private static bool HasMpegAudioSync(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0xFF)
            return false;

        var second = data[1];
        if ((second & 0xE0) != 0xE0)
            return false;
        if ((second & 0x18) == 0x08)
            return false; // MPEG version 01 is reserved.
        return (second & 0x06) != 0; // Layer 00 is reserved.
    }

    private static bool HasAdtsAacSync(byte[] data)
    {
        if (data.Length < 3 || data[0] != 0xFF || (data[1] & 0xF6) != 0xF0)
            return false;
        var profile = (data[2] >> 6) & 0x03;
        return profile != 0x03;
    }

    private static bool TryDecodeOggVorbisWave(
        byte[] data,
        CancellationToken cancellationToken,
        out (byte[] Data, string Extension) result)
    {
        result = default;
        if (!MatchesAscii(data, 0, "OggS"))
            return false;

        try
        {
            result = (DecodeVorbisWave(data, cancellationToken), ".wav");
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return false;
        }
    }

    private static bool TryDecodeFsb5Audio(
        byte[] data,
        CancellationToken cancellationToken,
        out (byte[] Data, string Extension) result)
    {
        result = default;
        if (!TryReadFsb5Bank(data, out var bank))
            return false;

        try
        {
            switch (bank.Codec)
            {
                case 1: // PCM8
                    result = (DecodeFsb5Pcm(bank, 1, formatTag: 1, cancellationToken), ".wav");
                    return true;
                case 2: // PCM16
                    result = (DecodeFsb5Pcm(bank, 2, formatTag: 1, cancellationToken), ".wav");
                    return true;
                case 3: // PCM24
                    result = (DecodeFsb5Pcm(bank, 3, formatTag: 1, cancellationToken), ".wav");
                    return true;
                case 4: // PCM32
                    result = (DecodeFsb5Pcm(bank, 4, formatTag: 1, cancellationToken), ".wav");
                    return true;
                case 5: // 32-bit IEEE float
                    result = (DecodeFsb5Pcm(bank, 4, formatTag: 3, cancellationToken), ".wav");
                    return true;
                case 6: // Nintendo DSP / GC-ADPCM
                    result = (DecodeGcAdpcmWave(bank, cancellationToken), ".wav");
                    return true;
                case 7: // IMA ADPCM
                    result = (DecodeImaAdpcmWave(bank, cancellationToken), ".wav");
                    return true;
                case 8: // Sony VAG / PS-ADPCM
                    result = (DecodeVagWave(bank, cancellationToken), ".wav");
                    return true;
                case 11: // MPEG frames are already playable MP3 data
                    if (!HasMpegAudioSync(bank.SampleData))
                        return false;
                    result = (bank.SampleData, ".mp3");
                    return true;
                default:
                    return false;
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    // Decode raw Unity 5+ compressed blocks with the same managed codecs used
    // for FSB5 streams. Only layouts with unambiguous frame metadata are
    // attempted; everything else stays exportable as its original data.
    private static bool TryDecodeRawCompressedAudio(
        AudioClip audio,
        byte[] data,
        CancellationToken cancellationToken,
        out (byte[] Data, string Extension) result)
    {
        result = default;
        if (audio.version is not { Length: > 0 } version || version[0] < 5)
            return false;

        var channels = audio.m_Channels;
        var sampleRate = audio.m_Frequency;
        if (channels is < 1 or > 2 || sampleRate <= 0 || sampleRate > 384000)
            return false;

        // Bound the work so a malformed size cannot allocate an oversized PCM buffer.
        const int maxFrames = 250_000;
        Fsb5Bank bank;
        switch (audio.m_CompressionFormat)
        {
            case AudioCompressionFormat.ADPCM:
            {
                const int frameSize = 0x24;
                var blockSize = frameSize * channels;
                if (data.Length < blockSize || data.Length % blockSize != 0)
                    return false;
                var frames = data.Length / blockSize;
                if (frames > maxFrames)
                    return false;
                bank = new Fsb5Bank
                {
                    Codec = 7,
                    Channels = channels,
                    SampleRate = sampleRate,
                    SampleCount = (uint)(frames * 64),
                    SampleData = data
                };
                break;
            }
            case AudioCompressionFormat.PSMVAG:
            {
                const int frameSize = 0x10;
                var blockSize = frameSize * channels;
                if (data.Length < blockSize || data.Length % blockSize != 0)
                    return false;
                var frames = data.Length / blockSize;
                if (frames > maxFrames)
                    return false;
                bank = new Fsb5Bank
                {
                    Codec = 8,
                    Channels = channels,
                    SampleRate = sampleRate,
                    SampleCount = (uint)(frames * 28),
                    SampleData = data
                };
                break;
            }
            default:
                return false;
        }

        try
        {
            var wave = bank.Codec == 7
                ? DecodeImaAdpcmWave(bank, cancellationToken)
                : DecodeVagWave(bank, cancellationToken);
            result = (wave, ".wav");
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException
            or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadFsb5Bank(byte[] data, out Fsb5Bank bank)
    {
        bank = new Fsb5Bank();
        if (data.Length < 0x3C || !MatchesAscii(data, 0, "FSB5"))
            return false;

        var version = ReadUInt32(data, 4);
        if (version > 1)
            return false;

        var headerSize = version == 0 ? 0x40 : 0x3C;
        if (data.Length < headerSize)
            return false;

        var sampleCount = ReadUInt32(data, 0x08);
        var sampleHeadersSize = ReadUInt32(data, 0x0C);
        var nameTableSize = ReadUInt32(data, 0x10);
        var sampleDataSize = ReadUInt32(data, 0x14);
        bank.Codec = ReadUInt32(data, 0x18);
        bank.Flags = ReadUInt32(data, version == 1 ? 0x20 : 0x24);

        if (sampleCount is 0 or > 4096)
            return false;

        var headersEnd = (long)headerSize + sampleHeadersSize;
        var dataStart = headersEnd + nameTableSize;
        if (headersEnd > data.Length || dataStart > data.Length
            || dataStart + sampleDataSize != data.Length)
        {
            return false;
        }

        var dataOffsets = new uint[sampleCount];
        var cursor = headerSize;
        for (var index = 0; index < sampleCount; index++)
        {
            if (cursor + 8 > headersEnd)
                return false;

            var mode = ReadUInt64(data, cursor);
            cursor += 8;

            var sampleCountForSound = (uint)((mode >> 34) & 0x3FFFFFFF);
            var dataOffset = (uint)((mode >> 7) & 0x07FFFFFF) * 32u;
            var frequencyIndex = (int)((mode >> 1) & 0x0F);
            var channelCode = (int)((mode >> 5) & 0x03);
            var channels = channelCode switch
            {
                0 => 1,
                1 => 2,
                2 => 6,
                3 => 8,
                _ => 0
            };

            dataOffsets[index] = dataOffset;
            if (index == 0)
            {
                bank.SampleCount = sampleCountForSound;
                bank.Channels = channels;
                bank.SampleRate = GetFsb5Frequency(frequencyIndex);
            }

            if ((mode & 1) == 0)
                continue;

            while (cursor + 4 <= headersEnd)
            {
                var chunkHeader = ReadUInt32(data, cursor);
                cursor += 4;
                var hasAnotherChunk = (chunkHeader & 1) != 0;
                var chunkSize = (int)((chunkHeader >> 1) & 0x00FFFFFF);
                var chunkType = (int)((chunkHeader >> 25) & 0x7F);
                if (chunkSize < 0 || cursor + chunkSize > headersEnd)
                    return false;

                if (index == 0)
                {
                    switch (chunkType)
                    {
                        case 1: // explicit channel count
                            if (chunkSize >= 1 && data[cursor] is >= 1 and <= 8)
                                bank.Channels = data[cursor];
                            break;
                        case 2: // explicit sample rate
                            if (chunkSize >= 4)
                            {
                                var rate = (int)ReadUInt32(data, cursor);
                                if (rate is > 0 and <= 384000)
                                    bank.SampleRate = rate;
                            }
                            break;
                        case 7: // Nintendo DSP coefficients
                            if (chunkSize >= 0x20)
                                bank.DspCoefficients = data.AsSpan(cursor, chunkSize).ToArray();
                            break;
                    }
                }

                cursor += chunkSize;
                if (!hasAnotherChunk)
                    break;
            }
        }

        if (bank.SampleCount == 0 || bank.Channels is < 1 or > 8 || bank.SampleRate <= 0)
            return false;

        var sampleStart = dataOffsets[0];
        var sampleEnd = sampleCount > 1 ? dataOffsets[1] : sampleDataSize;
        if (sampleEnd <= sampleStart || sampleEnd > sampleDataSize)
            return false;

        var absoluteStart = dataStart + sampleStart;
        if (absoluteStart > data.Length)
            return false;

        bank.SampleData = data.AsSpan((int)absoluteStart, checked((int)(sampleEnd - sampleStart))).ToArray();
        return true;
    }

    private static int GetFsb5Frequency(int index) => index switch
    {
        0 => 4000,
        1 => 8000,
        2 => 11000,
        3 => 11025,
        4 => 16000,
        5 => 22050,
        6 => 24000,
        7 => 32000,
        8 => 44100,
        9 => 48000,
        10 => 96000,
        _ => 0
    };

    private static byte[] DecodeFsb5Pcm(
        Fsb5Bank bank,
        int bytesPerSample,
        int formatTag,
        CancellationToken cancellationToken)
    {
        var pcmLength = checked((int)((long)bank.SampleCount * bank.Channels * bytesPerSample));
        if (pcmLength <= 0 || pcmLength > bank.SampleData.Length)
            throw new InvalidDataException("FSB5 PCM sample data is smaller than its declared format.");

        var pcm = new byte[pcmLength];
        bank.SampleData.AsSpan(0, pcmLength).CopyTo(pcm);
        cancellationToken.ThrowIfCancellationRequested();

        if ((bank.Flags & 0x01) != 0 && bytesPerSample > 1)
        {
            for (var offset = 0; offset < pcmLength; offset += bytesPerSample)
            {
                Array.Reverse(pcm, offset, bytesPerSample);
            }
        }

        return BuildWave(pcm, bank.SampleRate, bank.Channels, bytesPerSample * 8, formatTag);
    }

    private static byte[] DecodeImaAdpcmWave(Fsb5Bank bank, CancellationToken cancellationToken)
    {
        if (bank.Channels is < 1 or > 2)
            throw new InvalidDataException("Only mono and stereo IMA ADPCM clips are supported.");

        const int samplesPerFrame = 0x40;
        var channels = bank.Channels;
        var frameCount = (int)((bank.SampleCount + samplesPerFrame - 1) / samplesPerFrame);
        var pcm = new byte[checked((int)bank.SampleCount * channels * sizeof(short))];
        var frameSize = 0x24 * channels;

        for (var frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frameOffset = checked(frame * frameSize);
            var samplesInFrame = (int)Math.Min(samplesPerFrame, bank.SampleCount - (uint)(frame * samplesPerFrame));

            for (var channel = 0; channel < channels; channel++)
            {
                var headerOffset = frameOffset + 0x04 * channel;
                if (headerOffset + 4 > bank.SampleData.Length)
                    throw new InvalidDataException("IMA ADPCM frame header is outside the sample data.");

                var history = (int)BinaryPrimitives.ReadInt16LittleEndian(bank.SampleData.AsSpan(headerOffset, 2));
                var stepIndex = Math.Clamp((int)(sbyte)bank.SampleData[headerOffset + 2], 0, 88);
                WritePcm16(pcm, ((frame * samplesPerFrame) * channels + channel) * sizeof(short), history);

                for (var sample = 1; sample < samplesInFrame; sample++)
                {
                    var nibbleIndex = sample - 1;
                    var dataOffset = channels == 1
                        ? frameOffset + 0x04 + nibbleIndex / 2
                        : frameOffset + 0x08 + 0x08 * (nibbleIndex / 8) + 0x04 * channel + (nibbleIndex % 8) / 2;
                    if (dataOffset >= bank.SampleData.Length)
                        throw new InvalidDataException("IMA ADPCM sample data ends before the declared sample count.");

                    var shift = (nibbleIndex & 1) == 0 ? 0 : 4;
                    ExpandImaNibble(bank.SampleData[dataOffset], shift, ref history, ref stepIndex);
                    WritePcm16(pcm, ((frame * samplesPerFrame + sample) * channels + channel) * sizeof(short), history);
                }
            }
        }

        return BuildWave(pcm, bank.SampleRate, channels, 16, 1);
    }

    private static void ExpandImaNibble(byte value, int shift, ref int history, ref int stepIndex)
    {
        var code = (value >> shift) & 0x0F;
        var step = ImaStepTable[stepIndex];
        var delta = step >> 3;
        if ((code & 1) != 0)
            delta += step >> 2;
        if ((code & 2) != 0)
            delta += step >> 1;
        if ((code & 4) != 0)
            delta += step;
        if ((code & 8) != 0)
            delta = -delta;

        history = Clamp16(history + delta);
        stepIndex = Math.Clamp(stepIndex + ImaIndexTable[code], 0, 88);
    }

    private static byte[] DecodeGcAdpcmWave(Fsb5Bank bank, CancellationToken cancellationToken)
    {
        if (bank.Channels is < 1 or > 2)
            throw new InvalidDataException("Only mono and stereo DSP ADPCM clips are supported.");
        if (bank.DspCoefficients is null || bank.DspCoefficients.Length < bank.Channels * 0x2E)
            throw new InvalidDataException("DSP ADPCM coefficient metadata is missing.");

        var channels = bank.Channels;
        var coefficients = new short[channels, 16];
        for (var channel = 0; channel < channels; channel++)
        {
            var coefficientOffset = channel * 0x2E;
            for (var index = 0; index < 16; index++)
            {
                coefficients[channel, index] = BinaryPrimitives.ReadInt16BigEndian(
                    bank.DspCoefficients.AsSpan(coefficientOffset + index * 2, 2));
            }
        }

        const int samplesPerFrame = 14;
        var frameCount = (int)((bank.SampleCount + samplesPerFrame - 1) / samplesPerFrame);
        var pcm = new byte[checked((int)bank.SampleCount * channels * sizeof(short))];
        var history1 = new int[channels];
        var history2 = new int[channels];
        var nonInterleaved = (bank.Flags & 0x02) != 0;
        var frameBytes = new byte[channels, 8];

        for (var frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samplesInFrame = (int)Math.Min(samplesPerFrame, bank.SampleCount - (uint)(frame * samplesPerFrame));

            for (var channel = 0; channel < channels; channel++)
            {
                for (var byteIndex = 0; byteIndex < 8; byteIndex++)
                {
                    var dataOffset = nonInterleaved
                        ? channel * (bank.SampleData.Length / channels) + frame * 8 + byteIndex
                        : frame * 8 * channels + (byteIndex / 2) * 2 * channels + (byteIndex % 2) + 2 * channel;
                    if (dataOffset >= bank.SampleData.Length)
                        throw new InvalidDataException("DSP ADPCM sample data ends before the declared sample count.");
                    frameBytes[channel, byteIndex] = bank.SampleData[dataOffset];
                }
            }

            for (var sample = 0; sample < samplesInFrame; sample++)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var header = frameBytes[channel, 0];
                    var scale = 1 << (header & 0x0F);
                    var predictor = Math.Min((header >> 4) & 0x0F, 7);
                    var coefficient1 = coefficients[channel, predictor * 2];
                    var coefficient2 = coefficients[channel, predictor * 2 + 1];
                    var packed = frameBytes[channel, 1 + sample / 2];
                    var nibble = sample % 2 == 0 ? packed >> 4 : packed & 0x0F;
                    if ((nibble & 0x08) != 0)
                        nibble -= 0x10;

                    var decoded = ((nibble * scale) << 11);
                    decoded = (decoded + 1024 + coefficient1 * history1[channel] + coefficient2 * history2[channel]) >> 11;
                    decoded = Clamp16(decoded);
                    history2[channel] = history1[channel];
                    history1[channel] = decoded;
                    WritePcm16(pcm, ((frame * samplesPerFrame + sample) * channels + channel) * sizeof(short), decoded);
                }
            }
        }

        return BuildWave(pcm, bank.SampleRate, channels, 16, 1);
    }

    private static byte[] DecodeVagWave(Fsb5Bank bank, CancellationToken cancellationToken)
    {
        const int bytesPerFrame = 0x10;
        const int samplesPerFrame = (bytesPerFrame - 2) * 2;
        var channels = bank.Channels;
        var nonInterleaved = (bank.Flags & 0x02) != 0;
        var channelStreamSize = bank.SampleData.Length / channels;

        if (nonInterleaved && (channelStreamSize < bytesPerFrame || channelStreamSize % bytesPerFrame != 0))
            throw new InvalidDataException("FSB5 VAG stream size is inconsistent with its channel count.");

        var frameCount = (int)((bank.SampleCount + samplesPerFrame - 1) / samplesPerFrame);
        var pcm = new byte[checked((int)bank.SampleCount * channels * sizeof(short))];
        var history1 = new int[channels];
        var history2 = new int[channels];

        for (var frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samplesInFrame = (int)Math.Min(samplesPerFrame, bank.SampleCount - (uint)(frame * samplesPerFrame));

            for (var channel = 0; channel < channels; channel++)
            {
                var frameOffset = nonInterleaved
                    ? channel * channelStreamSize + frame * bytesPerFrame
                    : frame * bytesPerFrame * channels + channel * bytesPerFrame;
                if (frameOffset < 0 || frameOffset + bytesPerFrame > bank.SampleData.Length)
                    throw new InvalidDataException("VAG frame is outside the sample data.");

                var header = bank.SampleData[frameOffset];
                var flag = bank.SampleData[frameOffset + 1] & 0x07;
                var predictor = header >> 4;
                if (predictor > 4)
                    predictor = 0;
                var shift = header & 0x0F;
                if (shift > 12)
                    shift = 9;
                var scale = 20 - shift;
                var coefficient1 = VagCoefficient1[predictor];
                var coefficient2 = VagCoefficient2[predictor];

                for (var sample = 0; sample < samplesInFrame; sample++)
                {
                    var packed = bank.SampleData[frameOffset + 2 + sample / 2];
                    var nibble = (sample & 1) == 0 ? packed & 0x0F : packed >> 4;
                    var decoded = flag >= 7
                        ? 0
                        : (SignExtend4(nibble) << scale)
                          + ((coefficient1 * history1[channel] + coefficient2 * history2[channel]) >> 6);

                    decoded = Clamp16(decoded);
                    history2[channel] = history1[channel];
                    history1[channel] = decoded;
                    WritePcm16(
                        pcm,
                        ((frame * samplesPerFrame + sample) * channels + channel) * sizeof(short),
                        decoded);
                }
            }
        }

        return BuildWave(pcm, bank.SampleRate, channels, 16, 1);
    }

    private static int SignExtend4(int value) => ((value & 0x0F) ^ 0x08) - 0x08;

    private static void WritePcm16(byte[] target, int offset, int sample)
    {
        BinaryPrimitives.WriteInt16LittleEndian(target.AsSpan(offset, 2), (short)Clamp16(sample));
    }

    private static int Clamp16(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);

    private static byte[] BuildWave(byte[] pcm, int sampleRate, int channels, int bitsPerSample, int formatTag)
    {
        var header = CreateWaveHeader(pcm.Length, sampleRate, channels, bitsPerSample, formatTag);
        var wave = new byte[header.Length + pcm.Length];
        header.CopyTo(wave, 0);
        pcm.CopyTo(wave, header.Length);
        return wave;
    }

    private static byte[] DecodeVorbisWave(byte[] encodedAudio, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(encodedAudio, writable: false);
        using var decoder = new NVorbis.VorbisReader(input, closeOnDispose: false);
        var sampleRate = decoder.SampleRate;
        var channels = decoder.Channels;
        if (sampleRate <= 0 || channels <= 0 || channels > ushort.MaxValue)
            throw new InvalidDataException("Vorbis audio has invalid sample-rate or channel metadata.");

        using var output = new MemoryStream();
        output.Write(new byte[44]);
        var samples = new float[8192];
        var pcmBytes = new byte[samples.Length * sizeof(short)];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sampleCount = decoder.ReadSamples(samples, 0, samples.Length);
            if (sampleCount == 0)
                break;

            for (var index = 0; index < sampleCount; index++)
            {
                var sample = samples[index];
                if (!float.IsFinite(sample))
                    throw new InvalidDataException("Vorbis decoder produced a non-finite audio sample.");
                sample = Math.Clamp(sample, -1f, 1f);
                var pcmSample = sample <= -1f
                    ? short.MinValue
                    : (short)Math.Round(sample * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(
                    pcmBytes.AsSpan(index * sizeof(short), sizeof(short)),
                    pcmSample);
            }
            output.Write(pcmBytes.AsSpan(0, sampleCount * sizeof(short)));
        }

        var pcmLength = checked((int)(output.Length - 44));
        if (pcmLength == 0)
            throw new InvalidDataException("Vorbis audio contains no decoded samples.");

        if (!output.TryGetBuffer(out var outputBuffer) || outputBuffer.Array == null)
            throw new InvalidOperationException("Could not access the decoded Vorbis audio buffer.");
        var header = CreatePcmWaveHeader(pcmLength, sampleRate, channels, 16);
        header.CopyTo(outputBuffer.Array, outputBuffer.Offset);
        return output.ToArray();
    }

    private static byte[] EncodePcmWave(
        byte[] pcm,
        int sampleRate,
        int channels,
        int bitsPerSample,
        CancellationToken cancellationToken)
    {
        if (sampleRate <= 0 || channels <= 0 || channels > ushort.MaxValue
            || bitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new InvalidDataException("PCM audio has invalid sample-rate, channel, or bit-depth metadata.");
        }

        var blockAlign = checked(channels * (bitsPerSample / 8));
        var byteRate = (long)sampleRate * blockAlign;
        if (blockAlign > ushort.MaxValue || byteRate > uint.MaxValue
            || pcm.Length % blockAlign != 0
            || pcm.Length > int.MaxValue - 44)
        {
            throw new InvalidDataException("PCM audio data is inconsistent with its format metadata.");
        }

        var header = CreatePcmWaveHeader(pcm.Length, sampleRate, channels, bitsPerSample);
        var wave = new byte[44 + pcm.Length];
        header.CopyTo(wave, 0);
        const int chunkSize = 1024 * 1024;
        for (var offset = 0; offset < pcm.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, pcm.Length - offset);
            Buffer.BlockCopy(pcm, offset, wave, 44 + offset, length);
        }
        return wave;
    }

    private static byte[] CreatePcmWaveHeader(
        int pcmLength,
        int sampleRate,
        int channels,
        int bitsPerSample)
    {
        if (sampleRate <= 0 || channels <= 0 || channels > ushort.MaxValue
            || bitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new InvalidDataException("PCM audio has invalid sample-rate, channel, or bit-depth metadata.");
        }

        var blockAlign = checked(channels * (bitsPerSample / 8));
        var byteRate = (long)sampleRate * blockAlign;
        if (blockAlign > ushort.MaxValue || byteRate > uint.MaxValue
            || pcmLength < 0 || pcmLength > int.MaxValue - 44
            || pcmLength % blockAlign != 0)
        {
            throw new InvalidDataException("PCM audio data is inconsistent with its format metadata.");
        }

        return CreateWaveHeader(pcmLength, sampleRate, channels, bitsPerSample, formatTag: 1);
    }

    private static byte[] CreateWaveHeader(
        int pcmLength,
        int sampleRate,
        int channels,
        int bitsPerSample,
        int formatTag)
    {
        if (sampleRate <= 0 || channels <= 0 || channels > ushort.MaxValue
            || bitsPerSample is not (8 or 16 or 24 or 32) || formatTag is not (1 or 3))
        {
            throw new InvalidDataException("PCM audio has invalid sample-rate, channel, or bit-depth metadata.");
        }

        var blockAlign = checked(channels * (bitsPerSample / 8));
        var byteRate = (long)sampleRate * blockAlign;
        if (blockAlign > ushort.MaxValue || byteRate > uint.MaxValue
            || pcmLength < 0 || pcmLength > int.MaxValue - 44
            || pcmLength % blockAlign != 0)
        {
            throw new InvalidDataException("PCM audio data is inconsistent with its format metadata.");
        }

        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), checked((uint)(36 + pcmLength)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), checked((ushort)formatTag));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), checked((ushort)channels));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28, 4), checked((uint)byteRate));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32, 2), checked((ushort)blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34, 2), checked((ushort)bitsPerSample));
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), checked((uint)pcmLength));
        return header;
    }

    private static (byte[] Data, string Extension) ExportVideo(
        VideoClip video,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = video.m_VideoData.GetData();
        cancellationToken.ThrowIfCancellationRequested();
        if (data is not { Length: > 0 })
            throw new InvalidDataException("This video clip contains no embedded video data.");

        return (data, GetVideoExtension(video.m_OriginalPath, data));
    }

    private static string GetVideoExtension(string? originalPath, byte[] data)
    {
        var extension = Path.GetExtension(originalPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(extension) && extension.Length <= 10
            && extension.All(character => char.IsAsciiLetterOrDigit(character) || character == '.'))
        {
            return extension.ToLowerInvariant();
        }

        var bytes = data.AsSpan();
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = bytes.Slice(8, 4);
            if (brand.SequenceEqual("qt  "u8))
                return ".mov";
            if (brand.SequenceEqual("M4V "u8))
                return ".m4v";
            return ".mp4";
        }
        if (bytes.StartsWith("OggS"u8))
            return ".ogv";
        if (bytes.Length >= 4
            && bytes[0] == 0x1A && bytes[1] == 0x45
            && bytes[2] == 0xDF && bytes[3] == 0xA3)
        {
            return bytes.IndexOf("webm"u8) >= 0 ? ".webm" : ".mkv";
        }
        if (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("AVI "u8))
            return ".avi";
        if (bytes.Length >= 4
            && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1
            && bytes[3] is 0xBA or 0xB3)
        {
            return ".mpg";
        }

        return ".video";
    }

    private static (byte[] Data, string Extension) ExportMovieTexture(MovieTexture movie)
    {
        if (movie.m_MovieData is not { Length: > 0 })
            throw new InvalidDataException("This movie texture contains no embedded video data.");
        return (movie.m_MovieData, ".ogv");
    }

    private static string ExportObj(Mesh mesh, CancellationToken cancellationToken = default) =>
        ExportObj(mesh, NumericsMatrix4x4.Identity, cancellationToken);

    private static string ExportObj(
        Mesh mesh,
        NumericsMatrix4x4 worldTransform,
        CancellationToken cancellationToken,
        IReadOnlyList<Material?>? submeshMaterials = null,
        IReadOnlyDictionary<Material, ObjMaterialInfo>? materialMap = null,
        NumericsMatrix4x4[]? skinTransforms = null,
        NumericsMatrix4x4[]? skinNormalTransforms = null,
        NumericsVector3[]? blendShapePositionDeltas = null,
        NumericsVector3[]? blendShapeNormalDeltas = null)
    {
        var positions = mesh.m_Vertices;
        if (positions is null || mesh.m_VertexCount <= 0
            || positions.Length < (long)mesh.m_VertexCount * 3)
        {
            throw new NotSupportedException("This mesh does not contain decoded vertex positions.");
        }

        var positionStride = positions.Length / mesh.m_VertexCount;
        if (positionStride is not (3 or 4))
        {
            throw new NotSupportedException("This mesh uses an unsupported vertex layout.");
        }

        var colors = mesh.m_Colors ?? Array.Empty<float>();
        var colorStride = 0;
        if (colors.Length > 0)
        {
            if (colors.Length % mesh.m_VertexCount != 0
                || colors.Length / mesh.m_VertexCount is not (3 or 4))
            {
                throw new InvalidDataException("This mesh contains an unsupported vertex-color layout.");
            }
            colorStride = colors.Length / mesh.m_VertexCount;
        }

        var uvs = mesh.GetUV(0) ?? Array.Empty<float>();
        var uvStride = uvs.Length == 0 ? 0 : uvs.Length / mesh.m_VertexCount;
        var normals = mesh.m_Normals ?? Array.Empty<float>();
        var normalStride = normals.Length == 0 ? 0 : normals.Length / mesh.m_VertexCount;
        var hasNormalTransform = NumericsMatrix4x4.Invert(worldTransform, out var inverseTransform);
        if (!hasNormalTransform)
            normalStride = 0;
        var normalTransform = hasNormalTransform
            ? NumericsMatrix4x4.Transpose(inverseTransform)
            : NumericsMatrix4x4.Identity;
        var determinant = worldTransform.GetDeterminant();
        if (!float.IsFinite(determinant))
            throw new InvalidDataException("The scene contains an invalid world transform.");
        var reverseFaceWinding = determinant >= 0;
        var builder = new StringBuilder(mesh.m_VertexCount * 48);
        builder.AppendLine("# Exported by AssetStudio Mobile");
        if (materialMap is { Count: > 0 })
            builder.AppendLine("mtllib materials.mtl");
        var hasFaces = false;

        for (var i = 0; i < mesh.m_VertexCount; i++)
        {
            if ((i & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var sourcePosition = new NumericsVector3(
                -positions[i * positionStride],
                positions[i * positionStride + 1],
                positions[i * positionStride + 2]);
            if (blendShapePositionDeltas != null)
                sourcePosition += blendShapePositionDeltas[i];
            var position = TransformVertexPosition(
                mesh,
                i,
                sourcePosition,
                worldTransform,
                skinTransforms);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new InvalidDataException("The scene transform produced a non-finite vertex.");
            builder.Append("v ")
                .Append(position.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(position.Y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(position.Z.ToString("R", CultureInfo.InvariantCulture));
            for (var channel = 0; channel < colorStride; channel++)
            {
                var color = colors[i * colorStride + channel];
                if (!float.IsFinite(color))
                    throw new InvalidDataException("The mesh contains a non-finite vertex color.");
                builder.Append(' ').Append(color.ToString("R", CultureInfo.InvariantCulture));
            }
            builder.AppendLine();
        }

        if (uvStride >= 2)
        {
            for (var i = 0; i < mesh.m_VertexCount; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                builder.Append("vt ")
                    .Append(uvs[i * uvStride].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append((1f - uvs[i * uvStride + 1]).ToString("R", CultureInfo.InvariantCulture)).AppendLine();
            }
        }

        if (normalStride >= 3)
        {
            for (var i = 0; i < mesh.m_VertexCount; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var sourceNormal = new NumericsVector3(
                    -normals[i * normalStride],
                    normals[i * normalStride + 1],
                    normals[i * normalStride + 2]);
                if (blendShapeNormalDeltas != null)
                    sourceNormal += blendShapeNormalDeltas[i];
                var normal = TransformVertexNormal(
                    mesh,
                    i,
                    sourceNormal,
                    normalTransform,
                    skinNormalTransforms);
                var lengthSquared = normal.LengthSquared();
                if (!float.IsFinite(lengthSquared))
                    throw new InvalidDataException("The scene transform produced a non-finite normal.");
                if (lengthSquared > 1e-12f)
                    normal = NumericsVector3.Normalize(normal);
                builder.Append("vn ")
                    .Append(normal.X.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(normal.Y.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(normal.Z.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
            }
        }

        var firstIndex = 0;
        var submeshIndex = 0;
        foreach (var submesh in mesh.m_SubMeshes)
        {
            if (submesh.indexCount > int.MaxValue)
                throw new InvalidDataException("The mesh contains an oversized triangle index range.");
            var submeshIndexCount = (int)submesh.indexCount;
            if (firstIndex > mesh.m_Indices.Count
                || submeshIndexCount > mesh.m_Indices.Count - firstIndex
                || submeshIndexCount % 3 != 0)
            {
                throw new InvalidDataException("The mesh contains an invalid triangle index range.");
            }

            if (materialMap is { Count: > 0 } && submeshMaterials != null)
            {
                var materialName = submeshIndex < submeshMaterials.Count
                    && submeshMaterials[submeshIndex] is { } material
                    && materialMap.TryGetValue(material, out var materialInfo)
                        ? materialInfo.Name
                        : "AssetStudio_Default";
                builder.Append("usemtl ").AppendLine(materialName);
            }

            var endIndex = checked(firstIndex + submeshIndexCount);
            for (var indexOffset = firstIndex; indexOffset < endIndex; indexOffset += 3)
            {
                if ((indexOffset & 3071) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var index0 = checked((int)mesh.m_Indices[indexOffset]);
                var index1 = checked((int)mesh.m_Indices[indexOffset + 1]);
                var index2 = checked((int)mesh.m_Indices[indexOffset + 2]);
                var indices = reverseFaceWinding
                    ? new[] { index2, index1, index0 }
                    : new[] { index0, index1, index2 };
                builder.Append("f");
                foreach (var index in indices)
                {
                    if ((uint)index >= (uint)mesh.m_VertexCount)
                    {
                        throw new InvalidDataException("The mesh contains an out-of-range triangle index.");
                    }

                    var objIndex = index + 1;
                    builder.Append(' ').Append(objIndex);
                    if (uvStride >= 2 && normalStride >= 3)
                    {
                        builder.Append('/').Append(objIndex).Append('/').Append(objIndex);
                    }
                    else if (uvStride >= 2)
                    {
                        builder.Append('/').Append(objIndex);
                    }
                    else if (normalStride >= 3)
                    {
                        builder.Append("//").Append(objIndex);
                    }
                }
                builder.AppendLine();
                hasFaces = true;
            }
            firstIndex = endIndex;
            submeshIndex++;
        }

        if (!hasFaces)
        {
            throw new NotSupportedException("This mesh has no triangle faces to export as OBJ.");
        }
        return builder.ToString();
    }
}

internal enum MobileExportKind
{
    Converted,
    Raw,
    Dump
}

internal enum MobileImageFormat
{
    Jpeg,
    Png,
    Bmp,
    Tga
}

internal enum MobileArchiveGrouping
{
    TypeName,
    ContainerPath,
    SourceFileName,
    None
}

internal sealed record MobileExportItem(
    AssetStudio.Object Asset,
    string? Container,
    string? SourceFile = null,
    AssetStudio.Animator? AnimationContext = null);

internal sealed record MobileSingleExportResult(string FilePath, bool UsedRawFallback, string? Warning);

internal sealed record MobileArchiveExportResult(string ArchivePath, int ExportedCount, int RawFallbackCount);

internal sealed record MobileArchiveReportEntry(
    string File,
    string Name,
    string Type,
    long PathId,
    bool RawFallback,
    string? Warning);

internal sealed record ExportPayload(byte[] Data, string Extension, bool UsedRawFallback, string? Warning);

internal sealed record ObjMaterialInfo(string Name);

internal sealed record ObjTextureEntry(string Name, byte[] Data);

internal sealed record ObjMaterialBundle(
    Dictionary<AssetStudio.Material, ObjMaterialInfo> Materials,
    string MaterialLibrary,
    List<ObjTextureEntry> Textures);
