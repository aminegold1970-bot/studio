using AssetStudio;

namespace AssetStudioMobile;

/// <summary>Connects persistent mobile files to the original AssetStudio parser.</summary>
public sealed class AssetStudioMobileLoader : IDisposable
{
    private readonly AssetsManager _manager = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _disposed;

    public string? SpecifyUnityVersion { get; set; }

    public async Task LoadFilesAsync(
        IReadOnlyList<FileResult> selected,
        IProgress<MainPage.AssetLoadProgress> progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(selected);
        var selectedPaths = selected.Select(file => file?.FullPath).ToArray();
        if (selectedPaths.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("One or more selected files did not provide a readable path.");

        var paths = selectedPaths
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (paths.Length == 0)
            throw new FileNotFoundException("No readable files were selected from the AssetStudio library.");
        var missingPaths = paths.Where(path => !File.Exists(path)).ToArray();
        if (missingPaths.Length > 0)
            throw new FileNotFoundException($"Selected file(s) are no longer accessible: {string.Join(", ", missingPaths)}");

        await LoadAsync(
            $"Preparing {paths.Length} file(s)…",
            cancellationToken => _manager.LoadFiles(cancellationToken, paths),
            progress,
            cancellationToken);
    }

    public async Task LoadFolderAsync(
        string folderPath,
        IProgress<MainPage.AssetLoadProgress> progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Selected folder was not found: {folderPath}");

        var fullPath = Path.GetFullPath(folderPath);
        await LoadAsync(
            $"Preparing folder {Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath))}…",
            cancellationToken => _manager.LoadFolder(fullPath, cancellationToken),
            progress,
            cancellationToken);
    }

    private async Task LoadAsync(
        string preparingMessage,
        Action<CancellationToken> load,
        IProgress<MainPage.AssetLoadProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new MainPage.AssetLoadProgress(5, preparingMessage));

        var host = MainPageHost ?? throw new InvalidOperationException("The mobile asset browser is not connected.");
        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            BrowseSnapshot snapshot;
            try
            {
                snapshot = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _manager.Clear();
                    _manager.SpecifyUnityVersion = SpecifyUnityVersion ?? string.Empty;

                    var previousProgress = AssetStudio.Progress.Default;
                    var parserPhase = -1;
                    var parserPercent = 5;
                    _manager.LoadingStatus = message =>
                        progress?.Report(new MainPage.AssetLoadProgress(parserPercent, message));
                    AssetStudio.Progress.Default = new InlineProgress<int>(value =>
                    {
                        if (value == 0)
                            parserPhase = Math.Min(parserPhase + 1, 2);

                        var boundedValue = Math.Clamp(value, 0, 100);
                        var (start, end, label) = parserPhase switch
                        {
                            0 => (5, 28, "Importing files"),
                            1 => (28, 76, "Reading asset objects"),
                            _ => (76, 90, "Linking asset relationships")
                        };
                        var percent = start + (end - start) * boundedValue / 100;
                        parserPercent = percent;
                        progress?.Report(new MainPage.AssetLoadProgress(percent, $"{label} · {boundedValue}%"));
                    });

                    try
                    {
                        load(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();

                        if (_manager.assetsFileList.Count == 0)
                            throw new InvalidDataException("No Unity asset files were found in the selected input.");

                        progress?.Report(new MainPage.AssetLoadProgress(90, "Building asset browser…"));
                        var result = BuildBrowseData(_manager, cancellationToken, (percent, message) =>
                            progress?.Report(new MainPage.AssetLoadProgress(percent, message)));
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report(new MainPage.AssetLoadProgress(
                            96, $"Prepared {result.Assets.Count:N0} assets and {result.Hierarchy.Count:N0} scene entries…"));
                        return result;
                    }
                    finally
                    {
                        _manager.LoadingStatus = null;
                        AssetStudio.Progress.Default = previousProgress;
                    }
                }, cancellationToken);
            }
            catch
            {
                await Task.Run(_manager.Clear);
                throw;
            }

            try
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    host(snapshot.Assets, snapshot.Hierarchy, snapshot.Classes, snapshot.Metadata);
                });
            }
            catch (OperationCanceledException)
            {
                await Task.Run(_manager.Clear);
                throw;
            }
            var loadedMessage = $"Finished loading {snapshot.Metadata.FileCount} files with {snapshot.Metadata.ExportableCount} exportable assets";
            if (snapshot.Metadata.FailedReadCount > 0)
                loadedMessage += $" and {snapshot.Metadata.FailedReadCount} assets failed to read";
            progress?.Report(new MainPage.AssetLoadProgress(100, loadedMessage));
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public Action<IEnumerable<MainPage.AssetRow>, IEnumerable<MainPage.HierarchyRow>,
        IEnumerable<MainPage.ClassRow>, MainPage.LoadMetadata>? MainPageHost { get; set; }

    private static BrowseSnapshot BuildBrowseData(
        AssetsManager manager,
        CancellationToken cancellationToken,
        Action<int, string> reportProgress)
    {
        var totalAssets = manager.assetsFileList.Sum(file => file.Objects.Count);
        var containerByObject = BuildContainerMap(
            manager,
            cancellationToken,
            (completed, total) => ReportBrowseProgress(reportProgress, 90, 92, completed, total, "Mapping containers"));
        var rowsByObject = new Dictionary<AssetStudio.Object, MainPage.AssetRow>();
        var assetRows = new List<MainPage.AssetRow>();
        var objectIndex = 0;
        var processedAssets = 0;
        var exportableCount = 0;

        foreach (var file in manager.assetsFileList)
        {
            foreach (var asset in file.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                containerByObject.TryGetValue(asset, out var container);
                var exportable = IsExportable(asset);
                if (exportable) exportableCount++;
                var row = MainPage.CreateAssetRow(
                    asset,
                    ReadAssetName(asset, objectIndex),
                    container ?? string.Empty,
                    asset.type.ToString(),
                    asset.m_PathID,
                    GetReportedSize(asset),
                    Convert.ToInt32(asset.type),
                    file.fullName,
                    exportable);
                rowsByObject.Add(asset, row);
                assetRows.Add(row);
                objectIndex++;
                ReportBrowseProgress(reportProgress, 92, 94, ++processedAssets, totalAssets, "Building asset rows");
            }
        }

        var hierarchyRows = BuildHierarchy(manager, rowsByObject, cancellationToken, (completed, total) =>
            ReportBrowseProgress(reportProgress, 94, 95, completed, total, "Building tree structure..."));
        var classRows = BuildClassRows(manager, cancellationToken, (completed, total) =>
            ReportBrowseProgress(reportProgress, 95, 96, completed, total, "Building class browser"));

        // Desktop title/load status metadata comes from the first serialized
        // file, PlayerSettings.productName, and the parsed/exportable counts.
        var firstFile = manager.assetsFileList[0];
        var productName = manager.assetsFileList
            .SelectMany(file => file.Objects)
            .OfType<PlayerSettings>()
            .Select(settings => settings.productName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? string.Empty;
        var failedReadCount = manager.assetsFileList.Sum(file =>
            Math.Max(0, file.m_Objects.Count - file.Objects.Count));
        var metadata = new MainPage.LoadMetadata(
            productName,
            firstFile.unityVersion,
            firstFile.m_TargetPlatform.ToString(),
            manager.assetsFileList.Count,
            exportableCount,
            failedReadCount);
        return new BrowseSnapshot(assetRows, hierarchyRows, classRows, metadata);
    }

    private static Dictionary<AssetStudio.Object, string> BuildContainerMap(
        AssetsManager manager,
        CancellationToken cancellationToken,
        Action<int, int> reportProgress)
    {
        var containers = new Dictionary<AssetStudio.Object, string>();
        var totalAssets = manager.assetsFileList.Sum(file => file.Objects.Count);
        var processedAssets = 0;
        foreach (var file in manager.assetsFileList)
        {
            foreach (var asset in file.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                            cancellationToken.ThrowIfCancellationRequested();
                            if (bundle.m_PreloadTable[index].TryGet(out var contained))
                                containers[contained] = entry.Key;
                        }
                    }
                }
                else if (asset is ResourceManager resourceManager)
                {
                    foreach (var entry in resourceManager.m_Container)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entry.Value.TryGet(out var contained))
                            containers[contained] = entry.Key;
                    }
                }
                reportProgress(++processedAssets, totalAssets);
            }
        }

        return containers;
    }

    private static List<MainPage.HierarchyRow> BuildHierarchy(
        AssetsManager manager,
        IReadOnlyDictionary<AssetStudio.Object, MainPage.AssetRow> rowsByObject,
        CancellationToken cancellationToken,
        Action<int, int> reportProgress)
    {
        // The original GUI builds one global GameObject dictionary before it
        // resolves parents, so a transform may point at an object in another
        // serialized file. Collect IDs first, then create rows in file order.
        var fileEntries = new List<(int Index, string FileName, List<GameObject> GameObjects)>();
        var gameObjectIds = new Dictionary<GameObject, string>();
        var processedFiles = 0;
        var totalFiles = manager.assetsFileList.Count;
        for (var fileIndex = 0; fileIndex < totalFiles; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = manager.assetsFileList[fileIndex];
            var gameObjects = new List<GameObject>();
            foreach (var asset in file.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (asset is GameObject gameObject)
                    gameObjects.Add(gameObject);
            }
            fileEntries.Add((fileIndex, file.fileName, gameObjects));
            foreach (var gameObject in gameObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Stable IDs allow selection/search state to survive reloads of
                // the same file set. PathIDs are only unique within a file.
                gameObjectIds.TryAdd(gameObject, $"go:{fileIndex}:{gameObject.m_PathID}");
            }
            reportProgress(++processedFiles, totalFiles);
        }

        var rows = new List<MainPage.HierarchyRow>();
        foreach (var entry in fileEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileId = $"file:{entry.Index}:{entry.FileName}";
            var fileRows = new List<MainPage.HierarchyRow>();
            foreach (var gameObject in entry.GameObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parentId = fileId;
                if (gameObject.m_Transform is { } transform
                    && transform.m_Father.TryGet(out var parentTransform)
                    && parentTransform.m_GameObject.TryGet(out var parentGameObject)
                    && gameObjectIds.TryGetValue(parentGameObject, out var resolvedParentId))
                {
                    parentId = resolvedParentId;
                }

                var gameObjectId = gameObjectIds[gameObject];
                fileRows.Add(new MainPage.HierarchyRow
                {
                    Id = gameObjectId,
                    ParentId = parentId,
                    // Original GameObjectTreeNode.Text is the raw m_Name.
                    DisplayName = gameObject.m_Name ?? string.Empty,
                    SourceAsset = gameObject
                });
                if (rowsByObject.TryGetValue(gameObject, out var gameObjectRow))
                    gameObjectRow.HierarchyNodeId = gameObjectId;

                foreach (var componentPointer in gameObject.m_Components)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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

            // Original adds a file root only when it actually has child objects.
            if (fileRows.Any(row => string.Equals(row.ParentId, fileId, StringComparison.Ordinal)))
                rows.Add(new MainPage.HierarchyRow { Id = fileId, DisplayName = entry.FileName });
            rows.AddRange(fileRows);
        }

        return rows;
    }

    private static List<MainPage.ClassRow> BuildClassRows(
        AssetsManager manager,
        CancellationToken cancellationToken,
        Action<int, int> reportProgress)
    {
        var classesByVersion = new SortedDictionary<string, SortedDictionary<int, MainPage.ClassRow>>(StringComparer.Ordinal);
        var totalTypes = manager.assetsFileList.Sum(file => file.m_Types.Count);
        var processedTypes = 0;
        foreach (var file in manager.assetsFileList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!classesByVersion.TryGetValue(file.unityVersion, out var versionClasses))
            {
                versionClasses = new SortedDictionary<int, MainPage.ClassRow>();
                classesByVersion.Add(file.unityVersion, versionClasses);
            }

            foreach (var type in file.m_Types)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var nodes = type.m_Type?.m_Nodes;
                if (nodes == null)
                {
                    reportProgress(++processedTypes, totalTypes);
                    continue;
                }

                var typeId = type.m_ScriptTypeIndex >= 0
                    ? -1 - type.m_ScriptTypeIndex
                    : type.classID;
                versionClasses[typeId] = MainPage.CreateClassRow(
                    typeId,
                    file.unityVersion,
                    nodes.Select(node =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return new MainPage.TypeTreeNodeRow
                        {
                            Level = node.m_Level,
                            Type = node.m_Type,
                            Name = node.m_Name,
                            ByteSize = node.m_ByteSize,
                            MetaFlag = node.m_MetaFlag
                        };
                    }));
                reportProgress(++processedTypes, totalTypes);
            }
        }

        var rows = new List<MainPage.ClassRow>();
        foreach (var versionClasses in classesByVersion.Values)
        {
            foreach (var classRow in versionClasses.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(classRow);
            }
        }

        return rows;
    }

    private static void ReportBrowseProgress(
        Action<int, string> reportProgress,
        int start,
        int end,
        int completed,
        int total,
        string phase)
    {
        if (total <= 0)
        {
            reportProgress(end, $"{phase} · nothing to process");
            return;
        }

        var interval = Math.Max(1, total / 40);
        if (completed != total && completed % interval != 0)
            return;

        var percent = start + (int)((long)(end - start) * completed / total);
        reportProgress(percent, $"{phase} · {completed:N0}/{total:N0}");
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report) => _report = report;

        public void Report(T value) => _report(value);
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
        List<MainPage.ClassRow> Classes,
        MainPage.LoadMetadata Metadata);

    public void Dispose()
    {
        if (_disposed) return;
        if (!_loadGate.Wait(0))
            throw new InvalidOperationException("Cannot dispose the asset loader while a load is in progress.");
        try
        {
            _manager.Clear();
        }
        finally
        {
            _disposed = true;
            _loadGate.Release();
            _loadGate.Dispose();
        }
    }
}
