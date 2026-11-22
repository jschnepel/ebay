using SkiaSharp;

namespace Auction.Api.Images;

public sealed record ImageInfo(string ContentType, int Width, int Height);

internal sealed record ListingPhoto(ImageInfo Info, byte[] Data);

/// <summary>Only pixels from a bounded decode are used to produce a listing photo.</summary>
public static class ImageFormat
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";

    private static readonly SemaphoreSlim Slots = new(2, 2);

    internal static async Task<ListingPhoto?> NormalizeAsync(byte[] data, string? claimed, CancellationToken cancellationToken)
    {
        if (data.Length is 0 or > ImageOptions.MaxImageBytes)
        {
            return null;
        }

        await Slots.WaitAsync(cancellationToken);
        try
        {
            using var input = SKData.CreateCopy(data);
            using var codec = SKCodec.Create(input);
            var mime = codec?.EncodedFormat switch
            {
                SKEncodedImageFormat.Jpeg => Jpeg,
                SKEncodedImageFormat.Png => Png,
                SKEncodedImageFormat.Webp => WebP,
                _ => null,
            };
            if (codec is null || mime is null || (claimed is not null && claimed != mime))
            {
                return null;
            }

            var (width, height) = (codec.Info.Width, codec.Info.Height);
            if (width is <= 0 or > 4096 || height is <= 0 or > 4096 || (long)width * height > 4_000_000)
            {
                return null;
            }

            var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var pixels = new SKBitmap(info);
            if (codec.GetPixels(info, pixels.GetPixels()) != SKCodecResult.Success)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var output = pixels.Encode(codec.EncodedFormat, 90);
            return output is { Size: > 0 and <= ImageOptions.MaxImageBytes }
                ? new ListingPhoto(new ImageInfo(mime, width, height), output.ToArray())
                : null;
        }
        finally
        {
            Slots.Release();
        }
    }
}
