using System.Security.Cryptography;
using SkiaSharp;

namespace Auction.IntegrationTests.Api;

/// <summary>Decodable photos with distinct pixels, including incompressible samples for storage quotas.</summary>
internal static class TestImages
{
    public static byte[] Png(int extraBytes = 0)
    {
        if (extraBytes > ImageQuotaSample)
        {
            return [.. Encode(SKEncodedImageFormat.Png, 4), .. new byte[extraBytes]];
        }

        return Encode(SKEncodedImageFormat.Png, extraBytes == 0 ? 4 : 26);
    }

    private const int ImageQuotaSample = 1500;

    public static byte[] Jpeg() => Encode(SKEncodedImageFormat.Jpeg, 4);

    public static byte[] WebP() => Encode(SKEncodedImageFormat.Webp, 4);

    public static byte[] NotAnImage() => "just text, named .png"u8.ToArray();

    public static object Upload(byte[] data, string contentType) => new { contentType, dataBase64 = Convert.ToBase64String(data) };

    private static byte[] Encode(SKEncodedImageFormat format, int side)
    {
        using var image = new SKBitmap(side, side, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = image.GetPixelSpan();
        RandomNumberGenerator.Fill(pixels);
        for (var alpha = 3; alpha < pixels.Length; alpha += 4)
        {
            pixels[alpha] = byte.MaxValue;
        }

        using var data = image.Encode(format, 90);
        return data.ToArray();
    }
}
