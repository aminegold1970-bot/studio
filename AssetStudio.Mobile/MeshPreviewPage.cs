using AssetStudio;
using Microsoft.Maui.Graphics;
using MauiColor = Microsoft.Maui.Graphics.Color;

namespace AssetStudioMobile;

internal sealed class MeshPreviewPage : ContentPage
{
    private readonly MeshWireframeDrawable _drawable;
    private readonly GraphicsView _graphicsView;

    public MeshPreviewPage(Mesh mesh)
    {
        Title = mesh.m_Name;
        _drawable = new MeshWireframeDrawable(mesh);
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
    private readonly float[] _vertices;
    private readonly int _vertexStride;
    private readonly (int A, int B, int C)[] _triangles;
    private float _yaw = -0.55f;
    private float _pitch = 0.35f;
    private float _zoom = 1f;
    private float _startYaw;
    private float _startPitch;
    private float _startZoom;

    public MeshWireframeDrawable(Mesh mesh)
    {
        if (mesh.m_VertexCount <= 0 || mesh.m_Vertices is null
            || mesh.m_Vertices.Length < checked(mesh.m_VertexCount * 3))
        {
            throw new NotSupportedException("This mesh does not contain decoded vertex positions.");
        }

        _vertices = mesh.m_Vertices;
        _vertexStride = _vertices.Length / mesh.m_VertexCount;
        if (_vertexStride is not (3 or 4))
        {
            throw new NotSupportedException("This mesh uses an unsupported vertex layout.");
        }

        var triangles = new List<(int A, int B, int C)>();
        var firstIndex = 0;
        foreach (var submesh in mesh.m_SubMeshes)
        {
            var indexCount = checked((int)submesh.indexCount);
            for (var index = firstIndex; index + 2 < firstIndex + indexCount; index += 3)
            {
                var a = checked((int)mesh.m_Indices[index] - (int)submesh.firstVertex);
                var b = checked((int)mesh.m_Indices[index + 1] - (int)submesh.firstVertex);
                var c = checked((int)mesh.m_Indices[index + 2] - (int)submesh.firstVertex);
                if ((uint)a >= (uint)mesh.m_VertexCount
                    || (uint)b >= (uint)mesh.m_VertexCount
                    || (uint)c >= (uint)mesh.m_VertexCount)
                {
                    throw new InvalidDataException("The mesh contains an out-of-range triangle index.");
                }
                triangles.Add((a, b, c));
                if (triangles.Count > MaxTriangles)
                {
                    throw new NotSupportedException($"Preview is limited to {MaxTriangles:N0} triangles on mobile.");
                }
            }
            firstIndex = checked(firstIndex + indexCount);
        }

        if (triangles.Count == 0)
        {
            throw new NotSupportedException("This mesh has no triangle faces to preview.");
        }
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
        var projected = new PointF[_vertexStride == 0 ? 0 : _vertices.Length / _vertexStride];
        var cosYaw = MathF.Cos(_yaw);
        var sinYaw = MathF.Sin(_yaw);
        var cosPitch = MathF.Cos(_pitch);
        var sinPitch = MathF.Sin(_pitch);
        for (var i = 0; i < projected.Length; i++)
        {
            var x = _vertices[i * _vertexStride] - centerX;
            var y = _vertices[i * _vertexStride + 1] - centerY;
            var z = _vertices[i * _vertexStride + 2] - centerZ;
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
        for (var i = 0; i < _vertices.Length / _vertexStride; i++)
        {
            var x = _vertices[i * _vertexStride];
            var y = _vertices[i * _vertexStride + 1];
            var z = _vertices[i * _vertexStride + 2];
            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
            minZ = MathF.Min(minZ, z);
            maxZ = MathF.Max(maxZ, z);
        }
        return (minX, maxX, minY, maxY, minZ, maxZ);
    }
}
