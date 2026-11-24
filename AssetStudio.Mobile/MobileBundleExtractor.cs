using System.IO.Compression;
using AssetStudio;

namespace AssetStudioMobile;

internal static class MobileBundleExtractor
{
    public static string CreateArchive(IReadOnlyCollection<string> paths)
    {
        return CreateArchive(paths, FileSystem.CacheDirectory, CancellationToken.None);
    }

    internal static string CreateArchive(
        IReadOnlyCollection<string> paths,
        CancellationToken cancellationToken)
    {
        return CreateArchive(paths, FileSystem.CacheDirectory, cancellationToken);
    }

    internal static string CreateArchive(IReadOnlyCollection<string> paths, string outputDirectory)
    {
        return CreateArchive(paths, outputDirectory, CancellationToken.None);
    }

    public static string CreateArchiveFromFolder(
        string sourceFolder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || !Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Selected folder was not found: {sourceFolder}");

        var files = new List<string>();
        foreach (var path in Directory.EnumerateFiles(sourceFolder, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        }).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add(path);
        }
        if (files.Count == 0)
            throw new NotSupportedException("The selected folder does not contain any files to extract.");

        return CreateArchive(files, FileSystem.CacheDirectory, cancellationToken);
    }

    private static string CreateArchive(
        IReadOnlyCollection<string> paths,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputDirectory);
        var archivePath = Path.Combine(outputDirectory, $"assetstudio-unpacked-{Guid.NewGuid():N}.zip");
        var entryNames = new HashSet<string>(StringComparer.Ordinal);
        var entryCount = 0;

        try
        {
            using (var output = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var inputIndex = 0;
                foreach (var path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    inputIndex++;
                    using var reader = new FileReader(path);
                    var root = $"{SafeSegment(Path.GetFileNameWithoutExtension(path))}_{inputIndex}";
                    switch (reader.FileType)
                    {
                        case FileType.BundleFile:
                            var bundle = new BundleFile(reader);
                            if (bundle.fileList is null)
                            {
                                throw new NotSupportedException($"Unity bundle type '{bundle.m_Header.signature}' is not supported for extraction.");
                            }
                            entryCount += AddEntries(output, root, bundle.fileList, entryNames, cancellationToken);
                            break;
                        case FileType.WebFile:
                            var webFile = new WebFile(reader);
                            entryCount += AddEntries(output, root, webFile.fileList, entryNames, cancellationToken);
                            break;
                        case FileType.ZipFile:
                            entryCount += AddZipEntries(output, root, reader.BaseStream, entryNames, cancellationToken);
                            break;
                    }
                }
            }

            if (entryCount == 0)
            {
                throw new NotSupportedException("No Unity bundles, WebData files, or ZIP archives were selected for extraction.");
            }

            return archivePath;
        }
        catch
        {
            if (File.Exists(archivePath))
            {
                File.Delete(archivePath);
            }
            throw;
        }
    }

    private static int AddEntries(
        ZipArchive archive,
        string root,
        IEnumerable<StreamFile> files,
        HashSet<string> entryNames,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var fileList = files.ToList();
        try
        {
            foreach (var file in fileList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = CombineSafePath(root, file.path);
                AddStream(archive, path, file.stream, entryNames, cancellationToken);
                count++;
            }
        }
        finally
        {
            foreach (var file in fileList)
            {
                file.stream.Dispose();
            }
        }
        return count;
    }

    private static int AddZipEntries(
        ZipArchive output,
        string root,
        Stream input,
        HashSet<string> entryNames,
        CancellationToken cancellationToken)
    {
        using var source = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var count = 0;
        foreach (var entry in source.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var path = CombineSafePath(root, entry.FullName);
            using var stream = entry.Open();
            AddStream(output, path, stream, entryNames, cancellationToken);
            count++;
        }
        return count;
    }

    private static void AddStream(
        ZipArchive archive,
        string path,
        Stream source,
        HashSet<string> entryNames,
        CancellationToken cancellationToken)
    {
        if (!entryNames.Add(path))
        {
            throw new InvalidDataException($"Multiple extracted files have the same path '{path}'.");
        }

        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var target = entry.Open();
        if (source.CanSeek)
        {
            source.Position = 0;
        }
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            target.Write(buffer, 0, read);
        }
    }

    private static string CombineSafePath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
        {
            throw new InvalidDataException("An archive contains an empty or rooted file path.");
        }

        var segments = relativePath.Replace('\\', '/').Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || segment.Contains(':')))
        {
            throw new InvalidDataException($"An archive contains an unsafe file path: '{relativePath}'.");
        }

        return $"{root}/{string.Join('/', segments.Select(SafeSegment))}";
    }

    private static string SafeSegment(string value)
    {
        var safe = string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) || character is '/' or '\\' or ':'
                ? '_'
                : character));
        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
        {
            throw new InvalidDataException("An archive contains an invalid file name.");
        }
        return safe;
    }
}
