using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace AssetStudio
{
    public class WebFile
    {
        public StreamFile[] fileList;

        private class WebData
        {
            public int dataOffset;
            public int dataLength;
            public string path;
        }

        public WebFile(EndianBinaryReader reader)
            : this(reader, CancellationToken.None)
        {
        }

        public WebFile(EndianBinaryReader reader, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader.Endian = EndianType.LittleEndian;
            var signature = reader.ReadStringToNull();
            var headLength = reader.ReadInt32();
            var dataList = new List<WebData>();
            while (reader.BaseStream.Position < headLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = new WebData();
                data.dataOffset = reader.ReadInt32();
                data.dataLength = reader.ReadInt32();
                var pathLength = reader.ReadInt32();
                data.path = Encoding.UTF8.GetString(reader.ReadBytes(pathLength));
                dataList.Add(data);
            }
            fileList = new StreamFile[dataList.Count];
            try
            {
                for (int i = 0; i < dataList.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var data = dataList[i];
                    var file = new StreamFile();
                    fileList[i] = file;
                    file.path = data.path;
                    file.fileName = Path.GetFileName(data.path);
                    reader.BaseStream.Position = data.dataOffset;
                    file.stream = new MemoryStream();
                    reader.BaseStream.CopyTo(file.stream, data.dataLength, cancellationToken);
                    file.stream.Position = 0;
                }
            }
            catch
            {
                foreach (var file in fileList)
                    file?.stream?.Dispose();
                throw;
            }
        }
    }
}
