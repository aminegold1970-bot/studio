#if IOS
using AssetStudio;
using Foundation;
using FbxSharp;
using ModelIO;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;

namespace AssetStudioMobile;

internal static class MobileFbxExporter
{
    public static string ExportMeshes(IEnumerable<Mesh> sourceMeshes, CancellationToken cancellationToken = default)
    {
        var meshes = sourceMeshes?.Distinct().ToList()
            ?? throw new ArgumentNullException(nameof(sourceMeshes));
        if (meshes.Count == 0)
            throw new InvalidOperationException("No meshes were selected for FBX export.");

        return ExportGeometry(
            meshes.Select(mesh => new MobileAssetExporter.SceneMeshGeometry(
                mesh,
                NumericsMatrix4x4.Identity,
                string.IsNullOrWhiteSpace(mesh.m_Name) ? "Mesh" : mesh.m_Name,
                Array.Empty<Material>(),
                null,
                null,
                null,
                null)),
            cancellationToken);
    }

    public static string ExportSceneMeshes(
        IEnumerable<MobileAssetExporter.SceneMeshGeometry> sourceMeshes,
        CancellationToken cancellationToken = default)
    {
        var meshes = sourceMeshes?.ToList()
            ?? throw new ArgumentNullException(nameof(sourceMeshes));
        if (meshes.Count == 0)
            throw new InvalidOperationException("The selected scene objects contain no exportable meshes.");

        return ExportGeometry(meshes, cancellationToken);
    }

    private static string ExportGeometry(
        IEnumerable<MobileAssetExporter.SceneMeshGeometry> sourceMeshes,
        CancellationToken cancellationToken)
    {
        var meshes = sourceMeshes.ToList();
        if (meshes.Count == 0)
            throw new InvalidOperationException("No meshes were selected for FBX export.");

        var outputDirectory = Path.Combine(FileSystem.CacheDirectory, $"AssetStudio_FBX_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "AssetStudio.fbx");

        try
        {
            using var allocator = new MDLMeshBufferDataAllocator();
            using var asset = new MDLAsset(allocator);
            foreach (var geometry in meshes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                asset.AddObject(CreateModelIoMesh(
                    geometry.Mesh,
                    geometry.WorldTransform,
                    geometry.Name,
                    geometry.SkinTransforms,
                    geometry.SkinNormalTransforms,
                    geometry.BlendShapePositionDeltas,
                    geometry.BlendShapeNormalDeltas,
                    allocator,
                    cancellationToken));
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
        NumericsMatrix4x4 worldTransform,
        string objectName,
        NumericsMatrix4x4[]? skinTransforms,
        NumericsMatrix4x4[]? skinNormalTransforms,
        NumericsVector3[]? blendShapePositionDeltas,
        NumericsVector3[]? blendShapeNormalDeltas,
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
        var hasNormalTransform = NumericsMatrix4x4.Invert(worldTransform, out var inverseTransform);
        var hasNormals = hasNormalTransform
            && normals is { Length: > 0 }
            && normals.Length % vertexCount == 0
            && normals.Length / vertexCount == 3;
        var colors = source.m_Colors;
        var colorStride = colors is { Length: > 0 } && colors.Length % vertexCount == 0
            ? colors.Length / vertexCount
            : 0;
        var hasColors = colorStride is 3 or 4;
        var normalTransform = hasNormals
            ? NumericsMatrix4x4.Transpose(inverseTransform)
            : NumericsMatrix4x4.Identity;
        var determinant = worldTransform.GetDeterminant();
        if (!float.IsFinite(determinant))
            throw new InvalidDataException($"Mesh '{source.m_Name}' has an invalid world transform.");
        var reverseFaceWinding = determinant >= 0;
        var uvs = source.GetUV(0);
        var hasUvs = uvs is { Length: > 0 }
            && uvs.Length % vertexCount == 0
            && uvs.Length / vertexCount == 2;
        var componentsPerVertex = 3 + (hasNormals ? 3 : 0) + (hasColors ? 4 : 0) + (hasUvs ? 2 : 0);
        var vertexData = new float[checked(vertexCount * componentsPerVertex)];
        for (var index = 0; index < vertexCount; index++)
        {
            if ((index & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var sourceOffset = index * positionStride;
            var targetOffset = index * componentsPerVertex;
            var sourcePosition = new NumericsVector3(
                -positions[sourceOffset],
                positions[sourceOffset + 1],
                positions[sourceOffset + 2]);
            if (blendShapePositionDeltas != null)
                sourcePosition += blendShapePositionDeltas[index];
            var position = MobileAssetExporter.TransformVertexPosition(
                source,
                index,
                sourcePosition,
                worldTransform,
                skinTransforms);
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new InvalidDataException($"Mesh '{source.m_Name}' has a non-finite transformed vertex.");
            vertexData[targetOffset] = position.X;
            vertexData[targetOffset + 1] = position.Y;
            vertexData[targetOffset + 2] = position.Z;
            var attributeOffset = targetOffset + 3;
            if (hasNormals)
            {
                var sourceNormal = new NumericsVector3(
                    -normals![index * 3],
                    normals[index * 3 + 1],
                    normals[index * 3 + 2]);
                if (blendShapeNormalDeltas != null)
                    sourceNormal += blendShapeNormalDeltas[index];
                var normal = MobileAssetExporter.TransformVertexNormal(
                    source,
                    index,
                    sourceNormal,
                    normalTransform,
                    skinNormalTransforms);
                var lengthSquared = normal.LengthSquared();
                if (!float.IsFinite(lengthSquared))
                    throw new InvalidDataException($"Mesh '{source.m_Name}' has a non-finite transformed normal.");
                if (lengthSquared > 1e-12f)
                    normal = NumericsVector3.Normalize(normal);
                vertexData[attributeOffset] = normal.X;
                vertexData[attributeOffset + 1] = normal.Y;
                vertexData[attributeOffset + 2] = normal.Z;
                attributeOffset += 3;
            }
            if (hasColors)
            {
                var colorOffset = index * colorStride;
                for (var channel = 0; channel < colorStride; channel++)
                {
                    var value = colors![colorOffset + channel];
                    if (!float.IsFinite(value))
                        throw new InvalidDataException($"Mesh '{source.m_Name}' contains a non-finite vertex color.");
                    vertexData[attributeOffset + channel] = value;
                }
                if (colorStride == 3)
                    vertexData[attributeOffset + 3] = 1;
                attributeOffset += 4;
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
        var nextAttributeOffset = 3 + (hasNormals ? 3 : 0);
        if (hasColors)
        {
            descriptor.AddOrReplaceAttribute(new MDLVertexAttribute(
                MDLVertexAttributes.Color,
                MDLVertexFormat.Float4,
                (nuint)(nextAttributeOffset * sizeof(float)),
                0));
            nextAttributeOffset += 4;
        }
        if (hasUvs)
        {
            descriptor.AddOrReplaceAttribute(new MDLVertexAttribute(
                MDLVertexAttributes.TextureCoordinate,
                MDLVertexFormat.Float2,
                (nuint)(nextAttributeOffset * sizeof(float)),
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
                var triangleIndex = index - index % 3
                    + (reverseFaceWinding ? 2 - index % 3 : index % 3);
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
            Name = string.IsNullOrWhiteSpace(objectName) ? "Mesh" : objectName
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

    public static string ExportSceneMeshes(
        IEnumerable<MobileAssetExporter.SceneMeshGeometry> sourceMeshes,
        CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("FBX export is available only in the iOS app.");
}
#endif
