using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
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
}
