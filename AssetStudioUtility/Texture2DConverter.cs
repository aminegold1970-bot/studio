using System;
using System.IO;
using System.Runtime.CompilerServices;
using Texture2DDecoder;

namespace AssetStudio
{
    public class Texture2DConverter
    {
        private ResourceReader reader;
        private int m_Width;
        private int m_Height;
        private TextureFormat m_TextureFormat;
        private int[] version;
        private BuildTarget platform;
        private int outPutSize;

        public Texture2DConverter(Texture2D m_Texture2D)
        {
            reader = m_Texture2D.image_data;
            m_Width = m_Texture2D.m_Width;
            m_Height = m_Texture2D.m_Height;
            m_TextureFormat = m_Texture2D.m_TextureFormat;
            version = m_Texture2D.version;
            platform = m_Texture2D.platform;
            outPutSize = checked(m_Width * m_Height * 4);
        }

        public bool DecodeTexture2D(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            if (reader == null)
                throw new InvalidOperationException("This texture contains no image data.");
            if (reader.Size <= 0 || m_Width <= 0 || m_Height <= 0)
            {
                return false;
            }
            if (bytes.Length < outPutSize)
                throw new ArgumentException("The output buffer is too small for this texture.", nameof(bytes));

            var inputSize = reader.Size;
            var minimumInputSize = GetMinimumInputSize();
            if (inputSize < minimumInputSize)
                throw new InvalidDataException(
                    $"Texture format {m_TextureFormat} requires at least {minimumInputSize} image bytes, but only {inputSize} are available.");

            var buff = BigArrayPool<byte>.Shared.Rent(inputSize);
            try
            {
                reader.GetData(buff);
                switch (m_TextureFormat)
                {
                    case TextureFormat.Alpha8: //test pass
                        return DecodeAlpha8(buff, bytes);
                    case TextureFormat.ARGB4444: //test pass
                        SwapBytesForXbox(buff);
                        return DecodeARGB4444(buff, bytes);
                    case TextureFormat.RGB24: //test pass
                        return DecodeRGB24(buff, bytes);
                    case TextureFormat.RGBA32: //test pass
                        return DecodeRGBA32(buff, bytes);
                    case TextureFormat.ARGB32: //test pass
                        return DecodeARGB32(buff, bytes);
                    case TextureFormat.ARGBFloat:
                        return DecodeARGBFloat(buff, bytes);
                    case TextureFormat.BGR24:
                        return DecodeBGR24(buff, bytes);
                    case TextureFormat.RGB565: //test pass
                        SwapBytesForXbox(buff);
                        return DecodeRGB565(buff, bytes);
                    case TextureFormat.R16: //test pass
                        return DecodeR16(buff, bytes);
                    case TextureFormat.DXT1: //test pass
                        SwapBytesForXbox(buff);
                        return DecodeDXT1(buff, bytes);
                    case TextureFormat.DXT3:
                        return false;
                    case TextureFormat.DXT5: //test pass
                        SwapBytesForXbox(buff);
                        return DecodeDXT5(buff, bytes);
                    case TextureFormat.RGBA4444: //test pass
                        return DecodeRGBA4444(buff, bytes);
                    case TextureFormat.BGRA32: //test pass
                        return DecodeBGRA32(buff, bytes);
                    case TextureFormat.RHalf:
                        return DecodeRHalf(buff, bytes);
                    case TextureFormat.RGHalf:
                        return DecodeRGHalf(buff, bytes);
                    case TextureFormat.RGBAHalf: //test pass
                        return DecodeRGBAHalf(buff, bytes);
                    case TextureFormat.RFloat:
                        return DecodeRFloat(buff, bytes);
                    case TextureFormat.RGFloat:
                        return DecodeRGFloat(buff, bytes);
                    case TextureFormat.RGBAFloat:
                        return DecodeRGBAFloat(buff, bytes);
                    case TextureFormat.RGBFloat:
                        return DecodeRGBFloat(buff, bytes);
                    case TextureFormat.YUY2: //test pass
                        return DecodeYUY2(buff, bytes);
                    case TextureFormat.RGB9e5Float: //test pass
                        return DecodeRGB9e5Float(buff, bytes);
                    case TextureFormat.BC6H: //test pass
                        return DecodeBC6H(buff, bytes);
                    case TextureFormat.BC7: //test pass
                        return DecodeBC7(buff, bytes);
                    case TextureFormat.BC4: //test pass
                        return DecodeBC4(buff, bytes);
                    case TextureFormat.BC5: //test pass
                        return DecodeBC5(buff, bytes);
                    case TextureFormat.DXT1Crunched: //test pass
                        return DecodeDXT1Crunched(buff, bytes);
                    case TextureFormat.DXT5Crunched: //test pass
                        return DecodeDXT5Crunched(buff, bytes);
                    case TextureFormat.PVRTC_RGB2: //test pass
                    case TextureFormat.PVRTC_RGBA2: //test pass
                        return DecodePVRTC(buff, bytes, true);
                    case TextureFormat.PVRTC_RGB4: //test pass
                    case TextureFormat.PVRTC_RGBA4: //test pass
                        return DecodePVRTC(buff, bytes, false);
                    case TextureFormat.ETC_RGB4: //test pass
                    case TextureFormat.ETC_RGB4_3DS:
                        return DecodeETC1(buff, bytes);
                    case TextureFormat.ATC_RGB4: //test pass
                        return DecodeATCRGB4(buff, bytes);
                    case TextureFormat.ATC_RGBA8: //test pass
                        return DecodeATCRGBA8(buff, bytes);
                    case TextureFormat.EAC_R: //test pass
                        return DecodeEACR(buff, bytes);
                    case TextureFormat.EAC_R_SIGNED:
                        return DecodeEACRSigned(buff, bytes);
                    case TextureFormat.EAC_RG: //test pass
                        return DecodeEACRG(buff, bytes);
                    case TextureFormat.EAC_RG_SIGNED:
                        return DecodeEACRGSigned(buff, bytes);
                    case TextureFormat.ETC2_RGB: //test pass
                        return DecodeETC2(buff, bytes);
                    case TextureFormat.ETC2_RGBA1: //test pass
                        return DecodeETC2A1(buff, bytes);
                    case TextureFormat.ETC2_RGBA8: //test pass
                    case TextureFormat.ETC_RGBA8_3DS:
                        return DecodeETC2A8(buff, bytes);
                    case TextureFormat.ASTC_RGB_4x4: //test pass
                    case TextureFormat.ASTC_RGBA_4x4: //test pass
                    case TextureFormat.ASTC_HDR_4x4: //test pass
                        return DecodeASTC(buff, bytes, 4);
                    case TextureFormat.ASTC_RGB_5x5: //test pass
                    case TextureFormat.ASTC_RGBA_5x5: //test pass
                    case TextureFormat.ASTC_HDR_5x5: //test pass
                        return DecodeASTC(buff, bytes, 5);
                    case TextureFormat.ASTC_RGB_6x6: //test pass
                    case TextureFormat.ASTC_RGBA_6x6: //test pass
                    case TextureFormat.ASTC_HDR_6x6: //test pass
                        return DecodeASTC(buff, bytes, 6);
                    case TextureFormat.ASTC_RGB_8x8: //test pass
                    case TextureFormat.ASTC_RGBA_8x8: //test pass
                    case TextureFormat.ASTC_HDR_8x8: //test pass
                        return DecodeASTC(buff, bytes, 8);
                    case TextureFormat.ASTC_RGB_10x10: //test pass
                    case TextureFormat.ASTC_RGBA_10x10: //test pass
                    case TextureFormat.ASTC_HDR_10x10: //test pass
                        return DecodeASTC(buff, bytes, 10);
                    case TextureFormat.ASTC_RGB_12x12: //test pass
                    case TextureFormat.ASTC_RGBA_12x12: //test pass
                    case TextureFormat.ASTC_HDR_12x12: //test pass
                        return DecodeASTC(buff, bytes, 12);
                    case TextureFormat.RG16: //test pass
                        return DecodeRG16(buff, bytes);
                    case TextureFormat.R8: //test pass
                        return DecodeR8(buff, bytes);
                    case TextureFormat.ETC_RGB4Crunched: //test pass
                        return DecodeETC1Crunched(buff, bytes);
                    case TextureFormat.ETC2_RGBA8Crunched: //test pass
                        return DecodeETC2A8Crunched(buff, bytes);
                    case TextureFormat.RG32: //test pass
                        return DecodeRG32(buff, bytes);
                    case TextureFormat.RGB48: //test pass
                        return DecodeRGB48(buff, bytes);
                    case TextureFormat.RGBA64: //test pass
                        return DecodeRGBA64(buff, bytes);
                    default:
                        return false;
                }
            }
            finally
            {
                BigArrayPool<byte>.Shared.Return(buff);
            }
        }

        private long GetMinimumInputSize()
        {
            var width = (long)m_Width;
            var height = (long)m_Height;
            var pixels = checked(width * height);
            return m_TextureFormat switch
            {
                TextureFormat.Alpha8 or TextureFormat.R8 => pixels,
                TextureFormat.ARGB4444 or TextureFormat.RGBA4444 or TextureFormat.RGB565
                    or TextureFormat.R16 or TextureFormat.RHalf or TextureFormat.RG16
                    => checked(pixels * 2),
                TextureFormat.RGB24 or TextureFormat.BGR24 => checked(pixels * 3),
                TextureFormat.RGBA32 or TextureFormat.ARGB32 or TextureFormat.BGRA32
                    or TextureFormat.RGHalf or TextureFormat.RFloat or TextureFormat.RG32
                    or TextureFormat.RGB9e5Float => checked(pixels * 4),
                TextureFormat.RGBAHalf or TextureFormat.RGFloat or TextureFormat.RGBA64
                    => checked(pixels * 8),
                TextureFormat.ARGBFloat or TextureFormat.RGBAFloat => checked(pixels * 16),
                TextureFormat.RGBFloat => checked(pixels * 12),
                TextureFormat.RGB48 => checked(pixels * 6),
                TextureFormat.YUY2 => checked(((width + 1) / 2) * height * 4),
                TextureFormat.DXT1 or TextureFormat.BC4 or TextureFormat.ETC_RGB4
                    or TextureFormat.ETC_RGB4_3DS or TextureFormat.ETC2_RGB
                    or TextureFormat.ETC2_RGBA1 or TextureFormat.EAC_R
                    or TextureFormat.EAC_R_SIGNED or TextureFormat.ATC_RGB4
                    => GetBlockCompressedSize(width, height, 4, 4, 8),
                TextureFormat.DXT3 or TextureFormat.DXT5 or TextureFormat.BC5
                    or TextureFormat.BC6H or TextureFormat.BC7 or TextureFormat.ATC_RGBA8
                    or TextureFormat.EAC_RG or TextureFormat.EAC_RG_SIGNED
                    or TextureFormat.ETC2_RGBA8 or TextureFormat.ETC_RGBA8_3DS
                    => GetBlockCompressedSize(width, height, 4, 4, 16),
                TextureFormat.PVRTC_RGB2 or TextureFormat.PVRTC_RGBA2
                    => GetPvrtcDataSize(width, height, 2),
                TextureFormat.PVRTC_RGB4 or TextureFormat.PVRTC_RGBA4
                    => GetPvrtcDataSize(width, height, 4),
                TextureFormat.DXT1Crunched or TextureFormat.DXT5Crunched
                    or TextureFormat.ETC_RGB4Crunched or TextureFormat.ETC2_RGBA8Crunched
                    => 1,
                TextureFormat.ASTC_RGB_4x4 or TextureFormat.ASTC_RGBA_4x4 or TextureFormat.ASTC_HDR_4x4
                    => GetBlockCompressedSize(width, height, 4, 4, 16),
                TextureFormat.ASTC_RGB_5x5 or TextureFormat.ASTC_RGBA_5x5 or TextureFormat.ASTC_HDR_5x5
                    => GetBlockCompressedSize(width, height, 5, 5, 16),
                TextureFormat.ASTC_RGB_6x6 or TextureFormat.ASTC_RGBA_6x6 or TextureFormat.ASTC_HDR_6x6
                    => GetBlockCompressedSize(width, height, 6, 6, 16),
                TextureFormat.ASTC_RGB_8x8 or TextureFormat.ASTC_RGBA_8x8 or TextureFormat.ASTC_HDR_8x8
                    => GetBlockCompressedSize(width, height, 8, 8, 16),
                TextureFormat.ASTC_RGB_10x10 or TextureFormat.ASTC_RGBA_10x10 or TextureFormat.ASTC_HDR_10x10
                    => GetBlockCompressedSize(width, height, 10, 10, 16),
                TextureFormat.ASTC_RGB_12x12 or TextureFormat.ASTC_RGBA_12x12 or TextureFormat.ASTC_HDR_12x12
                    => GetBlockCompressedSize(width, height, 12, 12, 16),
                _ => 0
            };
        }

        private static long GetBlockCompressedSize(long width, long height, int blockWidth, int blockHeight, int bytesPerBlock) =>
            checked(((width + blockWidth - 1) / blockWidth)
                * ((height + blockHeight - 1) / blockHeight)
                * bytesPerBlock);

        private static long GetPvrtcDataSize(long width, long height, int bitsPerPixel)
        {
            var blockWidth = bitsPerPixel == 2 ? 8 : 4;
            var blocksWide = Math.Max(2L, (width + blockWidth - 1) / blockWidth);
            var blocksHigh = Math.Max(2L, (height + 3) / 4);
            return checked(blocksWide * blocksHigh * 8);
        }

        private void SwapBytesForXbox(byte[] image_data)
        {
            if (platform == BuildTarget.XBOX360)
            {
                for (var i = 0; i < reader.Size / 2; i++)
                {
                    var b = image_data[i * 2];
                    image_data[i * 2] = image_data[i * 2 + 1];
                    image_data[i * 2 + 1] = b;
                }
            }
        }

        private bool DecodeAlpha8(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            var span = new Span<byte>(buff);
            span.Fill(0xFF);
            for (var i = 0; i < size; i++)
            {
                buff[i * 4 + 3] = image_data[i];
            }
            return true;
        }

        private bool DecodeARGB4444(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            var pixelNew = new byte[4];
            for (var i = 0; i < size; i++)
            {
                var pixelOldShort = BitConverter.ToUInt16(image_data, i * 2);
                pixelNew[0] = (byte)(pixelOldShort & 0x000f);
                pixelNew[1] = (byte)((pixelOldShort & 0x00f0) >> 4);
                pixelNew[2] = (byte)((pixelOldShort & 0x0f00) >> 8);
                pixelNew[3] = (byte)((pixelOldShort & 0xf000) >> 12);
                for (var j = 0; j < 4; j++)
                    pixelNew[j] = (byte)((pixelNew[j] << 4) | pixelNew[j]);
                pixelNew.CopyTo(buff, i * 4);
            }
            return true;
        }

        private bool DecodeRGB24(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = image_data[i * 3 + 2];
                buff[i * 4 + 1] = image_data[i * 3 + 1];
                buff[i * 4 + 2] = image_data[i * 3 + 0];
                buff[i * 4 + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGBA32(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = image_data[i + 2];
                buff[i + 1] = image_data[i + 1];
                buff[i + 2] = image_data[i + 0];
                buff[i + 3] = image_data[i + 3];
            }
            return true;
        }

        private bool DecodeARGB32(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = image_data[i + 3];
                buff[i + 1] = image_data[i + 2];
                buff[i + 2] = image_data[i + 1];
                buff[i + 3] = image_data[i + 0];
            }
            return true;
        }

        private bool DecodeARGBFloat(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                var sourceOffset = i * 4;
                buff[i] = ToLdrByte(BitConverter.ToSingle(image_data, sourceOffset + 12));
                buff[i + 1] = ToLdrByte(BitConverter.ToSingle(image_data, sourceOffset + 8));
                buff[i + 2] = ToLdrByte(BitConverter.ToSingle(image_data, sourceOffset + 4));
                buff[i + 3] = ToLdrByte(BitConverter.ToSingle(image_data, sourceOffset));
            }
            return true;
        }

        private bool DecodeBGR24(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = image_data[i * 3];
                buff[i * 4 + 1] = image_data[i * 3 + 1];
                buff[i * 4 + 2] = image_data[i * 3 + 2];
                buff[i * 4 + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGB565(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                var p = BitConverter.ToUInt16(image_data, i * 2);
                buff[i * 4] = (byte)((p << 3) | (p >> 2 & 7));
                buff[i * 4 + 1] = (byte)((p >> 3 & 0xfc) | (p >> 9 & 3));
                buff[i * 4 + 2] = (byte)((p >> 8 & 0xf8) | (p >> 13));
                buff[i * 4 + 3] = 255;
            }
            return true;
        }

        private bool DecodeR16(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = 0; //b
                buff[i * 4 + 1] = 0; //g
                buff[i * 4 + 2] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2)); //r
                buff[i * 4 + 3] = 255; //a
            }
            return true;
        }

        private bool DecodeDXT1(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeDXT1(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeDXT5(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeDXT5(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeRGBA4444(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            var pixelNew = new byte[4];
            for (var i = 0; i < size; i++)
            {
                var pixelOldShort = BitConverter.ToUInt16(image_data, i * 2);
                pixelNew[0] = (byte)((pixelOldShort & 0x00f0) >> 4);
                pixelNew[1] = (byte)((pixelOldShort & 0x0f00) >> 8);
                pixelNew[2] = (byte)((pixelOldShort & 0xf000) >> 12);
                pixelNew[3] = (byte)(pixelOldShort & 0x000f);
                for (var j = 0; j < 4; j++)
                    pixelNew[j] = (byte)((pixelNew[j] << 4) | pixelNew[j]);
                pixelNew.CopyTo(buff, i * 4);
            }
            return true;
        }

        private bool DecodeBGRA32(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = image_data[i];
                buff[i + 1] = image_data[i + 1];
                buff[i + 2] = image_data[i + 2];
                buff[i + 3] = image_data[i + 3];
            }
            return true;
        }

        private bool DecodeRHalf(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = 0;
                buff[i + 2] = ToLdrByte(Half.ToHalf(image_data, i / 2));
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGHalf(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = ToLdrByte(Half.ToHalf(image_data, i + 2));
                buff[i + 2] = ToLdrByte(Half.ToHalf(image_data, i));
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGBAHalf(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = ToLdrByte(Half.ToHalf(image_data, i * 2 + 4));
                buff[i + 1] = ToLdrByte(Half.ToHalf(image_data, i * 2 + 2));
                buff[i + 2] = ToLdrByte(Half.ToHalf(image_data, i * 2));
                buff[i + 3] = ToLdrByte(Half.ToHalf(image_data, i * 2 + 6));
            }
            return true;
        }

        private bool DecodeRFloat(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = 0;
                buff[i + 2] = ToLdrByte(BitConverter.ToSingle(image_data, i));
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGFloat(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;
                buff[i + 1] = ToLdrByte(BitConverter.ToSingle(image_data, i * 2 + 4));
                buff[i + 2] = ToLdrByte(BitConverter.ToSingle(image_data, i * 2));
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeRGBAFloat(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = ToLdrByte(BitConverter.ToSingle(image_data, i * 4 + 8));
                buff[i + 1] = ToLdrByte(BitConverter.ToSingle(image_data, i * 4 + 4));
                buff[i + 2] = ToLdrByte(BitConverter.ToSingle(image_data, i * 4));
                buff[i + 3] = ToLdrByte(BitConverter.ToSingle(image_data, i * 4 + 12));
            }
            return true;
        }

        private bool DecodeRGBFloat(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                var sourceOffset = i * 3;
                buff[i] = ToLdrByte(BitConverter.ToSingle(image_data, sourceOffset + 8));
                buff[i + 1] = ToLdrByte(BitConverter.ToSingle(image_data, sourceOffset + 4));
                buff[i + 2] = ToLdrByte(BitConverter.ToSingle(image_data, sourceOffset));
                buff[i + 3] = 255;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte ClampByte(int x)
        {
            return (byte)(byte.MaxValue < x ? byte.MaxValue : (x > byte.MinValue ? x : byte.MinValue));
        }

        private static byte ToLdrByte(double value)
        {
            if (double.IsNaN(value))
                return 0;
            return (byte)Math.Round(Math.Clamp(value, 0d, 1d) * byte.MaxValue);
        }

        private bool DecodeYUY2(byte[] image_data, byte[] buff)
        {
            int p = 0;
            int o = 0;
            int pairsWide = (m_Width + 1) / 2;
            for (int j = 0; j < m_Height; j++)
            {
                for (int i = 0; i < pairsWide; ++i)
                {
                    int y0 = image_data[p++];
                    int u0 = image_data[p++];
                    int y1 = image_data[p++];
                    int v0 = image_data[p++];
                    WriteYuy2Pixel(y0, u0, v0, buff, ref o);
                    if (i * 2 + 1 < m_Width)
                        WriteYuy2Pixel(y1, u0, v0, buff, ref o);
                }
            }
            return true;
        }

        private static void WriteYuy2Pixel(int y, int u, int v, byte[] output, ref int offset)
        {
            var c = y - 16;
            var d = u - 128;
            var e = v - 128;
            output[offset++] = ClampByte((298 * c + 516 * d + 128) >> 8);
            output[offset++] = ClampByte((298 * c - 100 * d - 208 * e + 128) >> 8);
            output[offset++] = ClampByte((298 * c + 409 * e + 128) >> 8);
            output[offset++] = 255;
        }

        private bool DecodeRGB9e5Float(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                var n = BitConverter.ToInt32(image_data, i);
                var scale = n >> 27 & 0x1f;
                var scalef = Math.Pow(2, scale - 24);
                var b = n >> 18 & 0x1ff;
                var g = n >> 9 & 0x1ff;
                var r = n & 0x1ff;
                buff[i] = ToLdrByte(b * scalef);
                buff[i + 1] = ToLdrByte(g * scalef);
                buff[i + 2] = ToLdrByte(r * scalef);
                buff[i + 3] = 255;
            }
            return true;
        }

        private bool DecodeBC4(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeBC4(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeBC5(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeBC5(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeBC6H(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeBC6(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeBC7(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeBC7(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeDXT1Crunched(byte[] image_data, byte[] buff)
        {
            if (UnpackCrunch(image_data, out var result)
                && result != null
                && result.LongLength >= GetBlockCompressedSize(m_Width, m_Height, 4, 4, 8))
            {
                if (DecodeDXT1(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        private bool DecodeDXT5Crunched(byte[] image_data, byte[] buff)
        {
            if (UnpackCrunch(image_data, out var result)
                && result != null
                && result.LongLength >= GetBlockCompressedSize(m_Width, m_Height, 4, 4, 16))
            {
                if (DecodeDXT5(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        private bool DecodePVRTC(byte[] image_data, byte[] buff, bool is2bpp)
        {
            return TextureDecoder.DecodePVRTC(image_data, m_Width, m_Height, buff, is2bpp);
        }

        private bool DecodeETC1(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeETC1(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeATCRGB4(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeATCRGB4(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeATCRGBA8(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeATCRGBA8(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACR(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeEACR(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACRSigned(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeEACRSigned(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACRG(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeEACRG(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeEACRGSigned(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeEACRGSigned(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeETC2(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeETC2(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeETC2A1(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeETC2A1(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeETC2A8(byte[] image_data, byte[] buff)
        {
            return TextureDecoder.DecodeETC2A8(image_data, m_Width, m_Height, buff);
        }

        private bool DecodeASTC(byte[] image_data, byte[] buff, int blocksize)
        {
            return TextureDecoder.DecodeASTC(image_data, m_Width, m_Height, blocksize, blocksize, buff);
        }

        private bool DecodeRG16(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = 0; //B
                buff[i * 4 + 1] = image_data[i * 2 + 1];//G
                buff[i * 4 + 2] = image_data[i * 2];//R
                buff[i * 4 + 3] = 255;//A
            }
            return true;
        }

        private bool DecodeR8(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = 0; //B
                buff[i * 4 + 1] = 0; //G
                buff[i * 4 + 2] = image_data[i];//R
                buff[i * 4 + 3] = 255;//A
            }
            return true;
        }

        private bool DecodeETC1Crunched(byte[] image_data, byte[] buff)
        {
            if (UnpackCrunch(image_data, out var result)
                && result != null
                && result.LongLength >= GetBlockCompressedSize(m_Width, m_Height, 4, 4, 8))
            {
                if (DecodeETC1(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        private bool DecodeETC2A8Crunched(byte[] image_data, byte[] buff)
        {
            if (UnpackCrunch(image_data, out var result)
                && result != null
                && result.LongLength >= GetBlockCompressedSize(m_Width, m_Height, 4, 4, 16))
            {
                if (DecodeETC2A8(result, buff))
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte DownScaleFrom16BitTo8Bit(ushort component)
        {
            return (byte)(((component * 255) + 32895) >> 16);
        }

        private bool DecodeRG32(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = 0;                                                                          //b
                buff[i + 1] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i + 2));     //g
                buff[i + 2] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i));         //r
                buff[i + 3] = byte.MaxValue;                                                          //a
            }
            return true;
        }

        private bool DecodeRGB48(byte[] image_data, byte[] buff)
        {
            var size = m_Width * m_Height;
            for (var i = 0; i < size; i++)
            {
                buff[i * 4] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 6 + 4));     //b
                buff[i * 4 + 1] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 6 + 2)); //g
                buff[i * 4 + 2] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 6));     //r
                buff[i * 4 + 3] = byte.MaxValue;                                                          //a
            }
            return true;
        }

        private bool DecodeRGBA64(byte[] image_data, byte[] buff)
        {
            for (var i = 0; i < outPutSize; i += 4)
            {
                buff[i] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2 + 4));     //b
                buff[i + 1] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2 + 2)); //g
                buff[i + 2] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2));     //r
                buff[i + 3] = DownScaleFrom16BitTo8Bit(BitConverter.ToUInt16(image_data, i * 2 + 6)); //a
            }
            return true;
        }

        private bool UnpackCrunch(byte[] image_data, out byte[] result)
        {
            var isUnityCrunch = m_TextureFormat is TextureFormat.ETC_RGB4Crunched or TextureFormat.ETC2_RGBA8Crunched
                || version != null && version.Length >= 2
                    && (version[0] > 2017 || version[0] == 2017 && version[1] >= 3);
            result = isUnityCrunch
                ? TextureDecoder.UnpackUnityCrunch(image_data, reader.Size)
                : TextureDecoder.UnpackCrunch(image_data, reader.Size);
            return result != null;
        }
    }
}
