using System.Buffers.Binary;
using System.IO.Compression;
using System.Globalization;
using System.Text;
using AssetStudio;
using Newtonsoft.Json;

namespace AssetStudioMobile;

internal static class MobileAssetExporter
{
    private const int MaxTexturePixels = 16_000_000;

    public static string Export(AssetStudio.Object asset)
    {
        var (data, extension) = asset switch
        {
            Texture2D texture => ExportTexture(texture),
            TextAsset text => (text.m_Script, ".txt"),
            AudioClip audio => ExportAudio(audio),
            VideoClip video => ExportVideo(video),
            MonoBehaviour behaviour => ExportMonoBehaviour(behaviour),
            Mesh mesh => (Encoding.UTF8.GetBytes(ExportObj(mesh)), ".obj"),
            _ => (asset.GetRawData(), ".bin")
        };

        return WriteAssetFile(asset, data, extension);
    }

    public static string ExportRaw(AssetStudio.Object asset) =>
        WriteAssetFile(asset, asset.GetRawData(), ".bin");

    public static string ExportDump(AssetStudio.Object asset) =>
        WriteAssetFile(asset, Encoding.UTF8.GetBytes(asset.Dump() ?? string.Empty), ".txt");

    public static string ExportArchive(IEnumerable<AssetStudio.Object> assets, MobileExportKind kind)
    {
        var archivePath = Path.Combine(FileSystem.CacheDirectory, $"AssetStudio_{Guid.NewGuid():N}.zip");
        var temporaryFiles = new List<string>();
        try
        {
            using (var archive = new ZipArchive(File.Create(archivePath), ZipArchiveMode.Create))
            {
                var index = 0;
                foreach (var asset in assets)
                {
                    var filePath = kind switch
                    {
                        MobileExportKind.Raw => ExportRaw(asset),
                        MobileExportKind.Dump => ExportDump(asset),
                        _ => Export(asset)
                    };
                    temporaryFiles.Add(filePath);
                    var entry = archive.CreateEntry(
                        $"{index++:D5}_{Path.GetFileName(filePath)}",
                        CompressionLevel.Optimal);
                    using (var input = File.OpenRead(filePath))
                    using (var output = entry.Open())
                    {
                        input.CopyTo(output);
                    }
                    File.Delete(filePath);
                    temporaryFiles.Remove(filePath);
                }
            }

            return archivePath;
        }
        catch
        {
            foreach (var filePath in temporaryFiles)
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
            throw;
        }
    }

    private static string WriteAssetFile(AssetStudio.Object asset, byte[] data, string extension)
    {
        var name = asset is NamedObject named && !string.IsNullOrWhiteSpace(named.m_Name)
            ? named.m_Name
            : asset.type.ToString();
        var safeName = string.Concat(name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = asset.type.ToString();
        }

        var path = Path.Combine(FileSystem.CacheDirectory, $"{safeName}_{asset.m_PathID}{extension}");
        File.WriteAllBytes(path, data);
        return path;
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
        if (texture.m_Width <= 0 || texture.m_Height <= 0
            || (long)texture.m_Width * texture.m_Height > MaxTexturePixels)
        {
            throw new InvalidDataException("This texture is too large or has invalid dimensions for decoding on iPhone.");
        }

        try
        {
            var bgra = new byte[checked(texture.m_Width * texture.m_Height * 4)];
            if (!new Texture2DConverter(texture).DecodeTexture2D(bgra))
            {
                throw new NotSupportedException($"Texture format {texture.m_TextureFormat} could not be decoded.");
            }
            return (EncodePng(texture.m_Width, texture.m_Height, bgra), ".png");
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
        if (data.Length == 0)
        {
            throw new InvalidDataException("This audio clip contains no embedded audio data.");
        }

        var extension = audio.version[0] < 5
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
        var extension = Path.GetExtension(video.m_OriginalPath);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 10
            || extension.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '.'))
        {
            extension = ".video";
        }
        return (data, extension.ToLowerInvariant());
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
