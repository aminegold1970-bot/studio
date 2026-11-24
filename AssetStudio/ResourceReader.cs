using System;
using System.IO;

namespace AssetStudio
{
    public class ResourceReader
    {
        private bool needSearch;
        private string path;
        private SerializedFile assetsFile;
        private long offset;
        private long size;
        private BinaryReader reader;

        public int Size { get => checked((int)size); }

        public ResourceReader(string path, SerializedFile assetsFile, long offset, long size)
        {
            needSearch = true;
            this.path = path;
            this.assetsFile = assetsFile;
            this.offset = offset;
            this.size = size;
        }

        public ResourceReader(BinaryReader reader, long offset, long size)
        {
            this.reader = reader;
            this.offset = offset;
            this.size = size;
        }

        private BinaryReader GetReader()
        {
            if (needSearch)
            {
                var resourceFileName = Path.GetFileName(path);
                if (assetsFile.assetsManager.resourceFileReaders.TryGetValue(resourceFileName, out reader))
                {
                    needSearch = false;
                    return reader;
                }
                var assetsFileDirectory = Path.GetDirectoryName(assetsFile.fullName);
                var resourceFilePath = Path.Combine(assetsFileDirectory, resourceFileName);
                if (!File.Exists(resourceFilePath))
                {
                    var findFiles = Directory.GetFiles(assetsFileDirectory, resourceFileName, SearchOption.AllDirectories);
                    if (findFiles.Length > 0)
                    {
                        resourceFilePath = findFiles[0];
                    }
                }
                if (File.Exists(resourceFilePath))
                {
                    needSearch = false;
                    reader = new BinaryReader(File.OpenRead(resourceFilePath));
                    assetsFile.assetsManager.resourceFileReaders.Add(resourceFileName, reader);
                    return reader;
                }
                throw new FileNotFoundException($"Can't find the resource file {resourceFileName}");
            }
            else
            {
                return reader;
            }
        }

        public byte[] GetData()
        {
            var dataSize = GetDataSize();
            var binaryReader = GetReader();
            binaryReader.BaseStream.Position = offset;
            var data = binaryReader.ReadBytes(dataSize);
            if (data.Length != dataSize)
                throw new EndOfStreamException("The resource data ended before the requested range was read.");
            return data;
        }

        public void GetData(byte[] buff)
        {
            var dataSize = GetDataSize();
            if (buff == null)
                throw new ArgumentNullException(nameof(buff));
            if (buff.Length < dataSize)
                throw new ArgumentException("The destination buffer is smaller than the resource data.", nameof(buff));

            var binaryReader = GetReader();
            binaryReader.BaseStream.Position = offset;
            var bytesRead = 0;
            while (bytesRead < dataSize)
            {
                var read = binaryReader.Read(buff, bytesRead, dataSize - bytesRead);
                if (read == 0)
                    throw new EndOfStreamException("The resource data ended before the requested range was read.");
                bytesRead += read;
            }
        }

        private int GetDataSize()
        {
            if (size < 0 || size > int.MaxValue)
                throw new InvalidDataException("The resource data size is outside the supported range.");
            return (int)size;
        }

        public void WriteData(string path)
        {
            var binaryReader = GetReader();
            binaryReader.BaseStream.Position = offset;
            using (var writer = File.OpenWrite(path))
            {
                binaryReader.BaseStream.CopyTo(writer, size);
            }
        }
    }
}
