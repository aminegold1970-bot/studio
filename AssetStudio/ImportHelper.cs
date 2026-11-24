using Org.Brotli.Dec;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace AssetStudio
{
    public static class ImportHelper
    {
        public static void MergeSplitAssets(string path, bool allDirectories = false)
        {
            MergeSplitAssets(path, allDirectories, CancellationToken.None);
        }

        public static void MergeSplitAssets(string path, bool allDirectories, CancellationToken cancellationToken)
        {
            var splitFiles = Directory.EnumerateFiles(
                path, "*.split0", allDirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            foreach (var splitFile in splitFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destFile = Path.GetFileNameWithoutExtension(splitFile);
                var destPath = Path.GetDirectoryName(splitFile);
                var destFull = Path.Combine(destPath, destFile);
                if (!File.Exists(destFull))
                {
                    var splitParts = new List<string>();
                    foreach (var splitPart in Directory.EnumerateFiles(destPath, destFile + ".split*"))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        splitParts.Add(splitPart);
                    }
                    var temporaryPath = $"{destFull}.{Guid.NewGuid():N}.loading.tmp";
                    try
                    {
                        using (var destStream = File.Create(temporaryPath))
                        {
                            for (int i = 0; i < splitParts.Count; i++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var splitPart = destFull + ".split" + i;
                                using (var sourceStream = File.OpenRead(splitPart))
                                {
                                    CopyTo(sourceStream, destStream, cancellationToken);
                                }
                            }
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Move(temporaryPath, destFull);
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                }
            }
        }

        public static string[] ProcessingSplitFiles(List<string> selectFile)
        {
            return ProcessingSplitFiles(selectFile, CancellationToken.None);
        }

        public static string[] ProcessingSplitFiles(List<string> selectFile, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var splitFiles = new List<string>();
            var remainingFiles = new List<string>();
            var seenSplitFiles = new HashSet<string>();
            foreach (var file in selectFile)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!file.Contains(".split"))
                {
                    remainingFiles.Add(file);
                    continue;
                }

                var basePath = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file));
                if (seenSplitFiles.Add(basePath))
                    splitFiles.Add(basePath);
            }

            foreach (var file in splitFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(file))
                {
                    remainingFiles.Add(file);
                }
            }
            selectFile.Clear();
            selectFile.AddRange(remainingFiles);
            return remainingFiles.Distinct().ToArray();
        }

        public static FileReader DecompressGZip(FileReader reader)
        {
            return DecompressGZip(reader, CancellationToken.None);
        }

        public static FileReader DecompressGZip(FileReader reader, CancellationToken cancellationToken)
        {
            using (reader)
            {
                var stream = new MemoryStream();
                using (var gs = new GZipStream(reader.BaseStream, CompressionMode.Decompress))
                {
                    CopyTo(gs, stream, cancellationToken);
                }
                stream.Position = 0;
                return new FileReader(reader.FullPath, stream);
            }
        }

        public static FileReader DecompressBrotli(FileReader reader)
        {
            return DecompressBrotli(reader, CancellationToken.None);
        }

        public static FileReader DecompressBrotli(FileReader reader, CancellationToken cancellationToken)
        {
            using (reader)
            {
                var stream = new MemoryStream();
                using (var brotliStream = new BrotliInputStream(reader.BaseStream))
                {
                    CopyTo(brotliStream, stream, cancellationToken);
                }
                stream.Position = 0;
                return new FileReader(reader.FullPath, stream);
            }
        }

        internal static void CopyTo(Stream source, Stream destination, CancellationToken cancellationToken)
        {
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, bytesRead);
            }
        }
    }
}
