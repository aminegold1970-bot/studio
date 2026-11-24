using System.IO;
using System.Threading;

namespace AssetStudio
{
    public static class StreamExtensions
    {
        private const int BufferSize = 81920;

        public static void CopyTo(this Stream source, Stream destination, long size)
        {
            source.CopyTo(destination, size, CancellationToken.None);
        }

        public static void CopyTo(
            this Stream source,
            Stream destination,
            long size,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            for (var left = size; left > 0; left -= BufferSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int toRead = BufferSize < left ? BufferSize : (int)left;
                int read = source.Read(buffer, 0, toRead);
                destination.Write(buffer, 0, read);
                if (read != toRead)
                {
                    return;
                }
            }
        }
    }
}
