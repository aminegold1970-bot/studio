using AssetStudio;
using Microsoft.Maui.Graphics;
using MauiColor = Microsoft.Maui.Graphics.Color;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;

namespace AssetStudioMobile;

internal sealed class MeshPreviewPage : ContentPage
{
    private readonly MeshWireframeDrawable _drawable;
    private readonly GraphicsView _graphicsView;

    public MeshPreviewPage(Mesh mesh)
        : this(mesh?.m_Name ?? "Mesh Preview", new MeshWireframeDrawable(mesh))
    {
    }

    internal MeshPreviewPage(string title, MeshWireframeDrawable drawable)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Mesh Preview" : title;
        _drawable = drawable;
        _graphicsView = new GraphicsView
        {
            Drawable = _drawable,
            BackgroundColor = MauiColor.FromArgb("#A7A5A6")
        };

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPanUpdated;
        _graphicsView.GestureRecognizers.Add(pan);

        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += OnPinchUpdated;
        _graphicsView.GestureRecognizers.Add(pinch);

        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
        var instructions = new Label
        {
            Text = "Drag to rotate • Pinch to zoom",
            TextColor = MauiColor.FromArgb("#555555"),
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 10, 0, 4)
        };
        var grid = new Grid
        {
            Padding = 4,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        grid.Add(_graphicsView);
        grid.Add(instructions, 0, 1);
        Content = grid;
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        _drawable.UpdatePan(e);
        _graphicsView.Invalidate();
    }

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        _drawable.UpdatePinch(e);
        _graphicsView.Invalidate();
    }
}

internal sealed class MeshWireframeDrawable : IDrawable
{
    private const int MaxTriangles = 50_000;
    private readonly NumericsVector3[] _vertices;
    private readonly (int A, int B, int C)[] _triangles;
    private float _yaw = -0.55f;
    private float _pitch = 0.35f;
    private float _zoom = 1f;
    private float _startYaw;
    private float _startPitch;
    private float _startZoom;

    public MeshWireframeDrawable(Mesh mesh)
        : this(new[]
        {
            new MobileAssetExporter.SceneMeshGeometry(
                mesh,
                NumericsMatrix4x4.Identity,
                string.IsNullOrWhiteSpace(mesh?.m_Name) ? "Mesh" : mesh.m_Name,
                Array.Empty<Material?>(),
                null,
                null,
                null,
                null)
        }, false)
    {
    }

    public MeshWireframeDrawable(IReadOnlyList<MobileAssetExporter.SceneMeshGeometry> sceneMeshes)
        : this(sceneMeshes, true)
    {
    }

    private MeshWireframeDrawable(
        IReadOnlyList<MobileAssetExporter.SceneMeshGeometry> sceneMeshes,
        bool applySceneTransforms)
    {
        ArgumentNullException.ThrowIfNull(sceneMeshes);
        var vertices = new List<NumericsVector3>();
        var triangles = new List<(int A, int B, int C)>();
        foreach (var geometry in sceneMeshes)
        {
            var mesh = geometry.Mesh;
            if (mesh.m_VertexCount <= 0 || mesh.m_Vertices is null
                || mesh.m_Vertices.Length < checked(mesh.m_VertexCount * 3))
            {
                throw new NotSupportedException($"Mesh '{mesh.m_Name}' does not contain decoded vertex positions.");
            }

            var positions = mesh.m_Vertices;
            var vertexStride = positions.Length / mesh.m_VertexCount;
            if (vertexStride is not (3 or 4))
                throw new NotSupportedException($"Mesh '{mesh.m_Name}' uses an unsupported vertex layout.");
            if (geometry.BlendShapePositionDeltas is { } blendShapeDeltas
                && blendShapeDeltas.Length < mesh.m_VertexCount)
            {
                throw new InvalidDataException($"Mesh '{mesh.m_Name}' has incomplete blend-shape position data.");
            }

            var meshVertexOffset = vertices.Count;
            for (var vertexIndex = 0; vertexIndex < mesh.m_VertexCount; vertexIndex++)
            {
                var sourceOffset = vertexIndex * vertexStride;
                var position = new NumericsVector3(
                    positions[sourceOffset],
                    positions[sourceOffset + 1],
                    positions[sourceOffset + 2]);
                if (applySceneTransforms)
                {
                    position.X = -position.X;
                    if (geometry.BlendShapePositionDeltas is { } deltas)
                        position += deltas[vertexIndex];
                    position = MobileAssetExporter.TransformVertexPosition(
                        mesh, vertexIndex, position, geometry.WorldTransform, geometry.SkinTransforms);
                }

                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' contains a non-finite preview vertex.");
                vertices.Add(position);
            }

            var firstIndex = 0;
            foreach (var submesh in mesh.m_SubMeshes)
            {
                if (submesh.indexCount > int.MaxValue)
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' has an oversized triangle index range.");
                var indexCount = (int)submesh.indexCount;
                if (firstIndex > mesh.m_Indices.Count || indexCount > mesh.m_Indices.Count - firstIndex)
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' has an invalid triangle index range.");
                var endIndex = checked(firstIndex + indexCount);
                for (var index = firstIndex; index + 2 < endIndex; index += 3)
                {
                    var baseVertex = checked((int)submesh.firstVertex);
                    var a = checked((int)mesh.m_Indices[index] - baseVertex);
                    var b = checked((int)mesh.m_Indices[index + 1] - baseVertex);
                    var c = checked((int)mesh.m_Indices[index + 2] - baseVertex);
                    if ((uint)a >= (uint)mesh.m_VertexCount
                        || (uint)b >= (uint)mesh.m_VertexCount
                        || (uint)c >= (uint)mesh.m_VertexCount)
                    {
                        throw new InvalidDataException($"Mesh '{mesh.m_Name}' contains an out-of-range triangle index.");
                    }
                    triangles.Add((meshVertexOffset + a, meshVertexOffset + b, meshVertexOffset + c));
                    if (triangles.Count > MaxTriangles)
                        throw new NotSupportedException($"Preview is limited to {MaxTriangles:N0} triangles.");
                }
                firstIndex = endIndex;
            }
        }

        if (triangles.Count == 0)
            throw new NotSupportedException("The selected objects contain no triangle faces to preview.");
        _vertices = vertices.ToArray();
        _triangles = triangles.ToArray();
    }

    public void UpdatePan(PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started)
        {
            _startYaw = _yaw;
            _startPitch = _pitch;
        }
        else if (e.StatusType == GestureStatus.Running)
        {
            _yaw = _startYaw + (float)e.TotalX / 180f;
            _pitch = Math.Clamp(_startPitch + (float)e.TotalY / 180f, -1.5f, 1.5f);
        }
    }

    public void UpdatePinch(PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started)
        {
            _startZoom = _zoom;
        }
        else if (e.Status == GestureStatus.Running)
        {
            _zoom = Math.Clamp(_startZoom * (float)e.Scale, 0.25f, 5f);
        }
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.FillColor = MauiColor.FromArgb("#A7A5A6");
        canvas.FillRectangle(dirtyRect);
        var bounds = GetBounds();
        var centerX = (bounds.MinX + bounds.MaxX) * 0.5f;
        var centerY = (bounds.MinY + bounds.MaxY) * 0.5f;
        var centerZ = (bounds.MinZ + bounds.MaxZ) * 0.5f;
        var extent = MathF.Max(bounds.MaxX - bounds.MinX,
            MathF.Max(bounds.MaxY - bounds.MinY, bounds.MaxZ - bounds.MinZ));
        if (extent <= 0f)
        {
            return;
        }

        var scale = MathF.Min(dirtyRect.Width, dirtyRect.Height) * 0.8f * _zoom / extent;
        var projected = new PointF[_vertices.Length];
        var cosYaw = MathF.Cos(_yaw);
        var sinYaw = MathF.Sin(_yaw);
        var cosPitch = MathF.Cos(_pitch);
        var sinPitch = MathF.Sin(_pitch);
        for (var i = 0; i < projected.Length; i++)
        {
            var x = _vertices[i].X - centerX;
            var y = _vertices[i].Y - centerY;
            var z = _vertices[i].Z - centerZ;
            var rotatedX = x * cosYaw - z * sinYaw;
            var rotatedZ = x * sinYaw + z * cosYaw;
            var rotatedY = y * cosPitch - rotatedZ * sinPitch;
            projected[i] = new PointF(
                dirtyRect.Center.X + rotatedX * scale,
                dirtyRect.Center.Y - rotatedY * scale);
        }

        canvas.StrokeColor = MauiColor.FromArgb("#555555");
        canvas.StrokeSize = 1f;
        foreach (var (a, b, c) in _triangles)
        {
            canvas.DrawLine(projected[a], projected[b]);
            canvas.DrawLine(projected[b], projected[c]);
            canvas.DrawLine(projected[c], projected[a]);
        }
    }

    private (float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ) GetBounds()
    {
        var minX = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        var minZ = float.PositiveInfinity;
        var maxZ = float.NegativeInfinity;
        foreach (var vertex in _vertices)
        {
            minX = MathF.Min(minX, vertex.X);
            maxX = MathF.Max(maxX, vertex.X);
            minY = MathF.Min(minY, vertex.Y);
            maxY = MathF.Max(maxY, vertex.Y);
            minZ = MathF.Min(minZ, vertex.Z);
            maxZ = MathF.Max(maxZ, vertex.Z);
        }
        return (minX, maxX, minY, maxY, minZ, maxZ);
    }
}
