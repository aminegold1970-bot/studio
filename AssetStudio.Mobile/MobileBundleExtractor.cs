using System.IO.Compression;
using AssetStudio;

namespace AssetStudioMobile;

internal static class MobileBundleExtractor
{
    public static string CreateArchive(IReadOnlyCollection<string> paths)
    {
        return CreateArchive(paths, FileSystem.CacheDirectory);
    }

    internal static string CreateArchive(IReadOnlyCollection<string> paths, string outputDirectory)
    {
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
                            entryCount += AddEntries(output, root, bundle.fileList, entryNames);
                            break;
                        case FileType.WebFile:
                            var webFile = new WebFile(reader);
                            entryCount += AddEntries(output, root, webFile.fileList, entryNames);
                            break;
                        case FileType.ZipFile:
                            entryCount += AddZipEntries(output, root, reader.BaseStream, entryNames);
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
        HashSet<string> entryNames)
    {
        var count = 0;
        var fileList = files.ToList();
        try
        {
            foreach (var file in fileList)
            {
                var path = CombineSafePath(root, file.path);
                AddStream(archive, path, file.stream, entryNames);
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
        HashSet<string> entryNames)
    {
        using var source = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        var count = 0;
        foreach (var entry in source.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var path = CombineSafePath(root, entry.FullName);
            using var stream = entry.Open();
            AddStream(output, path, stream, entryNames);
            count++;
        }
        return count;
    }

    private static void AddStream(
        ZipArchive archive,
        string path,
        Stream source,
        HashSet<string> entryNames)
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
        source.CopyTo(target);
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
