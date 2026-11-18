using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Auction.Api.Auctions;

/// <summary>
/// Opaque page cursors: the sort key of the last item already returned, base64url-encoded. Keyset
/// pagination keeps pages stable while auctions are added.
/// </summary>
internal static class Cursors
{
    public static string Encode(DateTimeOffset endsAt, Guid id) =>
        Encode(string.Create(CultureInfo.InvariantCulture, $"{endsAt.UtcTicks}:{id:N}"));

    public static string Encode(long sequence) => Encode(sequence.ToString(CultureInfo.InvariantCulture));

    public static bool TryDecode(string? cursor, out (DateTimeOffset EndsAt, Guid Id)? after)
    {
        after = null;
        if (cursor is null)
        {
            return true;
        }

        var parts = Decode(cursor)?.Split(':');
        if (parts is not [var ticks, var id]
            || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var utcTicks)
            || utcTicks > DateTimeOffset.MaxValue.UtcTicks
            || !Guid.TryParseExact(id, "N", out var guid))
        {
            return false;
        }

        after = (new DateTimeOffset(utcTicks, TimeSpan.Zero), guid);
        return true;
    }

    public static bool TryDecode(string? cursor, out long? before)
    {
        before = null;
        if (cursor is null)
        {
            return true;
        }

        if (!long.TryParse(Decode(cursor), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
        {
            return false;
        }

        before = sequence;
        return true;
    }

    private static string Encode(string value) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));

    private static string? Decode(string cursor)
    {
        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
