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
                for (var index = 0; index < sourceItems.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = sourceItems[index]
                        ?? throw new InvalidDataException($"Asset {index + 1} is null.");
                    var asset = item.Asset
                        ?? throw new InvalidDataException($"Asset {index + 1} has no parsed object.");
                    var payload = CreateExportPayload(
                        asset, kind, item.Container, cancellationToken,
                        monoBehaviourAssemblyService, imageFormat,
                        convertTextures, convertAudio, restoreTextAssetExtension);
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

    public static string ExportMergedSceneObjects(
        IEnumerable<GameObject> gameObjects,
        CancellationToken cancellationToken = default)
    {
        var sceneMeshes = GetSceneMeshGeometries(gameObjects, cancellationToken);
        if (sceneMeshes.Count == 0)
            throw new InvalidOperationException("The selected scene objects contain no exportable meshes.");

        return WriteSceneObjArchive(sceneMeshes, mergeObjects: true, cancellationToken);
    }

    public static string ExportSceneObjectsArchive(
        IEnumerable<GameObject> gameObjects,
        CancellationToken cancellationToken = default)
    {
        var sceneMeshes = GetSceneMeshGeometries(gameObjects, cancellationToken);
        if (sceneMeshes.Count == 0)
            throw new InvalidOperationException("The selected scene objects contain no exportable meshes.");

        return WriteSceneObjArchive(sceneMeshes, mergeObjects: false, cancellationToken);
    }

    private static string WriteSceneObjArchive(
        IReadOnlyList<SceneMeshGeometry> sceneMeshes,
        bool mergeObjects,
        CancellationToken cancellationToken)
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
        bool restoreTextAssetExtension)
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
                    convertTextures, convertAudio, restoreTextAssetExtension)
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

    private static (byte[] Data, string Extension) ExportConverted(
        AssetStudio.Object asset,
        string? container = null,
        CancellationToken cancellationToken = default,
        MobileMonoBehaviourAssemblyService? monoBehaviourAssemblyService = null,
        MobileImageFormat imageFormat = MobileImageFormat.Png,
        bool convertTextures = true,
        bool convertAudio = true,
        bool restoreTextAssetExtension = true) =>
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

        var wave = new byte[44 + pcm.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4, 4), checked((uint)(36 + pcm.Length)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22, 2), checked((ushort)channels));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24, 4), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28, 4), checked((uint)byteRate));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32, 2), checked((ushort)blockAlign));
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34, 2), checked((ushort)bitsPerSample));
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40, 4), checked((uint)pcm.Length));
        const int chunkSize = 1024 * 1024;
        for (var offset = 0; offset < pcm.Length; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(chunkSize, pcm.Length - offset);
            Buffer.BlockCopy(pcm, offset, wave, 44 + offset, length);
        }
        return wave;
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
    string? SourceFile = null);

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
