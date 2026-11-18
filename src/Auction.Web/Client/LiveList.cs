namespace Auction.Web.Client;

/// <summary>
/// A paged auction list that stays current: it re-fetches the pages already shown when one of its auctions
/// changes or the view reconciles, and never replaces an item with an older version of it.
/// </summary>
public sealed class LiveList<T>(Func<string?, Task<ApiResult<Page<T>>>> fetch)
    where T : AuctionSummary
{
    private int _pages;

    public List<T>? Items { get; private set; }

    public string? NextCursor { get; private set; }

    public string? Error { get; private set; }

    public bool Shows(Guid auctionId) => Items?.Exists(item => item.Id == auctionId) == true;

    public async Task MoreAsync()
    {
        var page = await fetch(NextCursor);
        if (!page.Succeeded)
        {
            Error = page.Message;
            return;
        }

        (Items ??= []).AddRange(page.Value!.Items);
        NextCursor = page.Value.NextCursor;
        _pages++;
        Error = null;
    }

    /// <summary>Fetches the same number of pages again, from the start.</summary>
    public async Task ReloadAsync()
    {
        var fresh = new List<T>();
        string? cursor = null;
        for (var i = 0; i < Math.Max(_pages, 1); i++)
        {
            var page = await fetch(cursor);
            if (!page.Succeeded)
            {
                // Keep showing what we have; the next reconcile tries again.
                return;
            }

            fresh.AddRange(page.Value!.Items);
            cursor = page.Value.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        Items = VersionGate.Merge(Items, fresh);
        NextCursor = cursor;
        Error = null;
    }
}
