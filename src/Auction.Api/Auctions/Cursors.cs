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

    /// <summary>A time and a text key, for lists merged from several sources (the admin event log).</summary>
    public static string Encode(DateTimeOffset at, string key) =>
        Encode(string.Create(CultureInfo.InvariantCulture, $"{at.UtcTicks}|{key}"));

    public static bool TryDecode(string? cursor, out (DateTimeOffset At, string Key)? before)
    {
        before = null;
        if (cursor is null)
        {
            return true;
        }

        var text = Decode(cursor);
        var bar = text?.IndexOf('|', StringComparison.Ordinal) ?? -1;
        if (bar < 1
            || !long.TryParse(text![..bar], NumberStyles.None, CultureInfo.InvariantCulture, out var utcTicks)
            || utcTicks > DateTimeOffset.MaxValue.UtcTicks
            || bar == text.Length - 1)
        {
            return false;
        }

        before = (new DateTimeOffset(utcTicks, TimeSpan.Zero), text[(bar + 1)..]);
        return true;
    }

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
