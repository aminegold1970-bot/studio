using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;

namespace AssetStudioMobile;

internal static class ManagedDxtDecoder
{
    public static byte[] DecodeDxt1(
        byte[] source,
        int width,
        int height,
        CancellationToken cancellationToken) =>
        Decode(source, width, height, 8, DxtFormat.Dxt1, cancellationToken);

    public static byte[] DecodeDxt3(
        byte[] source,
        int width,
        int height,
        CancellationToken cancellationToken) =>
        Decode(source, width, height, 16, DxtFormat.Dxt3, cancellationToken);

    public static byte[] DecodeDxt5(
        byte[] source,
        int width,
        int height,
        CancellationToken cancellationToken) =>
        Decode(source, width, height, 16, DxtFormat.Dxt5, cancellationToken);

    private static byte[] Decode(
        byte[] source,
        int width,
        int height,
        int bytesPerBlock,
        DxtFormat format,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("Compressed texture dimensions must be positive.");

        var blocksWide = checked((width + 3) / 4);
        var blocksHigh = checked((height + 3) / 4);
        var requiredBytes = checked((long)blocksWide * blocksHigh * bytesPerBlock);
        if (requiredBytes > source.Length)
            throw new InvalidDataException("Compressed texture data is shorter than its dimensions require.");

        var output = new byte[checked(width * height * 4)];
        var colorPalette = new byte[16];
        var alphaPalette = new byte[8];
        var blockIndex = 0;
        for (var blockY = 0; blockY < blocksHigh; blockY++)
        {
            for (var blockX = 0; blockX < blocksWide; blockX++, blockIndex++)
            {
                if ((blockIndex & 255) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                var blockOffset = checked(blockIndex * bytesPerBlock);
                var colorOffset = format == DxtFormat.Dxt1 ? blockOffset : blockOffset + 8;
                DecodeColorPalette(
                    source.AsSpan(colorOffset, 8),
                    format == DxtFormat.Dxt1,
                    colorPalette);
                var colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(
                    source.AsSpan(colorOffset + 4, 4));
                ulong alphaIndices = 0;
                if (format == DxtFormat.Dxt3)
                {
                    alphaIndices = BinaryPrimitives.ReadUInt64LittleEndian(
                        source.AsSpan(blockOffset, 8));
                }
                else if (format == DxtFormat.Dxt5)
                {
                    DecodeDxt5AlphaPalette(source[blockOffset], source[blockOffset + 1], alphaPalette);
                    alphaIndices = BinaryPrimitives.ReadUInt64LittleEndian(
                        source.AsSpan(blockOffset, 8)) >> 16;
                }

                for (var pixel = 0; pixel < 16; pixel++)
                {
                    var x = blockX * 4 + pixel % 4;
                    var y = blockY * 4 + pixel / 4;
                    if (x >= width || y >= height)
                        continue;

                    var colorIndex = (int)((colorIndices >> (pixel * 2)) & 3);
                    var outputOffset = checked((y * width + x) * 4);
                    var paletteOffset = colorIndex * 4;
                    output[outputOffset] = colorPalette[paletteOffset];
                    output[outputOffset + 1] = colorPalette[paletteOffset + 1];
                    output[outputOffset + 2] = colorPalette[paletteOffset + 2];
                    output[outputOffset + 3] = format switch
                    {
                        DxtFormat.Dxt1 => colorPalette[paletteOffset + 3],
                        DxtFormat.Dxt3 => (byte)(((alphaIndices >> (pixel * 4)) & 0xF) * 17),
                        _ => alphaPalette[(int)((alphaIndices >> (pixel * 3)) & 7)]
                    };
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output;
    }

    private static void DecodeColorPalette(
        ReadOnlySpan<byte> block,
        bool allowTransparency,
        byte[] palette)
    {
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        SetColor(palette, 0, color0, 255);
        SetColor(palette, 1, color1, 255);

        if (!allowTransparency || color0 > color1)
        {
            InterpolateColor(palette, 2, 0, 1, 2, 1, 3);
            InterpolateColor(palette, 3, 0, 1, 1, 2, 3);
            palette[15] = 255;
            palette[11] = 255;
            return;
        }

        InterpolateColor(palette, 2, 0, 1, 1, 1, 2);
        palette[12] = 0;
        palette[13] = 0;
        palette[14] = 0;
        palette[15] = 0;
    }

    private static void SetColor(byte[] palette, int index, ushort color, byte alpha)
    {
        var offset = index * 4;
        palette[offset] = Expand5(color & 0x1F);
        palette[offset + 1] = Expand6((color >> 5) & 0x3F);
        palette[offset + 2] = Expand5((color >> 11) & 0x1F);
        palette[offset + 3] = alpha;
    }

    private static void InterpolateColor(
        byte[] palette,
        int destination,
        int color0,
        int color1,
        int weight0,
        int weight1,
        int divisor)
    {
        var outputOffset = destination * 4;
        var color0Offset = color0 * 4;
        var color1Offset = color1 * 4;
        for (var channel = 0; channel < 3; channel++)
        {
            palette[outputOffset + channel] = (byte)(
                (palette[color0Offset + channel] * weight0
                    + palette[color1Offset + channel] * weight1) / divisor);
        }
        palette[outputOffset + 3] = 255;
    }

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));
    private static byte Expand6(int value) => (byte)((value << 2) | (value >> 4));

    private static void DecodeDxt5AlphaPalette(byte alpha0, byte alpha1, byte[] palette)
    {
        palette[0] = alpha0;
        palette[1] = alpha1;
        if (alpha0 > alpha1)
        {
            for (var index = 1; index <= 6; index++)
                palette[index + 1] = (byte)(((7 - index) * alpha0 + index * alpha1) / 7);
            return;
        }

        for (var index = 1; index <= 4; index++)
            palette[index + 1] = (byte)(((5 - index) * alpha0 + index * alpha1) / 5);
        palette[6] = 0;
        palette[7] = 255;
    }

    private enum DxtFormat
    {
        Dxt1,
        Dxt3,
        Dxt5
    }
}
