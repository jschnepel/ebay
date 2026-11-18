namespace Auction.Web.Client;

/// <summary>
/// The client's consistency rule (RT-02): a view never goes back to an older version of an auction. Answers
/// can arrive out of order (a hint's re-fetch, the timer, a reconnect), so each one is checked against what
/// the view already shows.
/// </summary>
public static class VersionGate
{
    /// <summary>The auction to show: the incoming one unless the view already shows a newer version.</summary>
    public static T Newest<T>(T? shown, T incoming)
        where T : AuctionSummary =>
        shown is not null && shown.Id == incoming.Id && shown.Version > incoming.Version ? shown : incoming;

    /// <summary>A freshly fetched list, keeping any item the view already shows at a newer version.</summary>
    public static List<T> Merge<T>(IReadOnlyList<T>? shown, IEnumerable<T> incoming)
        where T : AuctionSummary
    {
        var current = shown?.ToDictionary(item => item.Id) ?? [];
        return [.. incoming.Select(item => Newest(current.GetValueOrDefault(item.Id), item))];
    }
}
