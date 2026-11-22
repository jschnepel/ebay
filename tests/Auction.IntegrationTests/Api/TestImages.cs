using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Auction.IntegrationTests.Api;

/// <summary>Decodable photos with distinct pixels, including incompressible samples for storage quotas.</summary>
internal static class TestImages
{
    public static byte[] Png(int extraBytes = 0)
    {
        if (extraBytes > ImageQuotaSample)
        {
            return [.. Encode(new PngEncoder(), 4), .. new byte[extraBytes]];
        }

        return Encode(new PngEncoder(), extraBytes == 0 ? 4 : 23);
    }

    private const int ImageQuotaSample = 1500;

    public static byte[] Jpeg() => Encode(new JpegEncoder(), 4);

    public static byte[] WebP() => Encode(new WebpEncoder(), 4);

    public static byte[] NotAnImage() => "just text, named .png"u8.ToArray();

    public static object Upload(byte[] data, string contentType) => new { contentType, dataBase64 = Convert.ToBase64String(data) };

    private static byte[] Encode(IImageEncoder encoder, int side)
    {
        using var image = Image.LoadPixelData<Rgba32>(RandomNumberGenerator.GetBytes(side * side * 4), side, side);
        using var output = new MemoryStream();
        image.Save(output, encoder);
        return output.ToArray();
    }
}
