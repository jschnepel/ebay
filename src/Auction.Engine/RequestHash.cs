using System.Security.Cryptography;
using System.Text.Json;

namespace Auction.Engine;

/// <summary>SHA-256 of a command and its payload, so a reused key with a different request is detected.</summary>
internal static class RequestHash
{
    public static byte[] Of(string command, object payload) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { command, payload }));
}
