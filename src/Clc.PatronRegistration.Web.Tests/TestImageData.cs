using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Clc.PatronRegistration.Tests;

internal static class TestImageData
{
    public static byte[] Create(string contentType)
    {
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = new Rgba32(0x33, 0x66, 0x99, 0xff);
        using var stream = new MemoryStream();
        switch (contentType)
        {
            case "image/png":
                image.SaveAsPng(stream);
                break;
            case "image/jpeg":
                image.SaveAsJpeg(stream, new JpegEncoder { Quality = 90 });
                break;
            case "image/webp":
                image.SaveAsWebp(stream, new WebpEncoder { Quality = 90 });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(contentType));
        }
        return stream.ToArray();
    }

    public static byte[] CreateAnimatedWebp()
    {
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = new Rgba32(0x33, 0x66, 0x99, 0xff);
        image.Metadata.GetWebpMetadata().RepeatCount = 0;
        image.Frames.RootFrame.Metadata.GetWebpMetadata().FrameDelay = 100;
        var secondFrame = image.Frames.AddFrame(image.Frames.RootFrame);
        secondFrame[0, 0] = new Rgba32(0xcc, 0x66, 0x33, 0xff);
        secondFrame.Metadata.GetWebpMetadata().FrameDelay = 100;
        using var stream = new MemoryStream();
        image.SaveAsWebp(stream, new WebpEncoder { Quality = 90 });
        return stream.ToArray();
    }

    public static byte[] CreateAnimatedWebpWithCanvas(int width, int height)
    {
        if (width is < 1 or > 0x1000000 || height is < 1 or > 0x1000000)
        {
            throw new ArgumentOutOfRangeException();
        }

        var content = CreateAnimatedWebp();
        var vp8xOffset = FindChunk(content, "VP8X"u8);
        WriteUInt24LittleEndian(content, vp8xOffset + 12, (uint)(width - 1));
        WriteUInt24LittleEndian(content, vp8xOffset + 15, (uint)(height - 1));
        return content;
    }

    public static byte[] CreateAnimatedWebpWithOversizedVp8x()
    {
        var content = CreateAnimatedWebp();
        var vp8xOffset = FindChunk(content, "VP8X"u8);
        var originalSize = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(vp8xOffset + 4, 4));
        if (originalSize != 10)
        {
            throw new InvalidDataException($"The generated WebP fixture has VP8X size {originalSize}, not 10.");
        }

        // Keep the physical ANIM/ANMF chunks in place, but make the declared
        // VP8X extent cover them. ImageSharp reads the fixed VP8X fields and
        // still sees those physical chunks; the pre-decode scanner must reject
        // the malformed extent instead of skipping them.
        content[vp8xOffset + 8] &= unchecked((byte)~0x02);
        var oversized = content.Length - vp8xOffset - 8;
        if ((oversized & 1) != 0)
        {
            Array.Resize(ref content, content.Length + 1);
            content[^1] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(4, 4), (uint)(content.Length - 8));
            oversized++;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(vp8xOffset + 4, 4), (uint)oversized);
        return content;
    }

    public static byte[] CreateWebpWithUnderstatedRiffSize()
    {
        var content = Create("image/webp");
        var originalLength = content.Length;
        var result = new byte[originalLength + 12];
        Buffer.BlockCopy(content, 0, result, 0, originalLength);
        "JUNK"u8.CopyTo(result.AsSpan(originalLength, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(originalLength + 4, 4), 4);
        result[originalLength + 8] = 1;
        result[originalLength + 9] = 2;
        result[originalLength + 10] = 3;
        result[originalLength + 11] = 4;
        // Leave the original RIFF size untouched so it understates result.Length.
        return result;
    }

    public static byte[] CreateAnimatedPng()
    {
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = new Rgba32(0x33, 0x66, 0x99, 0xff);
        image.Metadata.GetPngMetadata().RepeatCount = 0;
        image.Frames.RootFrame.Metadata.GetPngMetadata().FrameDelay = new Rational(100U);
        var secondFrame = image.Frames.AddFrame(image.Frames.RootFrame);
        secondFrame[0, 0] = new Rgba32(0xcc, 0x66, 0x33, 0xff);
        secondFrame.Metadata.GetPngMetadata().FrameDelay = new Rational(100U);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    public static byte[] CreatePngWithDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException();
        }

        var content = Create("image/png");
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(16, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(20, 4), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(29, 4), ComputePngCrc(content.AsSpan(12, 17)));
        return content;
    }

    private static int FindChunk(byte[] content, ReadOnlySpan<byte> type)
    {
        var offset = 12;
        while (offset <= content.Length - 8)
        {
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(offset + 4, 4));
            if (content.AsSpan(offset, 4).SequenceEqual(type))
            {
                if (chunkSize < 10 || (ulong)offset + 8UL + chunkSize > (ulong)content.Length)
                {
                    throw new InvalidDataException("The generated WebP test fixture has an invalid VP8X chunk.");
                }
                return offset;
            }

            var next = (ulong)offset + 8UL + chunkSize + (chunkSize & 1U);
            if (next > (ulong)content.Length)
            {
                break;
            }
            offset = (int)next;
        }

        throw new InvalidDataException("The generated WebP test fixture has no VP8X chunk.");
    }

    private static void WriteUInt24LittleEndian(byte[] content, int offset, uint value)
    {
        content[offset] = (byte)value;
        content[offset + 1] = (byte)(value >> 8);
        content[offset + 2] = (byte)(value >> 16);
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> content)
    {
        var crc = 0xffffffffU;
        foreach (var value in content)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xedb88320U & (uint)-(int)(crc & 1));
            }
        }
        return ~crc;
    }
}
