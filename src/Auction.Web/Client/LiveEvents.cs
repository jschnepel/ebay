namespace Auction.Web.Client;

/// <summary>
/// Live mode's two jobs: fetch everything that's new, even when a busy moment spans several pages, and merge it into
/// what's shown in time order, never showing an event twice.
/// </summary>
public static class LiveEvents
{
    /// <summary>
    /// Every event shown and every new one, once each, newest first by time then key. An event stamped earlier than
    /// one already shown, but only visible once its transaction committed, goes in its place rather than on top.
    /// </summary>
    public static IReadOnlyList<LogEvent> Merge(IReadOnlyList<LogEvent> shown, IReadOnlyList<LogEvent> newer)
    {
        var known = shown.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        return [.. shown.Concat(newer.Where(e => known.Add(e.Key)))
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Key, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Follows the cursor from the first page until the last, or until <paramref name="maxPages"/>; complete is false
    /// when it stopped early or a page failed, so the view can say there's more than it shows.
    /// </summary>
    public static async Task<(IReadOnlyList<LogEvent> Events, bool Complete)> CollectAsync(
        Func<string?, Task<Page<LogEvent>?>> fetchPage, int maxPages)
    {
        var events = new List<LogEvent>();
        string? cursor = null;
        for (var page = 0; page < maxPages; page++)
        {
            if (await fetchPage(cursor) is not { } next)
            {
                return (events, false);
            }

            events.AddRange(next.Items);
            if (next.NextCursor is null)
            {
                return (events, true);
            }

            cursor = next.NextCursor;
        }

        return (events, false);
    }
}
