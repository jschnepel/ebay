using Auction.Api.Auctions;

namespace Auction.Api.Hosting;

/// <summary>The page size and keyset cursor every list takes, checked the same way everywhere.</summary>
internal static class Paging
{
    /// <summary>
    /// The requested size cut to the maximum (the default when absent) and the decoded cursor; false with a
    /// 400 problem for a size below 1 or a cursor this API didn't issue.
    /// </summary>
    public static bool TryParse(
        int? pageSize, string? cursor, ApiOptions options, out int size, out (DateTimeOffset, Guid)? after, out IResult problem)
    {
        problem = Results.Empty;
        after = null;
        size = pageSize switch
        {
            null => options.DefaultPageSize,
            < 1 => 0,
            _ => Math.Min(pageSize.Value, options.MaxPageSize),
        };
        if (size == 0)
        {
            problem = Problems.BadRequest("invalid_page_size", "pageSize must be at least 1.");
            return false;
        }

        if (!Cursors.TryDecode(cursor, out after))
        {
            problem = Problems.BadRequest("invalid_cursor", "The cursor is not one this API returned.");
            return false;
        }

        return true;
    }
}
