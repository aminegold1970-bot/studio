using AssetStudio;

namespace AssetStudioMobile;

/// <summary>Connects persistent mobile files to the original AssetStudio parser.</summary>
public sealed class AssetStudioMobileLoader : IDisposable
{
    private readonly AssetsManager _manager = new();
    private bool _disposed;

    public string? SpecifyUnityVersion { get; set; }

    public async Task LoadFilesAsync(
        IReadOnlyList<FileResult> selected,
        IProgress<MainPage.AssetLoadProgress> progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(selected);
        var paths = selected
            .Where(file => file != null && !string.IsNullOrWhiteSpace(file.FullPath) && File.Exists(file.FullPath))
            .Select(file => file.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length == 0)
            throw new FileNotFoundException("No readable files were selected from the AssetStudio library.");

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new MainPage.AssetLoadProgress(5, $"Preparing {paths.Length} file(s)…"));

        var host = MainPageHost ?? throw new InvalidOperationException("The mobile asset browser is not connected.");
        var snapshot = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _manager.Clear();
            _manager.SpecifyUnityVersion = SpecifyUnityVersion ?? string.Empty;
            _manager.LoadFiles(paths);
            cancellationToken.ThrowIfCancellationRequested();

            if (_manager.assetsFileList.Count == 0)
                throw new InvalidDataException("No Unity asset files were found in the selected file(s).");

            var result = BuildBrowseData(_manager);
            progress?.Report(new MainPage.AssetLoadProgress(
                95, $"Loaded {result.Assets.Count:N0} assets and {result.Hierarchy.Count:N0} scene entries…"));
            return result;
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        await MainThread.InvokeOnMainThreadAsync(() =>
            host(snapshot.Assets, snapshot.Hierarchy, snapshot.Classes));
        progress?.Report(new MainPage.AssetLoadProgress(
            100, $"Loaded {snapshot.Assets.Count:N0} assets, {snapshot.Hierarchy.Count:N0} scene entries, and {snapshot.Classes.Count:N0} classes"));
    }

    public Action<IEnumerable<MainPage.AssetRow>, IEnumerable<MainPage.HierarchyRow>,
        IEnumerable<MainPage.ClassRow>>? MainPageHost { get; set; }

    private static BrowseSnapshot BuildBrowseData(AssetsManager manager)
    {
        var containerByObject = BuildContainerMap(manager);
        var rowsByObject = new Dictionary<AssetStudio.Object, MainPage.AssetRow>();
        var assetRows = new List<MainPage.AssetRow>();
        var objectIndex = 0;

        foreach (var file in manager.assetsFileList)
        {
            foreach (var asset in file.Objects)
            {
                containerByObject.TryGetValue(asset, out var container);
                var row = MainPage.CreateAssetRow(
                    asset,
                    ReadAssetName(asset, objectIndex),
                    container ?? string.Empty,
                    asset.type.ToString(),
                    asset.m_PathID,
                    GetReportedSize(asset),
                    Convert.ToInt32(asset.type),
                    file.fullName,
                    IsExportable(asset));
                rowsByObject.Add(asset, row);
                assetRows.Add(row);
                objectIndex++;
            }
        }

        var hierarchyRows = BuildHierarchy(manager, rowsByObject);
        var classRows = BuildClassRows(manager);
        return new BrowseSnapshot(assetRows, hierarchyRows, classRows);
    }

    private static Dictionary<AssetStudio.Object, string> BuildContainerMap(AssetsManager manager)
    {
        var containers = new Dictionary<AssetStudio.Object, string>();
        foreach (var file in manager.assetsFileList)
        {
            foreach (var asset in file.Objects)
            {
                if (asset is AssetBundle bundle)
                {
                    foreach (var entry in bundle.m_Container)
                    {
                        var start = Math.Clamp(entry.Value.preloadIndex, 0, bundle.m_PreloadTable.Length);
                        var end = (int)Math.Clamp(
                            (long)entry.Value.preloadIndex + entry.Value.preloadSize,
                            start,
                            bundle.m_PreloadTable.Length);
                        for (var index = start; index < end; index++)
                        {
                            if (bundle.m_PreloadTable[index].TryGet(out var contained))
                                containers[contained] = entry.Key;
                        }
                    }
                }
                else if (asset is ResourceManager resourceManager)
                {
                    foreach (var entry in resourceManager.m_Container)
                    {
                        if (entry.Value.TryGet(out var contained))
                            containers[contained] = entry.Key;
                    }
                }
            }
        }

        return containers;
    }

    private static List<MainPage.HierarchyRow> BuildHierarchy(
        AssetsManager manager,
        IReadOnlyDictionary<AssetStudio.Object, MainPage.AssetRow> rowsByObject)
    {
        var rows = new List<MainPage.HierarchyRow>();
        foreach (var file in manager.assetsFileList)
        {
            var gameObjects = file.Objects.OfType<GameObject>().ToList();
            if (gameObjects.Count == 0)
                continue;

            var fileId = Guid.NewGuid().ToString("N");
            var gameObjectIds = gameObjects.ToDictionary(
                gameObject => gameObject,
                _ => Guid.NewGuid().ToString("N"));
            rows.Add(new MainPage.HierarchyRow { Id = fileId, DisplayName = file.fileName });

            foreach (var gameObject in gameObjects)
            {
                var parentId = fileId;
                if (gameObject.m_Transform is { } transform
                    && transform.m_Father.TryGet(out var parentTransform)
                    && parentTransform.m_GameObject.TryGet(out var parentGameObject)
                    && gameObjectIds.TryGetValue(parentGameObject, out var resolvedParentId))
                {
                    parentId = resolvedParentId;
                }

                var gameObjectId = gameObjectIds[gameObject];
                rows.Add(new MainPage.HierarchyRow
                {
                    Id = gameObjectId,
                    ParentId = parentId,
                    DisplayName = string.IsNullOrWhiteSpace(gameObject.m_Name)
                        ? $"GameObject #{gameObject.m_PathID}"
                        : gameObject.m_Name,
                    SourceAsset = gameObject
                });
                if (rowsByObject.TryGetValue(gameObject, out var gameObjectRow))
                    gameObjectRow.HierarchyNodeId = gameObjectId;

                foreach (var componentPointer in gameObject.m_Components)
                {
                    if (!componentPointer.TryGet(out var component))
                        continue;
                    if (rowsByObject.TryGetValue(component, out var componentRow))
                        componentRow.HierarchyNodeId = gameObjectId;

                    AssetStudio.Mesh? mesh = component switch
                    {
                        MeshFilter meshFilter when meshFilter.m_Mesh.TryGet(out var value) => value,
                        SkinnedMeshRenderer skinnedRenderer when skinnedRenderer.m_Mesh.TryGet(out var value) => value,
                        _ => null
                    };
                    if (mesh != null && rowsByObject.TryGetValue(mesh, out var meshRow))
                        meshRow.HierarchyNodeId = gameObjectId;
                }
            }
        }

        return rows;
    }

    private static List<MainPage.ClassRow> BuildClassRows(AssetsManager manager)
    {
        var classesByVersion = new SortedDictionary<string, SortedDictionary<int, MainPage.ClassRow>>(StringComparer.Ordinal);
        foreach (var file in manager.assetsFileList)
        {
            if (!classesByVersion.TryGetValue(file.unityVersion, out var versionClasses))
            {
                versionClasses = new SortedDictionary<int, MainPage.ClassRow>();
                classesByVersion.Add(file.unityVersion, versionClasses);
            }

            foreach (var type in file.m_Types)
            {
                var nodes = type.m_Type?.m_Nodes;
                if (nodes == null)
                    continue;

                var typeId = type.m_ScriptTypeIndex >= 0
                    ? -1 - type.m_ScriptTypeIndex
                    : type.classID;
                versionClasses[typeId] = MainPage.CreateClassRow(
                    typeId,
                    file.unityVersion,
                    nodes.Select(node => new MainPage.TypeTreeNodeRow
                    {
                        Level = node.m_Level,
                        Type = node.m_Type,
                        Name = node.m_Name,
                        ByteSize = node.m_ByteSize,
                        MetaFlag = node.m_MetaFlag
                    }));
            }
        }

        return classesByVersion.Values.SelectMany(classes => classes.Values).ToList();
    }

    private static string ReadAssetName(AssetStudio.Object asset, int index)
    {
        string? name = asset switch
        {
            GameObject gameObject => gameObject.m_Name,
            Texture2D texture => texture.m_Name,
            AudioClip audio => audio.m_Name,
            VideoClip video => video.m_Name,
            Shader shader => shader.m_ParsedForm?.m_Name ?? shader.m_Name,
            Animator animator when animator.m_GameObject.TryGet(out var gameObject) => gameObject.m_Name,
            MonoBehaviour behaviour when string.IsNullOrWhiteSpace(behaviour.m_Name)
                && behaviour.m_Script.TryGet(out var script) => script.m_ClassName,
            MonoBehaviour behaviour => behaviour.m_Name,
            PlayerSettings settings => settings.productName,
            NamedObject named => named.m_Name,
            _ => null
        };

        return string.IsNullOrWhiteSpace(name) ? $"{asset.type} #{index}" : name;
    }

    private static long GetReportedSize(AssetStudio.Object asset)
    {
        long size = asset.byteSize;
        if (asset is Texture2D texture && !string.IsNullOrEmpty(texture.m_StreamData?.path))
            size += texture.m_StreamData.size;
        else if (asset is AudioClip audio && !string.IsNullOrEmpty(audio.m_Source))
            size += audio.m_Size;
        else if (asset is VideoClip video && !string.IsNullOrEmpty(video.m_OriginalPath))
            size += video.m_ExternalResources.m_Size;
        return size;
    }

    private static bool IsExportable(AssetStudio.Object asset) =>
        asset is Texture2D or AudioClip or VideoClip or Shader or Mesh or TextAsset
            or AnimationClip or AssetStudio.Font or MovieTexture or Sprite or Animator or MonoBehaviour;

    private sealed record BrowseSnapshot(
        List<MainPage.AssetRow> Assets,
        List<MainPage.HierarchyRow> Hierarchy,
        List<MainPage.ClassRow> Classes);

    public void Dispose()
    {
        if (_disposed) return;
        _manager.Clear();
        _disposed = true;
    }
}
