using System.IO;

namespace AssetStudio
{
    public class StreamedResource
    {
        public string m_Source;
        public long m_Offset; //ulong
        public long m_Size; //ulong

        public StreamedResource(BinaryReader reader)
        {
            m_Source = reader.ReadAlignedString();
            m_Offset = reader.ReadInt64();
            m_Size = reader.ReadInt64();
        }
    }

    public sealed class VideoClip : NamedObject
    {
        public ResourceReader m_VideoData;
        public string m_OriginalPath;
        public StreamedResource m_ExternalResources;
        public uint m_ProxyWidth;
        public uint m_ProxyHeight;
        public uint m_Width;
        public uint m_Height;
        public uint m_PixelAspectRatioNum;
        public uint m_PixelAspectRatioDen;
        public double m_FrameRate;
        public ulong m_FrameCount;
        public int m_Format;
        public ushort[] m_AudioChannelCount;
        public uint[] m_AudioSampleRate;
        public string[] m_AudioLanguage;
        public bool m_HasSplitAlpha;

        public VideoClip(ObjectReader reader) : base(reader)
        {
            m_OriginalPath = reader.ReadAlignedString();
            m_ProxyWidth = reader.ReadUInt32();
            m_ProxyHeight = reader.ReadUInt32();
            m_Width = reader.ReadUInt32();
            m_Height = reader.ReadUInt32();
            if (version[0] > 2017 || (version[0] == 2017 && version[1] >= 2)) //2017.2 and up
            {
                m_PixelAspectRatioNum = reader.ReadUInt32();
                m_PixelAspectRatioDen = reader.ReadUInt32();
            }
            m_FrameRate = reader.ReadDouble();
            m_FrameCount = reader.ReadUInt64();
            m_Format = reader.ReadInt32();
            m_AudioChannelCount = reader.ReadUInt16Array();
            reader.AlignStream();
            m_AudioSampleRate = reader.ReadUInt32Array();
            m_AudioLanguage = reader.ReadStringArray();
            if (version[0] >= 2020) //2020.1 and up
            {
                var m_VideoShadersSize = reader.ReadInt32();
                var m_VideoShaders = new PPtr<Shader>[m_VideoShadersSize];
                for (int i = 0; i < m_VideoShadersSize; i++)
                {
                    m_VideoShaders[i] = new PPtr<Shader>(reader);
                }
            }
            m_ExternalResources = new StreamedResource(reader);
            m_HasSplitAlpha = reader.ReadBoolean();
            if (version[0] >= 2020) //2020.1 and up
            {
                var m_sRGB = reader.ReadBoolean();
            }

            ResourceReader resourceReader;
            if (!string.IsNullOrEmpty(m_ExternalResources.m_Source))
            {
                resourceReader = new ResourceReader(m_ExternalResources.m_Source, assetsFile, m_ExternalResources.m_Offset, m_ExternalResources.m_Size);
            }
            else
            {
                resourceReader = new ResourceReader(reader, reader.BaseStream.Position, m_ExternalResources.m_Size);
            }
            m_VideoData = resourceReader;
        }
    }
}
