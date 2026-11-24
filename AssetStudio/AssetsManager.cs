using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using static AssetStudio.ImportHelper;

namespace AssetStudio
{
    public class AssetsManager
    {
        public string SpecifyUnityVersion;
        public List<SerializedFile> assetsFileList = new List<SerializedFile>();
        public Action<string> LoadingStatus { get; set; }

        internal Dictionary<string, int> assetsFileIndexCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, BinaryReader> resourceFileReaders = new Dictionary<string, BinaryReader>(StringComparer.OrdinalIgnoreCase);

        private List<string> importFiles = new List<string>();
        private HashSet<string> importFilesHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> noexistFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> assetsFileListHash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public void LoadFiles(params string[] files)
        {
            LoadFiles(CancellationToken.None, files);
        }

        public void LoadFiles(CancellationToken cancellationToken, params string[] files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetDirectoryName(Path.GetFullPath(files[0]));
            LoadingStatus?.Invoke($"Scanning for split files near {Path.GetFileName(path)}…");
            MergeSplitAssets(path, false, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var toReadFile = ProcessingSplitFiles(files.ToList(), cancellationToken);
            Load(toReadFile, cancellationToken);
        }

        public void LoadFolder(string path)
        {
            LoadFolder(path, CancellationToken.None);
        }

        public void LoadFolder(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadingStatus?.Invoke("Scanning selected folder for split files…");
            MergeSplitAssets(path, true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var files = new List<string>();
            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(file);
                if (files.Count % 128 == 0)
                    LoadingStatus?.Invoke($"Scanning folder · {files.Count:N0} files found");
            }
            var toReadFile = ProcessingSplitFiles(files, cancellationToken);
            Load(toReadFile, cancellationToken);
        }

        private void Load(string[] files, CancellationToken cancellationToken)
        {
            try
            {
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    importFiles.Add(file);
                    importFilesHash.Add(Path.GetFileName(file));
                }

                Progress.Reset();
                //use a for loop because list size can change
                for (var i = 0; i < importFiles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LoadingStatus?.Invoke(
                        $"Importing file {i + 1:N0} · {Path.GetFileName(importFiles[i])}");
                    LoadFile(importFiles[i], cancellationToken);
                    Progress.Report(i + 1, importFiles.Count);
                }

                importFiles.Clear();
                importFilesHash.Clear();
                noexistFiles.Clear();
                assetsFileListHash.Clear();

                ReadAssets(cancellationToken);
                ProcessAssets(cancellationToken);
            }
            finally
            {
                importFiles.Clear();
                importFilesHash.Clear();
                noexistFiles.Clear();
                assetsFileListHash.Clear();
            }
        }

        private void LoadFile(string fullName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reader = new FileReader(fullName);
            LoadFile(reader, cancellationToken);
        }

        private void LoadFile(FileReader reader, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (reader.FileType)
            {
                case FileType.AssetsFile:
                    LoadAssetsFile(reader, cancellationToken);
                    break;
                case FileType.BundleFile:
                    LoadBundleFile(reader, cancellationToken);
                    break;
                case FileType.WebFile:
                    LoadWebFile(reader, cancellationToken);
                    break;
                case FileType.GZipFile:
                    LoadFile(DecompressGZip(reader, cancellationToken), cancellationToken);
                    break;
                case FileType.BrotliFile:
                    LoadFile(DecompressBrotli(reader, cancellationToken), cancellationToken);
                    break;
                case FileType.ZipFile:
                    LoadZipFile(reader, cancellationToken);
                    break;
            }
        }

        private void LoadAssetsFile(FileReader reader, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!assetsFileListHash.Contains(reader.FileName))
            {
                Logger.Info($"Loading {reader.FullPath}");
                LoadingStatus?.Invoke($"Reading serialized metadata · {reader.FileName}");
                try
                {
                    var assetsFile = new SerializedFile(reader, this, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    CheckStrippedVersion(assetsFile);
                    assetsFileList.Add(assetsFile);
                    assetsFileListHash.Add(assetsFile.fileName);

                    foreach (var sharedFile in assetsFile.m_Externals)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sharedFileName = sharedFile.fileName;

                        if (!importFilesHash.Contains(sharedFileName))
                        {
                            var sharedFilePath = Path.Combine(Path.GetDirectoryName(reader.FullPath), sharedFileName);
                            if (!noexistFiles.Contains(sharedFilePath))
                            {
                                if (!File.Exists(sharedFilePath))
                                {
                                    foreach (var findFile in Directory.EnumerateFiles(
                                                 Path.GetDirectoryName(reader.FullPath), sharedFileName, SearchOption.AllDirectories))
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        sharedFilePath = findFile;
                                        break;
                                    }
                                }
                                if (File.Exists(sharedFilePath))
                                {
                                    importFiles.Add(sharedFilePath);
                                    importFilesHash.Add(sharedFileName);
                                }
                                else
                                {
                                    noexistFiles.Add(sharedFilePath);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    reader.Dispose();
                    throw;
                }
                catch (Exception e)
                {
                    Logger.Error($"Error while reading assets file {reader.FullPath}", e);
                    reader.Dispose();
                }
            }
            else
            {
                Logger.Info($"Skipping {reader.FullPath}");
                reader.Dispose();
            }
        }

        private void LoadAssetsFromMemory(
            FileReader reader,
            string originalPath,
            string unityVersion,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!assetsFileListHash.Contains(reader.FileName))
            {
                try
                {
                    var assetsFile = new SerializedFile(reader, this, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    assetsFile.originalPath = originalPath;
                    if (!string.IsNullOrEmpty(unityVersion) && assetsFile.header.m_Version < SerializedFileFormatVersion.Unknown_7)
                    {
                        assetsFile.SetVersion(unityVersion);
                    }
                    CheckStrippedVersion(assetsFile);
                    assetsFileList.Add(assetsFile);
                    assetsFileListHash.Add(assetsFile.fileName);
                }
                catch (OperationCanceledException)
                {
                    reader.Dispose();
                    throw;
                }
                catch (Exception e)
                {
                    Logger.Error($"Error while reading assets file {reader.FullPath} from {Path.GetFileName(originalPath)}", e);
                    resourceFileReaders.Add(reader.FileName, reader);
                }
            }
            else
                Logger.Info($"Skipping {originalPath} ({reader.FileName})");
        }

        private void LoadBundleFile(
            FileReader reader,
            CancellationToken cancellationToken,
            string originalPath = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Logger.Info("Loading " + reader.FullPath);
            LoadingStatus?.Invoke($"Decompressing bundle · {reader.FileName}");
            BundleFile bundleFile = null;
            try
            {
                bundleFile = new BundleFile(reader, cancellationToken);
                foreach (var file in bundleFile.fileList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    if (subReader.FileType == FileType.AssetsFile)
                    {
                        LoadAssetsFromMemory(
                            subReader,
                            originalPath ?? reader.FullPath,
                            bundleFile.m_Header.unityRevision,
                            cancellationToken);
                    }
                    else
                    {
                        resourceFileReaders[file.fileName] = subReader; //TODO
                    }
                }
            }
            catch (OperationCanceledException)
            {
                DisposeStreams(bundleFile?.fileList);
                throw;
            }
            catch (Exception e)
            {
                var str = $"Error while reading bundle file {reader.FullPath}";
                if (originalPath != null)
                {
                    str += $" from {Path.GetFileName(originalPath)}";
                }
                Logger.Error(str, e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private void LoadWebFile(FileReader reader, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Logger.Info("Loading " + reader.FullPath);
            LoadingStatus?.Invoke($"Reading WebData archive · {reader.FileName}");
            WebFile webFile = null;
            try
            {
                webFile = new WebFile(reader, cancellationToken);
                foreach (var file in webFile.fileList)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), file.fileName);
                    var subReader = new FileReader(dummyPath, file.stream);
                    switch (subReader.FileType)
                    {
                        case FileType.AssetsFile:
                            LoadAssetsFromMemory(subReader, reader.FullPath, null, cancellationToken);
                            break;
                        case FileType.BundleFile:
                            LoadBundleFile(subReader, cancellationToken, reader.FullPath);
                            break;
                        case FileType.WebFile:
                            LoadWebFile(subReader, cancellationToken);
                            break;
                        case FileType.ResourceFile:
                            resourceFileReaders[file.fileName] = subReader; //TODO
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                DisposeStreams(webFile?.fileList);
                throw;
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading web file {reader.FullPath}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        private static void DisposeStreams(IEnumerable<StreamFile> files)
        {
            if (files == null)
                return;
            foreach (var file in files)
                file?.stream?.Dispose();
        }

        private void LoadZipFile(FileReader reader, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Logger.Info("Loading " + reader.FileName);
            try
            {
                using (ZipArchive archive = new ZipArchive(reader.BaseStream, ZipArchiveMode.Read))
                {
                    List<string> splitFiles = new List<string>();
                    // register all files before parsing the assets so that the external references can be found
                    // and find split files
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entry.Name.Contains(".split"))
                        {
                            string baseName = Path.GetFileNameWithoutExtension(entry.Name);
                            string basePath = Path.Combine(Path.GetDirectoryName(entry.FullName), baseName);
                            if (!splitFiles.Contains(basePath))
                            {
                                splitFiles.Add(basePath);
                                importFilesHash.Add(baseName);
                            }
                        }
                        else
                        {
                            importFilesHash.Add(entry.Name);
                        }
                    }

                    // merge split files and load the result
                    foreach (string basePath in splitFiles)
                    {
                        try
                        {
                            Stream splitStream = new MemoryStream();
                            var transferred = false;
                            try
                            {
                                int i = 0;
                                while (true)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    string path = $"{basePath}.split{i++}";
                                    ZipArchiveEntry entry = archive.GetEntry(path);
                                    if (entry == null)
                                        break;
                                    using (Stream entryStream = entry.Open())
                                    {
                                        CopyTo(entryStream, splitStream, cancellationToken);
                                    }
                                }
                                splitStream.Seek(0, SeekOrigin.Begin);
                                FileReader entryReader = new FileReader(basePath, splitStream);
                                LoadFile(entryReader, cancellationToken);
                                transferred = assetsFileList.Any(
                                    assetsFile => ReferenceEquals(assetsFile.reader, entryReader));
                            }
                            finally
                            {
                                if (!transferred)
                                    splitStream.Dispose();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip split file {basePath}", e);
                        }
                    }

                    // load all entries
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string dummyPath = Path.Combine(Path.GetDirectoryName(reader.FullPath), reader.FileName, entry.FullName);
                            // create a new stream
                            // - to store the deflated stream in
                            // - to keep the data for later extraction
                            Stream streamReader = new MemoryStream();
                            var transferred = false;
                            try
                            {
                                using (Stream entryStream = entry.Open())
                                {
                                    CopyTo(entryStream, streamReader, cancellationToken);
                                }
                                streamReader.Position = 0;

                                FileReader entryReader = new FileReader(dummyPath, streamReader);
                                LoadFile(entryReader, cancellationToken);
                                if (entryReader.FileType == FileType.ResourceFile)
                                {
                                    entryReader.Position = 0;
                                    if (!resourceFileReaders.ContainsKey(entry.Name))
                                    {
                                        resourceFileReaders.Add(entry.Name, entryReader);
                                        transferred = true;
                                    }
                                }
                                else
                                {
                                    transferred = assetsFileList.Any(
                                        assetsFile => ReferenceEquals(assetsFile.reader, entryReader));
                                }
                            }
                            finally
                            {
                                if (!transferred)
                                    streamReader.Dispose();
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception e)
                        {
                            Logger.Error($"Error while reading zip entry {entry.FullName}", e);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                Logger.Error($"Error while reading zip file {reader.FileName}", e);
            }
            finally
            {
                reader.Dispose();
            }
        }

        public void CheckStrippedVersion(SerializedFile assetsFile)
        {
            if (assetsFile.IsVersionStripped && string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                throw new Exception("The Unity version has been stripped, please set the version in the options");
            }
            if (!string.IsNullOrEmpty(SpecifyUnityVersion))
            {
                assetsFile.SetVersion(SpecifyUnityVersion);
            }
        }

        public void Clear()
        {
            foreach (var assetsFile in assetsFileList)
            {
                assetsFile.Objects.Clear();
                assetsFile.reader.Close();
            }
            assetsFileList.Clear();

            foreach (var resourceFileReader in resourceFileReaders)
            {
                resourceFileReader.Value.Close();
            }
            resourceFileReaders.Clear();

            assetsFileIndexCache.Clear();
        }

        private void ReadAssets(CancellationToken cancellationToken)
        {
            Logger.Info("Read assets...");

            var progressCount = assetsFileList.Sum(x => x.m_Objects.Count);
            int i = 0;
            Progress.Reset();
            foreach (var assetsFile in assetsFileList)
            {
                LoadingStatus?.Invoke($"Decoding objects · {assetsFile.fileName}");
                foreach (var objectInfo in assetsFile.m_Objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var objectReader = new ObjectReader(assetsFile.reader, assetsFile, objectInfo);
                    try
                    {
                        Object obj;
                        switch (objectReader.type)
                        {
                            case ClassIDType.Animation:
                                obj = new Animation(objectReader);
                                break;
                            case ClassIDType.AnimationClip:
                                obj = new AnimationClip(objectReader);
                                break;
                            case ClassIDType.Animator:
                                obj = new Animator(objectReader);
                                break;
                            case ClassIDType.AnimatorController:
                                obj = new AnimatorController(objectReader);
                                break;
                            case ClassIDType.AnimatorOverrideController:
                                obj = new AnimatorOverrideController(objectReader);
                                break;
                            case ClassIDType.AssetBundle:
                                obj = new AssetBundle(objectReader);
                                break;
                            case ClassIDType.AudioClip:
                                obj = new AudioClip(objectReader);
                                break;
                            case ClassIDType.Avatar:
                                obj = new Avatar(objectReader);
                                break;
                            case ClassIDType.Font:
                                obj = new Font(objectReader);
                                break;
                            case ClassIDType.GameObject:
                                obj = new GameObject(objectReader);
                                break;
                            case ClassIDType.Material:
                                obj = new Material(objectReader);
                                break;
                            case ClassIDType.Mesh:
                                obj = new Mesh(objectReader);
                                break;
                            case ClassIDType.MeshFilter:
                                obj = new MeshFilter(objectReader);
                                break;
                            case ClassIDType.MeshRenderer:
                                obj = new MeshRenderer(objectReader);
                                break;
                            case ClassIDType.MonoBehaviour:
                                obj = new MonoBehaviour(objectReader);
                                break;
                            case ClassIDType.MonoScript:
                                obj = new MonoScript(objectReader);
                                break;
                            case ClassIDType.MovieTexture:
                                obj = new MovieTexture(objectReader);
                                break;
                            case ClassIDType.PlayerSettings:
                                obj = new PlayerSettings(objectReader);
                                break;
                            case ClassIDType.RectTransform:
                                obj = new RectTransform(objectReader);
                                break;
                            case ClassIDType.Shader:
                                obj = new Shader(objectReader);
                                break;
                            case ClassIDType.SkinnedMeshRenderer:
                                obj = new SkinnedMeshRenderer(objectReader);
                                break;
                            case ClassIDType.Sprite:
                                obj = new Sprite(objectReader);
                                break;
                            case ClassIDType.SpriteAtlas:
                                obj = new SpriteAtlas(objectReader);
                                break;
                            case ClassIDType.TextAsset:
                                obj = new TextAsset(objectReader);
                                break;
                            case ClassIDType.Texture2D:
                                obj = new Texture2D(objectReader);
                                break;
                            case ClassIDType.Transform:
                                obj = new Transform(objectReader);
                                break;
                            case ClassIDType.VideoClip:
                                obj = new VideoClip(objectReader);
                                break;
                            case ClassIDType.ResourceManager:
                                obj = new ResourceManager(objectReader);
                                break;
                            default:
                                obj = new Object(objectReader);
                                break;
                        }
                        assetsFile.AddObject(obj);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("Unable to load object")
                            .AppendLine($"Assets {assetsFile.fileName}")
                            .AppendLine($"Path {assetsFile.originalPath}")
                            .AppendLine($"Type {objectReader.type}")
                            .AppendLine($"PathID {objectInfo.m_PathID}")
                            .Append(e);
                        Logger.Error(sb.ToString());
                    }

                    Progress.Report(++i, progressCount);
                }
            }
        }

        private void ProcessAssets(CancellationToken cancellationToken)
        {
            Logger.Info("Process Assets...");

            var progressCount = assetsFileList.Sum(x => x.Objects.Count);
            var processedCount = 0;
            Progress.Reset();
            foreach (var assetsFile in assetsFileList)
            {
                LoadingStatus?.Invoke($"Linking relationships · {assetsFile.fileName}");
                foreach (var obj in assetsFile.Objects)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (obj is GameObject m_GameObject)
                    {
                        foreach (var pptr in m_GameObject.m_Components)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (pptr.TryGet(out var m_Component))
                            {
                                switch (m_Component)
                                {
                                    case Transform m_Transform:
                                        m_GameObject.m_Transform = m_Transform;
                                        break;
                                    case MeshRenderer m_MeshRenderer:
                                        m_GameObject.m_MeshRenderer = m_MeshRenderer;
                                        break;
                                    case MeshFilter m_MeshFilter:
                                        m_GameObject.m_MeshFilter = m_MeshFilter;
                                        break;
                                    case SkinnedMeshRenderer m_SkinnedMeshRenderer:
                                        m_GameObject.m_SkinnedMeshRenderer = m_SkinnedMeshRenderer;
                                        break;
                                    case Animator m_Animator:
                                        m_GameObject.m_Animator = m_Animator;
                                        break;
                                    case Animation m_Animation:
                                        m_GameObject.m_Animation = m_Animation;
                                        break;
                                }
                            }
                        }
                    }
                    else if (obj is SpriteAtlas m_SpriteAtlas)
                    {
                        foreach (var m_PackedSprite in m_SpriteAtlas.m_PackedSprites)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (m_PackedSprite.TryGet(out var m_Sprite))
                            {
                                if (m_Sprite.m_SpriteAtlas.IsNull)
                                {
                                    m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                }
                                else
                                {
                                    m_Sprite.m_SpriteAtlas.TryGet(out var m_SpriteAtlaOld);
                                    if (m_SpriteAtlaOld.m_IsVariant)
                                    {
                                        m_Sprite.m_SpriteAtlas.Set(m_SpriteAtlas);
                                    }
                                }
                            }

                        }
                    }

                    Progress.Report(++processedCount, progressCount);
                }
            }
        }
    }
}
