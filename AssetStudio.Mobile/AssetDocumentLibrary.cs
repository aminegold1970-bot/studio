using Microsoft.Maui.Storage;
using System.Security.Cryptography;

namespace AssetStudioMobile;

/// <summary>Persistent, content-deduplicated store of original Unity source files.</summary>
public sealed class AssetDocumentLibrary
{
    public string LibraryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AssetStudio", "Library");

    private string ImportedFoldersPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AssetStudio", "Folders");

    public AssetDocumentLibrary() => Directory.CreateDirectory(LibraryPath);

    public async Task<FileResult> ImportAsync(FileResult source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await using var input = await source.OpenReadAsync();
        return await CopyToLibraryAsync(input, source.FileName, cancellationToken);
    }

    public async Task<FileResult> ImportPathAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Dropped source file was not found.", sourcePath);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await CopyToLibraryAsync(input, Path.GetFileName(sourcePath), cancellationToken);
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

    private async Task<FileResult> CopyToLibraryAsync(Stream input, string originalName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LibraryPath);
        var safeName = SanitizeFileName(originalName);
        var staging = Path.Combine(LibraryPath, $".incoming_{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await input.CopyToAsync(output, 81920, cancellationToken);

            var incomingHash = await ComputeSha256Async(staging, cancellationToken);
            foreach (var existing in Directory.EnumerateFiles(LibraryPath).Where(File.Exists))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(existing).StartsWith(".incoming_", StringComparison.Ordinal)) continue;
                if (new FileInfo(existing).Length != new FileInfo(staging).Length) continue;
                if (string.Equals(incomingHash, await ComputeSha256Async(existing, cancellationToken), StringComparison.Ordinal))
                {
                    File.Delete(staging);
                    return new FileResult(existing);
                }
            }

            var destination = Path.Combine(LibraryPath, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{safeName}");
            File.Move(staging, destination);
            return new FileResult(destination);
        }
        catch
        {
            if (File.Exists(staging)) File.Delete(staging);
            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, token);
        return Convert.ToHexString(hash);
    }

    public IReadOnlyList<LibraryFileEntry> GetEntries()
    {
        Directory.CreateDirectory(LibraryPath);
        return Directory.EnumerateFiles(LibraryPath)
            .Where(path => File.Exists(path) && !Path.GetFileName(path).StartsWith(".incoming_", StringComparison.Ordinal))
            .Select(path => new FileInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => new LibraryFileEntry(
                new FileResult(info.FullName),
                GetOriginalName(Path.GetFileName(info.FullName)),
                info.Length,
                info.LastWriteTime))
            .ToList();
    }

    public bool Delete(FileResult file)
    {
        if (file == null || string.IsNullOrWhiteSpace(file.FullPath)) return false;
        var full = Path.GetFullPath(file.FullPath);
        var root = Path.GetFullPath(LibraryPath) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full)) return false;
        File.Delete(full);
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        var leaf = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "UnityAsset.bin" : name);
        foreach (var invalid in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(leaf) ? "UnityAsset.bin" : leaf;
    }

    private static string GetOriginalName(string storedName)
    {
        if (storedName.Length > 48
            && storedName[14] == '_'
            && storedName[47] == '_'
            && Guid.TryParseExact(storedName.Substring(15, 32), "N", out _))
        {
            return storedName[48..];
        }

        return storedName;
    }
}

public sealed record LibraryFileEntry(FileResult File, string StoredName, long Size, DateTime Modified)
{
    public string DisplaySize => Size switch
    {
        < 1024 => $"{Size:N0} B",
        < 1024 * 1024 => $"{Size / 1024d:0.#} KB",
        _ => $"{Size / (1024d * 1024d):0.##} MB"
    };
    public string DisplayDate => Modified.ToString("yyyy-MM-dd HH:mm");
}
