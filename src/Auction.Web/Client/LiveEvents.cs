namespace Auction.Web.Client;

/// <summary>Live mode's merge: newer events go on top, and an event already shown is never shown twice.</summary>
public static class LiveEvents
{
    public static IReadOnlyList<LogEvent> Merge(IReadOnlyList<LogEvent> shown, IReadOnlyList<LogEvent> newer)
    {
        var known = shown.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        return [.. newer.Where(e => known.Add(e.Key)), .. shown];
    }
}
