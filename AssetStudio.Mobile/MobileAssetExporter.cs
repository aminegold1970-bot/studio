using System.Buffers.Binary;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using AssetStudio;
using Newtonsoft.Json;
using System.Xml.Linq;

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

    public static MobileSingleExportResult ExportForShare(AssetStudio.Object asset, MobileExportKind kind)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var payload = CreateExportPayload(asset, kind);
        return new MobileSingleExportResult(
            WriteAssetFile(asset, payload.Data, payload.Extension),
            payload.UsedRawFallback,
            payload.Warning);
    }

    public static string ExportRaw(AssetStudio.Object asset) =>
        WriteAssetFile(asset, asset.GetRawData(), ".dat");

    public static string ExportDump(AssetStudio.Object asset)
    {
        var (data, extension) = GetDumpData(asset);
        return WriteAssetFile(asset, data, extension);
    }

    public static MobileArchiveExportResult ExportArchive(
        IEnumerable<AssetStudio.Object> assets,
        MobileExportKind kind,
        IProgress<MainPage.AssetLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var sourceAssets = assets.ToList();
        if (sourceAssets.Count == 0)
            throw new InvalidOperationException("There are no assets to add to the export archive.");

        var archivePath = Path.Combine(FileSystem.CacheDirectory, $"AssetStudio_{Guid.NewGuid():N}.zip");
        try
        {
            var report = new List<MobileArchiveReportEntry>(sourceAssets.Count);
            using (var archive = new ZipArchive(File.Create(archivePath), ZipArchiveMode.Create))
            {
                for (var index = 0; index < sourceAssets.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var asset = sourceAssets[index]
                        ?? throw new InvalidDataException($"Asset {index + 1} is null.");
                    var payload = CreateExportPayload(asset, kind);
                    var entryName = $"{index + 1:D5}_{GetArchiveAssetName(asset)}{payload.Extension}";
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
                        (index + 1) * 100 / sourceAssets.Count,
                        $"Exported {index + 1} of {sourceAssets.Count} assets"));
                }

                cancellationToken.ThrowIfCancellationRequested();
                var reportEntry = archive.CreateEntry("AssetStudio_Export_Report.json", CompressionLevel.Fastest);
                using var reportStream = reportEntry.Open();
                using var writer = new StreamWriter(reportStream, new UTF8Encoding(false));
                writer.Write(JsonConvert.SerializeObject(report, Formatting.Indented));
            }

            return new MobileArchiveExportResult(
                archivePath,
                sourceAssets.Count,
                report.Count(entry => entry.RawFallback));
        }
        catch
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            throw;
        }
    }

    public static string ExportMergedMeshes(IEnumerable<Mesh> sourceMeshes)
    {
        var meshes = sourceMeshes?.ToList() ?? new List<Mesh>();
        if (meshes.Count == 0)
            throw new InvalidOperationException("No meshes were selected for model export.");

        var builder = new StringBuilder();
        builder.AppendLine("# Merged by AssetStudio Mobile");
        var vertexOffset = 0;
        var uvOffset = 0;
        var normalOffset = 0;
        var groupIndex = 0;
        foreach (var mesh in meshes)
        {
            var vertices = 0;
            var uvs = 0;
            var normals = 0;
            var obj = ExportObj(mesh);
            builder.Append("g mesh_").Append(groupIndex++).AppendLine();
            foreach (var rawLine in obj.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
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
                else if (line.StartsWith("g ", StringComparison.Ordinal))
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

        return WriteOutputFile("MergedMeshes", Encoding.UTF8.GetBytes(builder.ToString()), ".obj");
    }

    private static string WriteAssetFile(AssetStudio.Object asset, byte[] data, string extension)
    {
        return WriteOutputFile($"{GetAssetName(asset)}_{asset.m_PathID}", data, extension);
    }

    private static string WriteOutputFile(string name, byte[] data, string extension)
    {
        var safeName = SanitizeFileName(name);
        Directory.CreateDirectory(FileSystem.CacheDirectory);
        var path = Path.Combine(FileSystem.CacheDirectory,
            $"{safeName}_{Guid.NewGuid():N}{extension}");
        try
        {
            File.WriteAllBytes(path, data);
            return path;
        }
        catch
        {
            if (File.Exists(path))
                File.Delete(path);
            throw;
        }
    }

    private static string GetAssetName(AssetStudio.Object asset) =>
        asset is NamedObject named && !string.IsNullOrWhiteSpace(named.m_Name)
            ? named.m_Name
            : asset.type.ToString();

    private static string GetArchiveAssetName(AssetStudio.Object asset) =>
        SanitizeFileName($"{GetAssetName(asset)}_{asset.m_PathID}");

    private static string SanitizeFileName(string name)
    {
        var safeName = string.Concat(name.Select(character =>
            char.IsControl(character) || "<>:\"/\\|?*".Contains(character) ? '_' : character));
        return string.IsNullOrWhiteSpace(safeName) ? "AssetStudioExport" : safeName;
    }

    private static ExportPayload CreateExportPayload(AssetStudio.Object asset, MobileExportKind kind)
    {
        try
        {
            var (data, extension) = kind switch
            {
                MobileExportKind.Raw => (asset.GetRawData(), ".dat"),
                MobileExportKind.Dump => GetDumpData(asset),
                _ => ExportConverted(asset)
            };
            return new ExportPayload(data, extension, false, null);
        }
        catch (Exception exception) when (
            kind != MobileExportKind.Raw
            && exception is (NotSupportedException or InvalidDataException))
        {
            var requestedFormat = kind == MobileExportKind.Dump ? "Dump" : "Converted";
            return new ExportPayload(
                asset.GetRawData(),
                ".dat",
                true,
                $"{requestedFormat} export unavailable for {asset.type}: {exception.Message}");
        }
    }

    private static (byte[] Data, string Extension) GetDumpData(AssetStudio.Object asset)
    {
        var dump = asset.Dump();
        if (dump is null)
            throw new NotSupportedException($"Asset type {asset.type} does not provide a text dump.");
        return (Encoding.UTF8.GetBytes(dump), ".txt");
    }

    private static (byte[] Data, string Extension) ExportConverted(AssetStudio.Object asset) =>
        asset switch
        {
            Texture2D texture => ExportTexture(texture),
            TextAsset text => (text.m_Script, ".txt"),
            AudioClip audio => ExportAudio(audio),
            VideoClip video => ExportVideo(video),
            MonoBehaviour behaviour => ExportMonoBehaviour(behaviour),
            Mesh mesh => (Encoding.UTF8.GetBytes(ExportObj(mesh)), ".obj"),
            AssetStudio.Font font => ExportFont(font),
            MovieTexture movie => ExportMovieTexture(movie),
            Sprite sprite => ExportSprite(sprite),
            Shader shader => (Encoding.UTF8.GetBytes(shader.Convert()), ".shader"),
            _ => throw new NotSupportedException($"No converted exporter is available for {asset.type}.")
        };

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

    private static (byte[] Data, string Extension) ExportMonoBehaviour(MonoBehaviour behaviour)
    {
        var typeData = behaviour.ToType();
        if (typeData is null)
        {
            throw new NotSupportedException("This MonoBehaviour has no serialized type tree. Export its raw data or load the game's managed assemblies on desktop.");
        }

        return (Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(typeData, Formatting.Indented)), ".json");
    }

    private static (byte[] Data, string Extension) ExportTexture(Texture2D texture)
    {
        var bgra = DecodeTexture(texture);
        return (EncodePng(texture.m_Width, texture.m_Height, bgra), ".png");
    }

    private static byte[] DecodeTexture(Texture2D texture)
    {
        if (texture.m_Width <= 0 || texture.m_Height <= 0
            || (long)texture.m_Width * texture.m_Height > MaxTexturePixels)
            throw new InvalidDataException("This texture is too large or has invalid dimensions for decoding on iPhone.");
        try
        {
            var bgra = new byte[checked(texture.m_Width * texture.m_Height * 4)];
            if (!new Texture2DConverter(texture).DecodeTexture2D(bgra))
                throw new NotSupportedException($"Texture format {texture.m_TextureFormat} could not be decoded.");
            return bgra;
        }
        catch (DllNotFoundException exception)
        {
            throw new NotSupportedException(
                $"Texture format {texture.m_TextureFormat} requires the native texture decoder, which is not yet available in the iOS app.",
                exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new NotSupportedException(
                $"Texture format {texture.m_TextureFormat} requires a decoder function not available in the iOS app.",
                exception);
        }
        catch (TypeInitializationException exception) when (
            exception.InnerException is NotSupportedException
                or DllNotFoundException
                or EntryPointNotFoundException)
        {
            throw new NotSupportedException(
                $"Texture format {texture.m_TextureFormat} requires a native decoder that is not available on iOS.",
                exception.InnerException);
        }
    }

    private static (byte[] Data, string Extension) ExportSprite(Sprite sprite)
    {
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

        var source = DecodeTexture(texture);
        var alphaSource = alphaTexture == null ? null : DecodeTexture(alphaTexture);
        var scale = downscaleMultiplier > 0 && float.IsFinite(downscaleMultiplier)
            ? 1f / downscaleMultiplier
            : 1f;
        var rectLeft = textureRect.x * scale;
        var rectTop = textureRect.y * scale;
        var rectRight = (textureRect.x + textureRect.width) * scale;
        var rectBottom = (textureRect.y + textureRect.height) * scale;
        if (!float.IsFinite(rectLeft) || !float.IsFinite(rectTop)
            || !float.IsFinite(rectRight) || !float.IsFinite(rectBottom))
            throw new InvalidDataException("The sprite has invalid texture rectangle values.");
        var left = (int)Math.Clamp(MathF.Floor(rectLeft), 0, texture.m_Width);
        var top = (int)Math.Clamp(MathF.Floor(rectTop), 0, texture.m_Height);
        var right = (int)Math.Clamp(MathF.Ceiling(rectRight), 0, texture.m_Width);
        var bottom = (int)Math.Clamp(MathF.Ceiling(rectBottom), 0, texture.m_Height);
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
                var sourceOffset = checked(((top + sourceY) * texture.m_Width + left + sourceX) * 4);
                var outputOffset = checked((y * outputWidth + x) * 4);
                Buffer.BlockCopy(source, sourceOffset, output, outputOffset, 4);
                if (alphaSource != null)
                {
                    var alphaX = Math.Clamp(
                        (int)((left + sourceX + 0.5) * alphaTexture!.m_Width / texture.m_Width),
                        0, alphaTexture.m_Width - 1);
                    var alphaY = Math.Clamp(
                        (int)((top + sourceY + 0.5) * alphaTexture.m_Height / texture.m_Height),
                        0, alphaTexture.m_Height - 1);
                    var alphaOffset = checked((alphaY * alphaTexture.m_Width + alphaX) * 4);
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
                output, outputWidth, outputHeight);
        }

        return (EncodePng(outputWidth, outputHeight, output), ".png");
    }

    private static void ApplySpriteMeshMask(
        Sprite sprite,
        Vector2 textureRectOffset,
        SpritePackingRotation rotation,
        int cropWidth,
        int cropHeight,
        byte[] pixels,
        int width,
        int height)
    {
        var renderData = sprite.m_RD;
        var triangles = ReadSpriteTriangles(renderData);
        if (triangles.Count == 0)
            throw new NotSupportedException("The tight-packed sprite has no readable triangles.");

        var covered = new bool[checked(width * height)];
        foreach (var triangle in triangles)
        {
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
            if (!covered[index])
                pixels[index * 4 + 3] = 0;
        }
    }

    private static List<(Vector3 A, Vector3 B, Vector3 C)> ReadSpriteTriangles(SpriteRenderData renderData)
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        if (renderData.vertices is { Length: > 0 } && renderData.indices is { Length: > 0 })
        {
            for (var index = 0; index + 2 < renderData.indices.Length; index += 3)
            {
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
        return rotation switch
        {
            SpritePackingRotation.FlipHorizontal => (cropWidth - x, y),
            SpritePackingRotation.FlipVertical => (x, cropHeight - y),
            SpritePackingRotation.Rotate180 => (cropWidth - x, cropHeight - y),
            SpritePackingRotation.Rotate90 => (cropHeight - y, x),
            _ => (x, y)
        };
    }

    private static double Edge((double X, double Y) a, (double X, double Y) b, double x, double y) =>
        (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);

    private static byte[] EncodePng(int width, int height, byte[] bgra)
    {
        using var imageData = new MemoryStream();
        using (var zlib = new ZLibStream(imageData, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[checked(width * 4 + 1)];
            for (var y = 0; y < height; y++)
            {
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

    private static (byte[] Data, string Extension) ExportAudio(AudioClip audio)
    {
        var data = audio.m_AudioData.GetData();
        if (data is not { Length: > 0 })
        {
            throw new InvalidDataException("This audio clip contains no embedded audio data.");
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
                FMODSoundType.XMA => ".wav",
                FMODSoundType.VAG => ".vag",
                _ => ".fsb"
            }
            : audio.m_CompressionFormat switch
            {
                AudioCompressionFormat.AAC => ".m4a",
                _ => ".fsb"
            };

        return (data, extension);
    }

    private static (byte[] Data, string Extension) ExportVideo(VideoClip video)
    {
        var data = video.m_VideoData.GetData();
        if (data is not { Length: > 0 })
            throw new InvalidDataException("This video clip contains no embedded video data.");

        var extension = Path.GetExtension(video.m_OriginalPath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10
            || extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.'))
        {
            extension = ".video";
        }
        return (data, extension.ToLowerInvariant());
    }

    private static (byte[] Data, string Extension) ExportMovieTexture(MovieTexture movie)
    {
        if (movie.m_MovieData is not { Length: > 0 })
            throw new InvalidDataException("This movie texture contains no embedded video data.");
        return (movie.m_MovieData, ".ogv");
    }

    private static string ExportObj(Mesh mesh)
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

        var uvs = mesh.GetUV(0) ?? Array.Empty<float>();
        var uvStride = uvs.Length == 0 ? 0 : uvs.Length / mesh.m_VertexCount;
        var normals = mesh.m_Normals ?? Array.Empty<float>();
        var normalStride = normals.Length == 0 ? 0 : normals.Length / mesh.m_VertexCount;
        var builder = new StringBuilder(mesh.m_VertexCount * 48);
        builder.AppendLine("# Exported by AssetStudio Mobile");
        var hasFaces = false;

        for (var i = 0; i < mesh.m_VertexCount; i++)
        {
            builder.Append("v ")
                .Append((-positions[i * positionStride]).ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(positions[i * positionStride + 1].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                .Append(positions[i * positionStride + 2].ToString("R", CultureInfo.InvariantCulture)).AppendLine();
        }

        if (uvStride >= 2)
        {
            for (var i = 0; i < mesh.m_VertexCount; i++)
            {
                builder.Append("vt ")
                    .Append(uvs[i * uvStride].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append((1f - uvs[i * uvStride + 1]).ToString("R", CultureInfo.InvariantCulture)).AppendLine();
            }
        }

        if (normalStride >= 3)
        {
            for (var i = 0; i < mesh.m_VertexCount; i++)
            {
                builder.Append("vn ")
                    .Append((-normals[i * normalStride]).ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(normals[i * normalStride + 1].ToString("R", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(normals[i * normalStride + 2].ToString("R", CultureInfo.InvariantCulture)).AppendLine();
            }
        }

        var firstFace = 0;
        foreach (var submesh in mesh.m_SubMeshes)
        {
            if (firstFace > mesh.m_Indices.Count
                || submesh.indexCount > (uint)(mesh.m_Indices.Count - firstFace)
                || submesh.indexCount % 3 != 0)
            {
                throw new InvalidDataException("The mesh contains an invalid triangle index range.");
            }

            var faceEnd = firstFace + checked((int)(submesh.indexCount / 3));
            for (var face = firstFace; face < faceEnd; face++)
            {
                var indices = new[]
                {
                    mesh.m_Indices[face * 3 + 2],
                    mesh.m_Indices[face * 3 + 1],
                    mesh.m_Indices[face * 3]
                };
                builder.Append("f");
                foreach (var rawIndex in indices)
                {
                    var index = checked((int)rawIndex - (int)submesh.firstVertex);
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
            firstFace = faceEnd;
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
