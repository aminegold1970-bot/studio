using Microsoft.Maui.Storage;
using System.Security.Cryptography;

namespace AssetStudioMobile;

/// <summary>
/// Persistent copy of user-selected Unity source files. Files are kept in the
/// app's Documents/AssetStudio/Library directory so they remain available on
/// later launches and can be inspected through the iOS Files app.
/// </summary>
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
        var destination = Path.Combine(LibraryPath, $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}_{safeName}");
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await input.CopyToAsync(output, 81920, cancellationToken);
        return new FileResult(destination);
    }

    public IReadOnlyList<FileResult> GetSavedFiles()
    {
        Directory.CreateDirectory(LibraryPath);
        return Directory.EnumerateFiles(LibraryPath)
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(path => new FileResult(path))
            .ToList();
    }

    private static string SanitizeFileName(string name)
    {
        var leaf = Path.GetFileName(string.IsNullOrWhiteSpace(name) ? "UnityAsset.bin" : name);
        foreach (var invalid in Path.GetInvalidFileNameChars()) leaf = leaf.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(leaf) ? "UnityAsset.bin" : leaf;
    }
}
