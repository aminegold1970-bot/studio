using AssetStudio;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;
using MauiColor = Microsoft.Maui.Graphics.Color;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsVector3 = System.Numerics.Vector3;

namespace AssetStudioMobile;

internal sealed class MeshPreviewPage : ContentPage
{
    private readonly Mesh? _mesh;
    private readonly MeshPreviewDrawable _drawable;
    private readonly GraphicsView _graphicsView;
    private readonly Button _modeButton;
    private readonly Button _shadeButton;
    private readonly Button _normalsButton;
    private readonly Button _interactionButton;
    private readonly Button _resetButton;
    private readonly Label _instructions;

    public MeshPreviewPage(Mesh mesh)
        : this(mesh?.m_Name ?? "Mesh Preview", new MeshPreviewDrawable(mesh))
    {
        _mesh = mesh;
        if (mesh != null)
            ToolbarItems.Add(new ToolbarItem(
                "Export selected assets", null, () => ExportMeshAsync(mesh)));
    }

    internal MeshPreviewPage(string title, MeshPreviewDrawable drawable)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "Mesh Preview" : title;
        _drawable = drawable ?? throw new ArgumentNullException(nameof(drawable));
        _graphicsView = new GraphicsView
        {
            Drawable = _drawable,
            BackgroundColor = MeshPreviewDrawable.PreviewBackground
        };

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPanUpdated;
        _graphicsView.GestureRecognizers.Add(pan);

        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += OnPinchUpdated;
        _graphicsView.GestureRecognizers.Add(pinch);

        ToolbarItems.Add(new ToolbarItem("Done", null, async () => await Navigation.PopModalAsync()));
        _modeButton = CreateControlButton(_drawable.ModeLabel, OnRenderModeClicked);
        _shadeButton = CreateControlButton(_drawable.ShadeLabel, OnShadeClicked);
        _normalsButton = CreateControlButton(_drawable.NormalsLabel, OnNormalsClicked);
        _interactionButton = CreateControlButton(_drawable.InteractionLabel, OnInteractionClicked);
        _resetButton = CreateControlButton("Reset view", OnResetClicked);

        var controls = new FlexLayout
        {
            Direction = FlexDirection.Row,
            Wrap = FlexWrap.Wrap,
            AlignItems = FlexAlignItems.Start,
            JustifyContent = FlexJustify.Start,
            Padding = new Thickness(2, 4)
        };
        controls.Children.Add(_modeButton);
        controls.Children.Add(_shadeButton);
        controls.Children.Add(_normalsButton);
        controls.Children.Add(_interactionButton);
        controls.Children.Add(_resetButton);

        _instructions = new Label
        {
            TextColor = MauiColor.FromArgb("#555555"),
            FontSize = 11,
            HorizontalTextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 2)
        };
        UpdateControls();

        var grid = new Grid
        {
            Padding = 4,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Star },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        grid.Add(_graphicsView);
        grid.Add(controls, 0, 1);
        grid.Add(_instructions, 0, 2);
        Content = grid;
    }

    private static Button CreateControlButton(string text, EventHandler handler)
    {
        var button = new Button
        {
            Text = text,
            FontSize = 11,
            Padding = new Thickness(6, 2),
            Margin = new Thickness(0, 0, 4, 4),
            HeightRequest = 32,
            MinimumHeightRequest = 32,
            MinimumWidthRequest = 0
        };
        button.Clicked += handler;
        return button;
    }

    private void UpdateControls()
    {
        _modeButton.Text = _drawable.ModeLabel;
        _shadeButton.Text = _drawable.ShadeLabel;
        _normalsButton.Text = _drawable.NormalsLabel;
        _interactionButton.Text = _drawable.InteractionLabel;
        _instructions.Text = _drawable.HelpText;
    }

    private void OnRenderModeClicked(object? sender, EventArgs e)
    {
        _drawable.ToggleRenderMode();
        UpdateControls();
        _graphicsView.Invalidate();
    }

    private void OnShadeClicked(object? sender, EventArgs e)
    {
        _drawable.ToggleShading();
        UpdateControls();
        _graphicsView.Invalidate();
    }

    private void OnNormalsClicked(object? sender, EventArgs e)
    {
        _drawable.ToggleNormals();
        UpdateControls();
        _graphicsView.Invalidate();
    }

    private void OnInteractionClicked(object? sender, EventArgs e)
    {
        _drawable.TogglePanMode();
        UpdateControls();
        _graphicsView.Invalidate();
    }

    private void OnResetClicked(object? sender, EventArgs e)
    {
        _drawable.ResetView();
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

    private async Task ExportMeshAsync(Mesh mesh)
    {
        try
        {
            var result = await Task.Run(
                () => MobileAssetExporter.ExportForShare(mesh, MobileExportKind.Converted));
            await Share.Default.RequestAsync(new ShareFileRequest(
                $"{mesh.m_Name} (obj)", new ShareFile(result.FilePath)));
        }
        catch (Exception exception)
        {
            await DisplayAlert("Export failed", exception.Message, "OK");
        }
    }
}

internal sealed class MeshPreviewDrawable : IDrawable
{
    private const int MaxTriangles = 50_000;
    internal static readonly MauiColor PreviewBackground = MauiColor.FromArgb("#5F9EA0");
    private static readonly MauiColor DefaultMaterialColor = MauiColor.FromArgb("#8EA7C1");

    private readonly NumericsVector3[] _vertices;
    private readonly MeshTriangle[] _triangles;
    private readonly NumericsVector3[] _calculatedNormals;
    private readonly NumericsVector3[] _sourceNormals;
    private readonly bool _hasSourceNormals;
    private readonly MeshBounds _bounds;
    private readonly int _submeshCount;

    private MeshRenderMode _renderMode;
    private MeshShadeMode _shadeMode = MeshShadeMode.Lit;
    private MeshNormalMode _normalMode;
    private float _yaw = -MathF.PI / 4f;
    private float _pitch = -MathF.PI / 6f;
    private float _zoom = 1f;
    private float _startYaw;
    private float _startPitch;
    private float _startZoom;
    private float _panX;
    private float _panY;
    private float _startPanX;
    private float _startPanY;

    public bool IsPanMode { get; private set; }

    public string ModeLabel => _renderMode switch
    {
        MeshRenderMode.Wireframe => "Wireframe",
        MeshRenderMode.SolidAndWireframe => "Solid + wireframe",
        _ => "Solid"
    };

    public string ShadeLabel => _shadeMode == MeshShadeMode.Lit ? "Shade: Lit" : "Shade: Color";

    public string NormalsLabel => _normalMode switch
    {
        MeshNormalMode.Calculated => "Normals: Calc",
        MeshNormalMode.Source when _hasSourceNormals => "Normals: Source",
        _ => "Normals: Off"
    };

    public string InteractionLabel => IsPanMode ? "Pan" : "Rotate";
    public string InteractionLanguage => IsPanMode ? "pan" : "rotate";

    public string Statistics =>
        $"{_vertices.Length:N0} vertices • {_triangles.Length:N0} triangles • " +
        $"{_bounds.SizeX:N2} x {_bounds.SizeY:N2} x {_bounds.SizeZ:N2}";

    public int VertexCount => _vertices.Length;
    public int TriangleCount => _triangles.Length;
    public int SubmeshCount => _submeshCount;

    public string HelpText =>
        $"Mesh · {VertexCount:N0} vertices · {TriangleCount:N0} triangles · {SubmeshCount:N0} submeshes\n" +
        $"Drag={InteractionLanguage} | Pan mode=Move | Pinch=Zoom\n" +
        "W=Wireframe | S=Shade | N=ReNormal";

    public void ToggleRenderMode() =>
        _renderMode = (MeshRenderMode)(((int)_renderMode + 1) % 3);

    public void ToggleShading() =>
        _shadeMode = _shadeMode == MeshShadeMode.Lit ? MeshShadeMode.Color : MeshShadeMode.Lit;

    public void ToggleNormals()
    {
        _normalMode = _normalMode switch
        {
            MeshNormalMode.Off => MeshNormalMode.Calculated,
            MeshNormalMode.Calculated when _hasSourceNormals => MeshNormalMode.Source,
            _ => MeshNormalMode.Off
        };
    }

    public void TogglePanMode()
    {
        IsPanMode = !IsPanMode;
        _startPanX = _panX;
        _startPanY = _panY;
    }

    public void ResetView()
    {
        _yaw = -MathF.PI / 4f;
        _pitch = -MathF.PI / 6f;
        _zoom = 1f;
        _panX = 0f;
        _panY = 0f;
    }

    private readonly record struct MeshTriangle(int A, int B, int C, MauiColor Color);
    private readonly record struct ProjectedVertex(PointF Point, NumericsVector3 ViewPosition);
    private readonly record struct RenderTriangle(MeshTriangle Triangle, float Depth);
    private readonly record struct MeshBounds(
        float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ)
    {
        public float SizeX => MaxX - MinX;
        public float SizeY => MaxY - MinY;
        public float SizeZ => MaxZ - MinZ;
    }

    private enum MeshRenderMode
    {
        Solid,
        Wireframe,
        SolidAndWireframe
    }

    private enum MeshShadeMode
    {
        Lit,
        Color
    }

    private enum MeshNormalMode
    {
        Off,
        Calculated,
        Source
    }

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
        var sourceNormals = new List<NumericsVector3>();
        var triangles = new List<MeshTriangle>();
        var foundSourceNormals = false;
        var submeshCount = 0;
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

            var normals = mesh.m_Normals ?? Array.Empty<float>();
            var normalStride = 0;
            if (normals.Length > 0)
            {
                if (normals.Length % mesh.m_VertexCount != 0
                    || normals.Length / mesh.m_VertexCount is not (3 or 4))
                {
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' has an unsupported normal layout.");
                }
                normalStride = normals.Length / mesh.m_VertexCount;
            }

            if (geometry.BlendShapePositionDeltas is { } blendShapeDeltas
                && blendShapeDeltas.Length < mesh.m_VertexCount)
            {
                throw new InvalidDataException($"Mesh '{mesh.m_Name}' has incomplete blend-shape position data.");
            }
            if (geometry.BlendShapeNormalDeltas is { } blendShapeNormalDeltas
                && blendShapeNormalDeltas.Length < mesh.m_VertexCount)
            {
                throw new InvalidDataException($"Mesh '{mesh.m_Name}' has incomplete blend-shape normal data.");
            }

            var worldNormalTransform = NumericsMatrix4x4.Identity;
            var inverseWorldTransform = NumericsMatrix4x4.Identity;
            if (applySceneTransforms
                && (!NumericsMatrix4x4.Invert(geometry.WorldTransform, out inverseWorldTransform)
                    || !IsFinite(inverseWorldTransform)))
            {
                throw new InvalidDataException(
                    $"Mesh '{mesh.m_Name}' has a singular or non-finite world transform.");
            }
            if (applySceneTransforms)
                worldNormalTransform = NumericsMatrix4x4.Transpose(inverseWorldTransform);

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
                var normal = normalStride == 0
                    ? NumericsVector3.UnitY
                    : new NumericsVector3(
                        normals[vertexIndex * normalStride],
                        normals[vertexIndex * normalStride + 1],
                        normals[vertexIndex * normalStride + 2]);
                if (applySceneTransforms)
                {
                    position.X = -position.X;
                    normal.X = -normal.X;
                    if (geometry.BlendShapePositionDeltas is { } deltas)
                        position += deltas[vertexIndex];
                    if (geometry.BlendShapeNormalDeltas is { } normalDeltas)
                        normal += normalDeltas[vertexIndex];
                    position = MobileAssetExporter.TransformVertexPosition(
                        mesh, vertexIndex, position, geometry.WorldTransform, geometry.SkinTransforms);
                    normal = MobileAssetExporter.TransformVertexNormal(
                        mesh, vertexIndex, normal, worldNormalTransform, geometry.SkinNormalTransforms);
                }

                if (!IsFinite(position))
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' contains a non-finite preview vertex.");
                if (!IsFinite(normal))
                    normal = NumericsVector3.UnitY;
                vertices.Add(position);
                sourceNormals.Add(normal);
                foundSourceNormals |= normalStride > 0;
            }

            var firstIndex = 0;
            submeshCount += mesh.m_SubMeshes.Length;
            for (var submeshIndex = 0; submeshIndex < mesh.m_SubMeshes.Length; submeshIndex++)
            {
                var submesh = mesh.m_SubMeshes[submeshIndex];
                if (submesh.indexCount > int.MaxValue)
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' has an oversized triangle index range.");
                var indexCount = (int)submesh.indexCount;
                if (indexCount % 3 != 0
                    || firstIndex > mesh.m_Indices.Count
                    || indexCount > mesh.m_Indices.Count - firstIndex)
                {
                    throw new InvalidDataException($"Mesh '{mesh.m_Name}' has an invalid triangle index range.");
                }
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
        _sourceNormals = NormalizeVertexNormals(sourceNormals.ToArray());
        _hasSourceNormals = foundSourceNormals;
        _calculatedNormals = CalculateVertexNormals(_vertices, _triangles);
        _bounds = CalculateBounds(_vertices);
        _submeshCount = submeshCount;
    }

    private static bool IsFinite(NumericsVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(NumericsMatrix4x4 matrix) =>
        float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12)
        && float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14)
        && float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22)
        && float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24)
        && float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32)
        && float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34)
        && float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42)
        && float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

    private static NumericsVector3[] CalculateVertexNormals(
        NumericsVector3[] vertices,
        MeshTriangle[] triangles)
    {
        var accumulated = new NumericsVector3[vertices.Length];
        foreach (var triangle in triangles)
        {
            var normal = NumericsVector3.Cross(
                vertices[triangle.B] - vertices[triangle.A],
                vertices[triangle.C] - vertices[triangle.A]);
            if (!IsFinite(normal))
                continue;
            accumulated[triangle.A] += normal;
            accumulated[triangle.B] += normal;
            accumulated[triangle.C] += normal;
        }
        return NormalizeVertexNormals(accumulated);
    }

    private static NumericsVector3[] NormalizeVertexNormals(NumericsVector3[] normals)
    {
        var result = new NumericsVector3[normals.Length];
        for (var index = 0; index < normals.Length; index++)
        {
            var normal = normals[index];
            var squared = normal.LengthSquared();
            result[index] = float.IsFinite(squared) && squared > 1e-12f
                ? normal / MathF.Sqrt(squared)
                : NumericsVector3.UnitY;
        }
        return result;
    }

    private static MeshBounds CalculateBounds(NumericsVector3[] vertices)
    {
        var minX = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var minY = float.PositiveInfinity;
        var maxY = float.NegativeInfinity;
        var minZ = float.PositiveInfinity;
        var maxZ = float.NegativeInfinity;
        foreach (var vertex in vertices)
        {
            minX = MathF.Min(minX, vertex.X);
            maxX = MathF.Max(maxX, vertex.X);
            minY = MathF.Min(minY, vertex.Y);
            maxY = MathF.Max(maxY, vertex.Y);
            minZ = MathF.Min(minZ, vertex.Z);
            maxZ = MathF.Max(maxZ, vertex.Z);
        }
        return new MeshBounds(minX, maxX, minY, maxY, minZ, maxZ);
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
                return new MauiColor(
                    SanitizeColorComponent(color.R, 0f),
                    SanitizeColorComponent(color.G, 0f),
                    SanitizeColorComponent(color.B, 0f),
                    SanitizeColorComponent(color.A, 1f));
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
        // Unity can contain NaN/Infinity colors in imported meshes. Clamp them
        // for preview instead of rejecting the whole mesh.
        return SanitizeColorComponent(value, channel == 3 ? 1f : 0f);
    }

    private static float SanitizeColorComponent(float value, float fallback)
    {
        if (!float.IsFinite(value))
            return fallback;
        return Math.Clamp(value, 0, 1);
    }

    public void UpdatePan(PanUpdatedEventArgs e)
    {
        if (e.StatusType == GestureStatus.Started)
        {
            _startYaw = _yaw;
            _startPitch = _pitch;
            _startPanX = _panX;
            _startPanY = _panY;
        }
        else if (e.StatusType == GestureStatus.Running)
        {
            if (IsPanMode)
            {
                _panX = _startPanX + (float)e.TotalX;
                _panY = _startPanY + (float)e.TotalY;
            }
            else
            {
                _yaw = _startYaw + (float)e.TotalX / 180f;
                _pitch = Math.Clamp(_startPitch + (float)e.TotalY / 180f, -1.5f, 1.5f);
            }
        }
        else if (e.StatusType == GestureStatus.Completed)
        {
            _startPanX = _panX;
            _startPanY = _panY;
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
        canvas.FillColor = PreviewBackground;
        canvas.FillRectangle(dirtyRect);

        var centerX = (_bounds.MinX + _bounds.MaxX) * 0.5f;
        var centerY = (_bounds.MinY + _bounds.MaxY) * 0.5f;
        var centerZ = (_bounds.MinZ + _bounds.MaxZ) * 0.5f;
        var extent = MathF.Max(
            1e-5f,
            MathF.Sqrt(
                _bounds.SizeX * _bounds.SizeX
                + _bounds.SizeY * _bounds.SizeY
                + _bounds.SizeZ * _bounds.SizeZ));
        if (!float.IsFinite(extent))
            return;

        var scale = MathF.Min(dirtyRect.Width, dirtyRect.Height) * 0.9f * _zoom / extent;
        var projected = new ProjectedVertex[_vertices.Length];
        var cosYaw = MathF.Cos(_yaw);
        var sinYaw = MathF.Sin(_yaw);
        var cosPitch = MathF.Cos(_pitch);
        var sinPitch = MathF.Sin(_pitch);
        for (var index = 0; index < projected.Length; index++)
        {
            var vertex = _vertices[index];
            var x = vertex.X - centerX;
            var y = vertex.Y - centerY;
            var z = vertex.Z - centerZ;
            var rotatedX = x * cosYaw - z * sinYaw;
            var rotatedZ = x * sinYaw + z * cosYaw;
            var rotatedY = y * cosPitch - rotatedZ * sinPitch;
            var viewDepth = y * sinPitch + rotatedZ * cosPitch;
            projected[index] = new ProjectedVertex(
                new PointF(
                    dirtyRect.Center.X + rotatedX * scale + _panX,
                    dirtyRect.Center.Y - rotatedY * scale + _panY),
                new NumericsVector3(rotatedX, rotatedY, viewDepth));
        }

        if (_renderMode != MeshRenderMode.Wireframe)
        {
            var shadeNormals = _normalMode == MeshNormalMode.Source && _hasSourceNormals
                ? _sourceNormals
                : _calculatedNormals;
            DrawSolid(canvas, projected, shadeNormals);
        }

        if (_renderMode != MeshRenderMode.Solid)
            DrawWireframe(canvas, projected);

        if (_normalMode != MeshNormalMode.Off)
        {
            var displayNormals = _normalMode == MeshNormalMode.Source && _hasSourceNormals
                ? _sourceNormals
                : _calculatedNormals;
            DrawNormalLines(
                canvas, projected, displayNormals, scale, cosYaw, sinYaw, cosPitch, sinPitch);
        }
    }

    private void DrawSolid(
        ICanvas canvas,
        ProjectedVertex[] projected,
        NumericsVector3[] shadeNormals)
    {
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

        foreach (var rendered in orderedTriangles)
        {
            var triangle = rendered.Triangle;
            var a = projected[triangle.A];
            var b = projected[triangle.B];
            var c = projected[triangle.C];
            var normal = (shadeNormals[triangle.A]
                + shadeNormals[triangle.B]
                + shadeNormals[triangle.C]) / 3;
            canvas.FillColor = _shadeMode == MeshShadeMode.Lit
                ? GetDesktopLitColor(normal)
                : new MauiColor(
                    triangle.Color.Red,
                    triangle.Color.Green,
                    triangle.Color.Blue,
                    triangle.Color.Alpha);
            var face = new PathF();
            face.MoveTo(a.Point);
            face.LineTo(b.Point);
            face.LineTo(c.Point);
            face.Close();
            canvas.FillPath(face);
        }
    }

    private static MauiColor GetDesktopLitColor(NumericsVector3 normal)
    {
        var squared = normal.LengthSquared();
        var unit = float.IsFinite(squared) && squared > 1e-12f
            ? normal / MathF.Sqrt(squared)
            : NumericsVector3.UnitY;
        var nDot = Math.Clamp((unit.X + unit.Z) * 0.707f, 0f, 1f);
        var weightYSqrt = 0.5f + 0.5f * unit.Y;
        var weightXSqrt = 0.5f - 0.5f * unit.Y;
        var weightY = weightYSqrt * weightYSqrt;
        var weightX = weightXSqrt * weightXSqrt;

        var red = nDot / MathF.PI + 0.779f * weightY + 0.368f * weightX;
        var green = nDot * 0.957f / MathF.PI + 0.716f * weightY + 0.477f * weightX;
        var blue = nDot * 0.839f / MathF.PI + 0.453f * weightY + 0.735f * weightX;
        return new MauiColor(
            MathF.Sqrt(Math.Clamp(red, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(green, 0f, 1f)),
            MathF.Sqrt(Math.Clamp(blue, 0f, 1f)),
            1f);
    }

    private void DrawWireframe(ICanvas canvas, ProjectedVertex[] projected)
    {
        canvas.StrokeColor = MauiColor.FromArgb("#000000");
        canvas.StrokeSize = 1f;
        foreach (var triangle in _triangles)
        {
            canvas.DrawLine(projected[triangle.A].Point, projected[triangle.B].Point);
            canvas.DrawLine(projected[triangle.B].Point, projected[triangle.C].Point);
            canvas.DrawLine(projected[triangle.C].Point, projected[triangle.A].Point);
        }
    }

    private void DrawNormalLines(
        ICanvas canvas,
        ProjectedVertex[] projected,
        NumericsVector3[] normals,
        float scale,
        float cosYaw,
        float sinYaw,
        float cosPitch,
        float sinPitch)
    {
        canvas.StrokeColor = MauiColor.FromArgb("#D64A3A");
        canvas.StrokeSize = 1f;
        var normalLength = MathF.Max(8f, 0.06f * MathF.Abs(scale) * _zoom);
        var stride = Math.Max(1, _triangles.Length / 2000);
        for (var index = 0; index < _triangles.Length; index += stride)
        {
            var triangle = _triangles[index];
            var a = projected[triangle.A];
            var b = projected[triangle.B];
            var c = projected[triangle.C];
            var normal = (normals[triangle.A]
                + normals[triangle.B]
                + normals[triangle.C]) / 3;
            normal = RotateDirection(normal, cosYaw, sinYaw, cosPitch, sinPitch);
            var squared = normal.LengthSquared();
            if (!float.IsFinite(squared) || squared <= 1e-12f)
                continue;
            normal /= MathF.Sqrt(squared);
            var center = new PointF(
                (a.Point.X + b.Point.X + c.Point.X) / 3f,
                (a.Point.Y + b.Point.Y + c.Point.Y) / 3f);
            var end = new PointF(
                center.X + normal.X * normalLength,
                center.Y - normal.Y * normalLength);
            canvas.DrawLine(center, end);
        }
    }

    private static NumericsVector3 RotateDirection(
        NumericsVector3 normal,
        float cosYaw,
        float sinYaw,
        float cosPitch,
        float sinPitch)
    {
        var rotatedX = normal.X * cosYaw - normal.Z * sinYaw;
        var rotatedZ = normal.X * sinYaw + normal.Z * cosYaw;
        var rotatedY = normal.Y * cosPitch - rotatedZ * sinPitch;
        return new NumericsVector3(rotatedX, rotatedY, rotatedZ);
    }
}
