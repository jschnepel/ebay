using System.Buffers.Binary;

namespace Auction.IntegrationTests.Api;

/// <summary>
/// Small images with real headers, each with a unique tail so no two uploads share content (the server
/// stores the same bytes from the same owner once). The server reads only signatures and headers.
/// </summary>
internal static class TestImages
{
    // A complete, decodable 1×1 PNG, so a browser can show it.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    public static byte[] Png(int extraBytes = 0) => [.. OnePixelPng, .. Unique(extraBytes)];

    public static byte[] Jpeg(int extraBytes = 0)
    {
        byte[] header =
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x02, 0x00, 0x03, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        ];
        return [.. header, .. Unique(extraBytes), 0xFF, 0xD9];
    }

    public static byte[] WebP(int extraBytes = 0)
    {
        var bits = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bits, (uint)((4 - 1) | ((5 - 1) << 14)));
        byte[] body = [.. "VP8L"u8, 0x05, 0x00, 0x00, 0x00, 0x2F, .. bits, .. Unique(extraBytes)];
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(body.Length + 4));
        return [.. "RIFF"u8, .. size, .. "WEBP"u8, .. body];
    }

    /// <summary>Bytes that claim nothing: no image signature at all.</summary>
    public static byte[] NotAnImage() => [.. "just text, named .png"u8, .. Unique(0)];

    public static object Upload(byte[] data, string contentType) => new { contentType, dataBase64 = Convert.ToBase64String(data) };

    private static byte[] Unique(int extra) => [.. Guid.NewGuid().ToByteArray(), .. new byte[extra]];
}
