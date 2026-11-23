namespace Auction.Web.Client;

/// <summary>
/// Live mode's two jobs: fetch everything that's new, even when a busy moment spans several pages, and merge it into
/// what's shown in time order, never showing an event twice.
/// </summary>
public static class LiveEvents
{
    /// <summary>The most events live mode keeps on screen; older ones are a search or a reload away.</summary>
    public const int Kept = 500;

    /// <summary>
    /// Every event shown and every new one, once each, newest first by time then key. An event stamped earlier than
    /// one already shown, but only visible once its transaction committed, goes in its place rather than on top.
    /// Only the newest <paramref name="keep"/> stay, and with nothing new the list comes back as it was.
    /// </summary>
    public static IReadOnlyList<LogEvent> Merge(IReadOnlyList<LogEvent> shown, IReadOnlyList<LogEvent> newer, int keep = Kept)
    {
        var known = shown.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var added = newer.Where(e => known.Add(e.Key)).ToList();
        if (added.Count == 0)
        {
            return shown;
        }

        return [.. shown.Concat(added)
            .OrderByDescending(e => e.At)
            .ThenByDescending(e => e.Key, StringComparer.Ordinal)
            .Take(keep)];
    }

    /// <summary>
    /// An older page added below what's shown, leaving out any event already there: live mode reaches back past the
    /// newest page, so the next page down can overlap it.
    /// </summary>
    public static IReadOnlyList<LogEvent> Append(IReadOnlyList<LogEvent> shown, IReadOnlyList<LogEvent> older)
    {
        var known = shown.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        return [.. shown, .. older.Where(e => known.Add(e.Key))];
    }

    /// <summary>
    /// Follows the cursor from the first page until the last, or until <paramref name="maxPages"/>, and says which:
    /// stopping early means more happened than is shown, which a failed request doesn't.
    /// </summary>
    public static async Task<(IReadOnlyList<LogEvent> Events, LiveFetch Fetch)> CollectAsync(
        Func<string?, Task<Page<LogEvent>?>> fetchPage, int maxPages)
    {
        var events = new List<LogEvent>();
        string? cursor = null;
        for (var page = 0; page < maxPages; page++)
        {
            if (await fetchPage(cursor) is not { } next)
            {
                return (events, LiveFetch.Failed);
            }

            events.AddRange(next.Items);
            if (next.NextCursor is null)
            {
                return (events, LiveFetch.Complete);
            }

            cursor = next.NextCursor;
        }

        return (events, LiveFetch.More);
    }
}

/// <summary>How a live fetch ended: everything new, more than it reads at once, or a request that failed.</summary>
public enum LiveFetch
{
    Complete,
    More,
    Failed,
}
