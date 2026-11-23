using Microsoft.Maui.Storage;
using System.Security.Cryptography;

namespace AssetStudioMobile;

/// <summary>Persistent, content-deduplicated store of original Unity source files.</summary>
public sealed class AssetDocumentLibrary
{
    public string LibraryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "AssetStudio", "Library");

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
