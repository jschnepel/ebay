using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;

namespace Auction.Api.Images;

public sealed record ImageInfo(string ContentType, int Width, int Height);

internal sealed record ListingPhoto(ImageInfo Info, byte[] Data);

/// <summary>Only pixels from a bounded decode are used to produce a listing photo.</summary>
public static class ImageFormat
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";

    private static readonly DecoderOptions Options = CreateOptions();
    private static readonly SemaphoreSlim Slots = new(2, 2);

    internal static async Task<ListingPhoto?> NormalizeAsync(byte[] data, string? claimed, CancellationToken cancellationToken)
    {
        await Slots.WaitAsync(cancellationToken);
        try
        {
            var format = Image.DetectFormat(Options, data);
            var mime = format.DefaultMimeType;
            if (mime is not (Jpeg or Png or WebP) || (claimed is not null && claimed != mime))
            {
                return null;
            }

            var info = Image.Identify(Options, data);
            if (info.Width is <= 0 or > 4096 || info.Height is <= 0 or > 4096 || (long)info.Width * info.Height > 4_000_000)
            {
                return null;
            }

            using var pixels = Image.Load<Rgba32>(Options, data);
            if (pixels.Width != info.Width || pixels.Height != info.Height)
            {
                return null;
            }

            using var output = new MemoryStream();
            await pixels.SaveAsync(output, format, cancellationToken);
            return output.Length <= ImageOptions.MaxImageBytes
                ? new ListingPhoto(new ImageInfo(mime, pixels.Width, pixels.Height), output.ToArray())
                : null;
        }
        catch (Exception e) when (e is ImageFormatException or InvalidMemoryOperationException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            Slots.Release();
        }
    }

    private static DecoderOptions CreateOptions()
    {
        var config = Configuration.Default.Clone();
        config.MaxDegreeOfParallelism = 1;
        config.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = 64, MaximumPoolSizeMegabytes = 16 });
        return new DecoderOptions { Configuration = config, SkipMetadata = true, MaxFrames = 1 };
    }
}
