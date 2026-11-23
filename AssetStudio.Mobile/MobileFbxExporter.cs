#if IOS
using AssetStudio;
using Foundation;
using FbxSharp;
using ModelIO;

namespace AssetStudioMobile;

internal static class MobileFbxExporter
{
    public static string ExportMeshes(IEnumerable<Mesh> sourceMeshes, CancellationToken cancellationToken = default)
    {
        var meshes = sourceMeshes?.Distinct().ToList()
            ?? throw new ArgumentNullException(nameof(sourceMeshes));
        if (meshes.Count == 0)
            throw new InvalidOperationException("No meshes were selected for FBX export.");

        var outputDirectory = Path.Combine(FileSystem.CacheDirectory, $"AssetStudio_FBX_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "AssetStudio.fbx");

        try
        {
            using var allocator = new MDLMeshBufferDataAllocator();
            using var asset = new MDLAsset(allocator);
            foreach (var mesh in meshes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                asset.AddObject(CreateModelIoMesh(mesh, allocator, cancellationToken));
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var scene = new FbxScene(asset);
            scene.Save(outputPath, -1, 0, 0);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidDataException("The FBX SDK did not produce an output file.");
            return outputPath;
        }
        catch
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
            if (Directory.Exists(outputDirectory)
                && !Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            {
                Directory.Delete(outputDirectory);
            }
            throw;
        }
    }

    private static MDLMesh CreateModelIoMesh(
        Mesh source,
        MDLMeshBufferDataAllocator allocator,
        CancellationToken cancellationToken)
    {
        var positions = source.m_Vertices;
        var vertexCount = source.m_VertexCount;
        if (positions is null || vertexCount <= 0 || positions.Length < (long)vertexCount * 3)
            throw new NotSupportedException($"Mesh '{source.m_Name}' has no decoded vertex positions.");

        var positionStride = positions.Length / vertexCount;
        if (positionStride is not (3 or 4))
            throw new NotSupportedException($"Mesh '{source.m_Name}' uses an unsupported vertex layout.");

        var normals = source.m_Normals;
        var hasNormals = normals is { Length: > 0 }
            && normals.Length % vertexCount == 0
            && normals.Length / vertexCount == 3;
        var uvs = source.GetUV(0);
        var hasUvs = uvs is { Length: > 0 }
            && uvs.Length % vertexCount == 0
            && uvs.Length / vertexCount == 2;
        var componentsPerVertex = 3 + (hasNormals ? 3 : 0) + (hasUvs ? 2 : 0);
        var vertexData = new float[checked(vertexCount * componentsPerVertex)];
        for (var index = 0; index < vertexCount; index++)
        {
            if ((index & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var sourceOffset = index * positionStride;
            var targetOffset = index * componentsPerVertex;
            vertexData[targetOffset] = -positions[sourceOffset];
            vertexData[targetOffset + 1] = positions[sourceOffset + 1];
            vertexData[targetOffset + 2] = positions[sourceOffset + 2];
            var attributeOffset = targetOffset + 3;
            if (hasNormals)
            {
                vertexData[attributeOffset] = -normals![index * 3];
                vertexData[attributeOffset + 1] = normals[index * 3 + 1];
                vertexData[attributeOffset + 2] = normals[index * 3 + 2];
                attributeOffset += 3;
            }
            if (hasUvs)
                Array.Copy(uvs!, index * 2, vertexData, attributeOffset, 2);
        }

        var descriptor = new MDLVertexDescriptor();
        descriptor.AddOrReplaceAttribute(new MDLVertexAttribute(
            MDLVertexAttributes.Position,
            MDLVertexFormat.Float3,
            0,
            0));
        if (hasNormals)
        {
            descriptor.AddOrReplaceAttribute(new MDLVertexAttribute(
                MDLVertexAttributes.Normal,
                MDLVertexFormat.Float3,
                (nuint)(3 * sizeof(float)),
                0));
        }
        if (hasUvs)
        {
            descriptor.AddOrReplaceAttribute(new MDLVertexAttribute(
                MDLVertexAttributes.TextureCoordinate,
                MDLVertexFormat.Float2,
                (nuint)((3 + (hasNormals ? 3 : 0)) * sizeof(float)),
                0));
        }
        descriptor.SetPackedOffsets();
        descriptor.SetPackedStrides();

        var vertexBytes = new byte[checked(vertexData.Length * sizeof(float))];
        Buffer.BlockCopy(vertexData, 0, vertexBytes, 0, vertexBytes.Length);
        using var vertexDataObject = NSData.FromArray(vertexBytes);
        var vertexBuffer = allocator.CreateBuffer(vertexDataObject, MDLMeshBufferType.Vertex);

        var indexValues = source.m_Indices;
        var sourceSubmeshes = source.m_SubMeshes;
        if (indexValues is null || sourceSubmeshes is not { Length: > 0 })
            throw new NotSupportedException($"Mesh '{source.m_Name}' has no triangle indices.");

        var submeshes = new List<MDLSubmesh>(sourceSubmeshes.Length);
        var indexOffset = 0;
        foreach (var sourceSubmesh in sourceSubmeshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indexCount = checked((int)sourceSubmesh.indexCount);
            if (indexCount == 0)
                continue;
            if (indexCount % 3 != 0
                || indexOffset > indexValues.Count
                || indexCount > indexValues.Count - indexOffset)
            {
                throw new InvalidDataException($"Mesh '{source.m_Name}' contains an invalid triangle index range.");
            }

            var indices = new uint[indexCount];
            for (var index = 0; index < indexCount; index++)
            {
                if ((index & 2047) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var triangleIndex = index - index % 3 + (2 - index % 3);
                var vertexIndex = indexValues[indexOffset + triangleIndex];
                if (vertexIndex >= vertexCount)
                    throw new InvalidDataException($"Mesh '{source.m_Name}' contains an out-of-range vertex index.");
                indices[index] = vertexIndex;
            }

            var indexBytes = new byte[checked(indices.Length * sizeof(uint))];
            Buffer.BlockCopy(indices, 0, indexBytes, 0, indexBytes.Length);
            using var indexData = NSData.FromArray(indexBytes);
            var indexBuffer = allocator.CreateBuffer(indexData, MDLMeshBufferType.Index);
            submeshes.Add(new MDLSubmesh(
                indexBuffer,
                (nuint)indexCount,
                MDLIndexBitDepth.UInt32,
                MDLGeometryType.Triangles,
                null));
            indexOffset += indexCount;
        }

        if (submeshes.Count == 0)
            throw new NotSupportedException($"Mesh '{source.m_Name}' has no triangle faces to export.");

        var result = new MDLMesh(vertexBuffer, (nuint)vertexCount, descriptor, submeshes.ToArray())
        {
            Name = string.IsNullOrWhiteSpace(source.m_Name) ? "Mesh" : source.m_Name
        };
        return result;
    }
}
#else
using AssetStudio;

namespace AssetStudioMobile;

internal static class MobileFbxExporter
{
    public static string ExportMeshes(IEnumerable<Mesh> sourceMeshes, CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("FBX export is available only in the iOS app.");
}
#endif
