using System.Buffers.Binary;

namespace Auction.Api.Images;

/// <summary>An accepted image: its real type, read from the file's own bytes, and its pixel size.</summary>
public sealed record ImageInfo(string ContentType, int Width, int Height);

/// <summary>
/// Recognises JPEG, PNG and WebP by their file signatures, never by a name or a claimed type, and reads
/// their dimensions from the headers. Nothing is decoded or re-encoded; anything else is refused.
/// </summary>
public static class ImageFormat
{
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string WebP = "image/webp";

    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal) { Jpeg, Png, WebP };

    public static ImageInfo? Identify(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 24 && data[..8].SequenceEqual(PngSignature) && data[12..16].SequenceEqual("IHDR"u8))
        {
            return new ImageInfo(Png, BinaryPrimitives.ReadInt32BigEndian(data[16..20]), BinaryPrimitives.ReadInt32BigEndian(data[20..24]));
        }

        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return JpegSize(data) is { } size ? new ImageInfo(Jpeg, size.Width, size.Height) : null;
        }

        if (data.Length >= 30 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return WebPSize(data) is { } size ? new ImageInfo(WebP, size.Width, size.Height) : null;
        }

        return null;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Walks the JPEG segments to the first start-of-frame marker, which holds the height and width.
    private static (int Width, int Height)? JpegSize(ReadOnlySpan<byte> data)
    {
        var at = 2;
        while (at + 9 < data.Length)
        {
            if (data[at] != 0xFF)
            {
                return null;
            }

            var marker = data[at + 1];
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
            var isFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isFrame)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(data[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(data[(at + 5)..]));
            }

            at += 2 + length;
        }

        return null;
    }

    // The three WebP layouts keep their size in different places: lossy (VP8), lossless (VP8L), extended (VP8X).
    private static (int Width, int Height)? WebPSize(ReadOnlySpan<byte> data)
    {
        var chunk = data[12..16];
        if (chunk.SequenceEqual("VP8 "u8) && data[23] == 0x9D && data[24] == 0x01 && data[25] == 0x2A)
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(data[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(data[28..]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8) && data[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            return (ReadUInt24(data[24..]) + 1, ReadUInt24(data[27..]) + 1);
        }

        return null;
    }

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
}
