using AssetStudio;
using Microsoft.Maui.Graphics;
using MauiColor = Microsoft.Maui.Graphics.Color;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;

namespace AssetStudioMobile;

internal sealed class MeshPreviewPage : ContentPage
{
    private readonly MeshPreviewDrawable _drawable;
    private readonly GraphicsView _graphicsView;

    public MeshPreviewPage(Mesh mesh)
        : this(mesh?.m_Name ?? "Mesh Preview", new MeshPreviewDrawable(mesh))
    {
    }

    internal MeshPreviewPage(string title, MeshPreviewDrawable drawable)
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
        var renderMode = new ToolbarItem { Text = "Wireframe" };
        renderMode.Clicked += OnRenderModeClicked;
        ToolbarItems.Add(renderMode);
        var instructions = new Label
        {
            Text = "Drag to rotate • Pinch to zoom • Toggle solid/wireframe",
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

    private void OnRenderModeClicked(object? sender, EventArgs e)
    {
        _drawable.ToggleRenderMode();
        if (sender is ToolbarItem item)
            item.Text = _drawable.IsWireframe ? "Solid" : "Wireframe";
        _graphicsView.Invalidate();
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

internal sealed class MeshPreviewDrawable : IDrawable
{
    private const int MaxTriangles = 50_000;
    private static readonly MauiColor DefaultMaterialColor = MauiColor.FromArgb("#8EA7C1");
    private readonly NumericsVector3[] _vertices;
    private readonly MeshTriangle[] _triangles;
    private float _yaw = -0.55f;
    private float _pitch = 0.35f;
    private float _zoom = 1f;
    private float _startYaw;
    private float _startPitch;
    private float _startZoom;

    public bool IsWireframe { get; private set; }

    public void ToggleRenderMode() => IsWireframe = !IsWireframe;

    private readonly record struct MeshTriangle(int A, int B, int C, MauiColor Color);

    public MeshPreviewDrawable(Mesh mesh)
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

    public MeshPreviewDrawable(IReadOnlyList<MobileAssetExporter.SceneMeshGeometry> sceneMeshes)
        : this(sceneMeshes, true)
    {
    }

    private MeshPreviewDrawable(
        IReadOnlyList<MobileAssetExporter.SceneMeshGeometry> sceneMeshes,
        bool applySceneTransforms)
    {
        ArgumentNullException.ThrowIfNull(sceneMeshes);
        var vertices = new List<NumericsVector3>();
        var triangles = new List<MeshTriangle>();
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

            var colors = mesh.m_Colors ?? Array.Empty<float>();
            var colorStride = 0;
            if (colors.Length > 0)
            {
                if (colors.Length % mesh.m_VertexCount != 0
                    || colors.Length / mesh.m_VertexCount is not (3 or 4))
                {
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' has an unsupported vertex-color layout.");
                }
                colorStride = colors.Length / mesh.m_VertexCount;
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
            for (var submeshIndex = 0; submeshIndex < mesh.m_SubMeshes.Length; submeshIndex++)
            {
                var submesh = mesh.m_SubMeshes[submeshIndex];
                if (submesh.indexCount > int.MaxValue)
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' has an oversized triangle index range.");
                var indexCount = (int)submesh.indexCount;
                if (indexCount % 3 != 0
                    || firstIndex > mesh.m_Indices.Count
                    || indexCount > mesh.m_Indices.Count - firstIndex)
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' has an invalid triangle index range.");
                var endIndex = checked(firstIndex + indexCount);
                var materialColor = submeshIndex < geometry.Materials.Length
                    ? GetMaterialColor(geometry.Materials[submeshIndex])
                    : DefaultMaterialColor;
                for (var index = firstIndex; index + 2 < endIndex; index += 3)
                {
                    var sourceA = mesh.m_Indices[index];
                    var sourceB = mesh.m_Indices[index + 1];
                    var sourceC = mesh.m_Indices[index + 2];
                    if (sourceA >= mesh.m_VertexCount
                        || sourceB >= mesh.m_VertexCount
                        || sourceC >= mesh.m_VertexCount)
                    {
                        throw new InvalidDataException($"Mesh '{mesh.m_Name}' contains an out-of-range triangle index.");
                    }
                    var a = checked((int)sourceA);
                    var b = checked((int)sourceB);
                    var c = checked((int)sourceC);
                    var triangleColor = GetTriangleColor(
                        materialColor, colors, colorStride, a, b, c);
                    triangles.Add(new MeshTriangle(
                        meshVertexOffset + a,
                        meshVertexOffset + b,
                        meshVertexOffset + c,
                        triangleColor));
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

    private static MauiColor GetMaterialColor(Material? material)
    {
        if (material == null)
            return DefaultMaterialColor;

        foreach (var property in material.m_SavedProperties.m_Colors)
        {
            if (property.Key is "_Color" or "_BaseColor")
            {
                var color = property.Value;
                if (!float.IsFinite(color.R) || !float.IsFinite(color.G)
                    || !float.IsFinite(color.B) || !float.IsFinite(color.A))
                {
                    throw new InvalidDataException($"Material '{material.m_Name}' contains a non-finite color.");
                }

                return new MauiColor(
                    Math.Clamp(color.R, 0, 1),
                    Math.Clamp(color.G, 0, 1),
                    Math.Clamp(color.B, 0, 1),
                    Math.Clamp(color.A, 0, 1));
            }
        }

        return DefaultMaterialColor;
    }

    private static MauiColor GetTriangleColor(
        MauiColor materialColor,
        float[] vertexColors,
        int colorStride,
        int a,
        int b,
        int c)
    {
        if (colorStride == 0)
            return materialColor;

        var red = (GetVertexChannel(vertexColors, colorStride, a, 0)
            + GetVertexChannel(vertexColors, colorStride, b, 0)
            + GetVertexChannel(vertexColors, colorStride, c, 0)) / 3;
        var green = (GetVertexChannel(vertexColors, colorStride, a, 1)
            + GetVertexChannel(vertexColors, colorStride, b, 1)
            + GetVertexChannel(vertexColors, colorStride, c, 1)) / 3;
        var blue = (GetVertexChannel(vertexColors, colorStride, a, 2)
            + GetVertexChannel(vertexColors, colorStride, b, 2)
            + GetVertexChannel(vertexColors, colorStride, c, 2)) / 3;
        var alpha = colorStride == 4
            ? (GetVertexChannel(vertexColors, colorStride, a, 3)
                + GetVertexChannel(vertexColors, colorStride, b, 3)
                + GetVertexChannel(vertexColors, colorStride, c, 3)) / 3
            : 1;
        return new MauiColor(
            materialColor.Red * red,
            materialColor.Green * green,
            materialColor.Blue * blue,
            materialColor.Alpha * alpha);
    }

    private static float GetVertexChannel(float[] colors, int stride, int vertex, int channel)
    {
        var value = colors[vertex * stride + channel];
        if (!float.IsFinite(value))
            throw new InvalidDataException("The mesh contains a non-finite vertex color.");
        return Math.Clamp(value, 0, 1);
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
        if (extent <= 0f || !float.IsFinite(extent))
            return;

        var scale = MathF.Min(dirtyRect.Width, dirtyRect.Height) * 0.8f * _zoom / extent;
        var projected = new ProjectedVertex[_vertices.Length];
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
            var viewDepth = y * sinPitch + rotatedZ * cosPitch;
            projected[i] = new ProjectedVertex(
                new PointF(
                    dirtyRect.Center.X + rotatedX * scale,
                    dirtyRect.Center.Y - rotatedY * scale),
                new NumericsVector3(rotatedX, rotatedY, viewDepth));
        }

        if (IsWireframe)
        {
            canvas.StrokeColor = MauiColor.FromArgb("#42474D");
            canvas.StrokeSize = 1f;
            foreach (var triangle in _triangles)
            {
                canvas.DrawLine(projected[triangle.A].Point, projected[triangle.B].Point);
                canvas.DrawLine(projected[triangle.B].Point, projected[triangle.C].Point);
                canvas.DrawLine(projected[triangle.C].Point, projected[triangle.A].Point);
            }
            return;
        }

        var orderedTriangles = new RenderTriangle[_triangles.Length];
        for (var index = 0; index < _triangles.Length; index++)
        {
            var triangle = _triangles[index];
            var depth = (projected[triangle.A].ViewPosition.Z
                + projected[triangle.B].ViewPosition.Z
                + projected[triangle.C].ViewPosition.Z) / 3;
            orderedTriangles[index] = new RenderTriangle(triangle, depth);
        }
        Array.Sort(orderedTriangles, static (left, right) => left.Depth.CompareTo(right.Depth));

        var lightDirection = NumericsVector3.Normalize(new NumericsVector3(-0.35f, 0.55f, 1f));
        foreach (var rendered in orderedTriangles)
        {
            var triangle = rendered.Triangle;
            var a = projected[triangle.A];
            var b = projected[triangle.B];
            var c = projected[triangle.C];
            var normal = NumericsVector3.Cross(
                b.ViewPosition - a.ViewPosition,
                c.ViewPosition - a.ViewPosition);
            var normalLengthSquared = normal.LengthSquared();
            var diffuse = 0f;
            if (float.IsFinite(normalLengthSquared) && normalLengthSquared > 1e-12f)
            {
                normal /= MathF.Sqrt(normalLengthSquared);
                diffuse = MathF.Max(0, NumericsVector3.Dot(normal, lightDirection));
            }

            var shade = 0.24f + diffuse * 0.76f;
            canvas.FillColor = new MauiColor(
                triangle.Color.Red * shade,
                triangle.Color.Green * shade,
                triangle.Color.Blue * shade,
                triangle.Color.Alpha);
            var face = new PathF();
            face.MoveTo(a.Point);
            face.LineTo(b.Point);
            face.LineTo(c.Point);
            face.Close();
            canvas.FillPath(face);
        }
    }

    private readonly record struct ProjectedVertex(PointF Point, NumericsVector3 ViewPosition);
    private readonly record struct RenderTriangle(MeshTriangle Triangle, float Depth);

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
