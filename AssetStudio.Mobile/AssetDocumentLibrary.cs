using Microsoft.Maui.Storage;
using SharpCompress.Readers;
using System.Security.Cryptography;

namespace AssetStudioMobile;

/// <summary>
/// Temporary, content-deduplicated cache for source files picked through the
/// mobile document pickers. Kept in the app cache directory so Documents
/// contains only the top-level Errors folder.
/// </summary>
public sealed class AssetDocumentLibrary
{
    public string SourceCachePath { get; } = Path.Combine(
        FileSystem.CacheDirectory, "AssetStudio", "Sources");

    private string ImportedFoldersPath => Path.Combine(
        FileSystem.CacheDirectory, "AssetStudio", "Folders");

    private readonly Dictionary<string, (long Length, string Hash)> _cacheHashes =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheIndexLock = new(1, 1);
    private bool _cacheIndexBuilt;

    public AssetDocumentLibrary() => Directory.CreateDirectory(SourceCachePath);

    public async Task<FileResult> ImportAsync(FileResult source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var input = await source.OpenReadAsync();
        return await CopyToCacheAsync(input, source.FileName, cancellationToken);
    }

    public async Task<FileResult> ImportPathAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Dropped source file was not found.", sourcePath);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await CopyToCacheAsync(input, Path.GetFileName(sourcePath), cancellationToken);
    }

    public async Task<string> ImportFolderAsync(
        string sourcePath,
        CancellationToken cancellationToken = default,
        IProgress<MainPage.AssetLoadProgress>? progress = null)
    {
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Selected folder was not found: {sourcePath}");

        var sourceRoot = Path.GetFullPath(sourcePath);
        var files = await Task.Run(() =>
        {
            var foundFiles = new List<string>();
            foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foundFiles.Add(file);
                if (foundFiles.Count % 32 == 0)
                    progress?.Report(new MainPage.AssetLoadProgress(
                        1, $"Scanning selected folder · {foundFiles.Count:N0} files found"));
            }
            return foundFiles;
        }, cancellationToken);
        if (files.Count == 0)
            throw new InvalidDataException("The selected folder does not contain any files.");
        progress?.Report(new MainPage.AssetLoadProgress(2, $"Found {files.Count:N0} files; preparing import…"));

        var totalBytes = await Task.Run(() =>
        {
            long total = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = checked(total + new FileInfo(file).Length);
            }
            return total;
        }, cancellationToken);

        var folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot));
        if (string.IsNullOrWhiteSpace(folderName))
            folderName = "AssetStudioFolder";
        folderName = SanitizeFileName(folderName);
        Directory.CreateDirectory(ImportedFoldersPath);
        var staging = Path.Combine(ImportedFoldersPath, $".incoming_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            long copiedBytes = 0;
            var lastReportedPercent = 2;
            foreach (var sourceFile in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
                if (Path.IsPathRooted(relativePath)
                    || relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(segment => segment is "" or "." or ".."))
                {
                    throw new InvalidDataException($"The selected folder contains an unsafe path: {relativePath}");
                }

                var destination = Path.Combine(staging, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = new FileStream(
                    sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await using var output = new FileStream(
                    destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await input.ReadAsync(
                           buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    copiedBytes += bytesRead;
                    var percent = totalBytes == 0
                        ? 4
                        : 2 + (int)Math.Min(2, (long)copiedBytes * 2 / totalBytes);
                    if (percent > lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        progress?.Report(new MainPage.AssetLoadProgress(
                            percent, $"Copying selected folder · {copiedBytes:N0}/{totalBytes:N0} bytes"));
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new MainPage.AssetLoadProgress(4, "Selected folder copied; preparing asset load…"));
            var importedFolder = Path.Combine(
                ImportedFoldersPath, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{folderName}");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, importedFolder);
            return importedFolder;
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private async Task<FileResult> CopyToCacheAsync(Stream input, string originalName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(SourceCachePath);
        var safeName = SanitizeFileName(originalName);
        var staging = Path.Combine(SourceCachePath, $".incoming_{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await input.CopyToAsync(output, 81920, cancellationToken);

            var incomingHash = await ComputeSha256Async(staging, cancellationToken);
            var incomingLength = new FileInfo(staging).Length;
            var cached = (await GetCacheIndexAsync(cancellationToken)).ToArray();
            foreach (var existing in cached)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existing.Value.Length != incomingLength) continue;
                if (string.Equals(incomingHash, existing.Value.Hash, StringComparison.Ordinal))
                {
                    File.Delete(staging);
                    return new FileResult(existing.Key);
                }
            }

            var destination = Path.Combine(SourceCachePath, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{safeName}");
            File.Move(staging, destination);
            await AddCachedHashAsync(destination, incomingLength, incomingHash, cancellationToken);
            return new FileResult(destination);
        }
        catch
        {
            if (File.Exists(staging)) File.Delete(staging);
            throw;
        }
    }

    // Hashing every cached file for every import made large multi-file loads
    // effectively O(n²). Build the index once, then reuse it for the session.
    private async Task<IReadOnlyDictionary<string, (long Length, string Hash)>> GetCacheIndexAsync(
        CancellationToken cancellationToken)
    {
        await _cacheIndexLock.WaitAsync(cancellationToken);
        try
        {
            if (!_cacheIndexBuilt)
            {
                foreach (var path in Directory.EnumerateFiles(SourceCachePath).ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(path);
                    if (name.StartsWith(".incoming_", StringComparison.Ordinal) || !File.Exists(path))
                        continue;
                    if (!_cacheHashes.ContainsKey(path))
                        _cacheHashes[path] = (new FileInfo(path).Length, await ComputeSha256Async(path, cancellationToken));
                }
                _cacheIndexBuilt = true;
            }
            return _cacheHashes.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        finally
        {
            _cacheIndexLock.Release();
        }
    }

    private async Task AddCachedHashAsync(
        string path, long length, string hash, CancellationToken cancellationToken)
    {
        await _cacheIndexLock.WaitAsync(cancellationToken);
        try
        {
            _cacheHashes[path] = (length, hash);
        }
        finally
        {
            _cacheIndexLock.Release();
        }
    }

    internal static bool IsExtractableArchive(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tgz", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tbz2", StringComparison.OrdinalIgnoreCase);
    }

    // Extract archives into the imported-folder cache and let the normal folder
    // loader parse their contents. This avoids the parser's in-memory ZIP path
    // for large archives and adds RAR/7z/tar support through SharpCompress.
    public async Task<string> ImportArchivesAsync(
        IReadOnlyList<FileResult> sources,
        IProgress<MainPage.AssetLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ImportedFoldersPath);
        var staging = Path.Combine(ImportedFoldersPath, $".incoming_archives_{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            for (var index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = sources[index];
                var originalName = Path.GetFileName(source.FileName);
                var safeName = SanitizeFileName(originalName);

                if (!IsExtractableArchive(originalName))
                {
                    var destination = Path.Combine(staging, $"{index:D3}_{safeName}");
                    await using var input = await source.OpenReadAsync();
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await input.CopyToAsync(output, 81920, cancellationToken);
                    continue;
                }

                progress?.Report(new MainPage.AssetLoadProgress(
                    2, $"Copying archive {originalName}…"));
                var cached = await ImportAsync(source, cancellationToken);
                progress?.Report(new MainPage.AssetLoadProgress(
                    3, $"Extracting archive {originalName}…"));
                var destinationRoot = Path.Combine(staging, $"{index:D3}_{SanitizeFileName(Path.GetFileNameWithoutExtension(originalName))}");
                await Task.Run(
                    () => ExtractArchive(cached.FullPath, destinationRoot, originalName, progress, cancellationToken),
                    cancellationToken);
            }

            progress?.Report(new MainPage.AssetLoadProgress(4, "Archive extracted; loading assets…"));
            var importedFolder = Path.Combine(
                ImportedFoldersPath,
                $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_Archives");
            Directory.Move(staging, importedFolder);
            return importedFolder;
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static void ExtractArchive(
        string archivePath,
        string destinationRoot,
        string displayName,
        IProgress<MainPage.AssetLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationRoot);
        var rootPrefix = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;
        using var input = File.OpenRead(archivePath);
        using var reader = ReaderFactory.OpenReader(input);
        var extracted = 0;
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory)
                continue;

            var entryName = reader.Entry.Key ?? string.Empty;
            var relative = entryName
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.Ordinal))
                throw new InvalidDataException($"The archive {displayName} contains an unsafe path: {entryName}");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            using var output = File.Create(fullPath);
            reader.WriteEntryTo(output);
            extracted++;
            if (extracted % 16 == 0)
                progress?.Report(new MainPage.AssetLoadProgress(
                    3, $"Extracting archive {displayName} · {extracted:N0} files"));
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, token);
        return Convert.ToHexString(hash);
    }

    private static string SanitizeFileName(string name)
    {
        var leaf = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "UnityAsset.bin" : name);
        foreach (var invalid in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(leaf) ? "UnityAsset.bin" : leaf;
    }
}
