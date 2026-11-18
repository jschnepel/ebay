namespace Auction.Api.Admin;

/// <summary>Exact percentiles of the samples in the window, in whole milliseconds; null when nothing was measured.</summary>
public sealed record LatencySummary(int Count, long? P50Ms, long? P95Ms);

/// <summary>
/// A rolling window of latency samples for one admin KPI (UI-04): 300 one-second buckets, so the window
/// always covers the last five minutes. Percentiles are exact over every sample still in the window
/// (nearest rank), never estimated, and a window with no samples reports "not measured" rather than zero.
/// </summary>
public sealed class LatencyWindow(TimeProvider clock)
{
    public const int Seconds = 300;

    private readonly Lock _gate = new();
    private readonly List<long>[] _buckets = [.. Enumerable.Range(0, Seconds).Select(_ => new List<long>())];
    private readonly long[] _bucketSecond = new long[Seconds];

    public void Record(TimeSpan elapsed)
    {
        var second = clock.GetUtcNow().ToUnixTimeSeconds();
        lock (_gate)
        {
            var bucket = Bucket(second);
            bucket.Add(elapsed.Ticks);
        }
    }

    public LatencySummary Summarize()
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        List<long> samples;
        lock (_gate)
        {
            samples = [.. Enumerable.Range(0, Seconds)
                .Where(i => now - _bucketSecond[i] < Seconds && _bucketSecond[i] <= now)
                .SelectMany(i => _buckets[i])];
        }

        if (samples.Count == 0)
        {
            return new LatencySummary(0, null, null);
        }

        samples.Sort();
        return new LatencySummary(samples.Count, Rank(samples, 50), Rank(samples, 95));
    }

    // Nearest rank: the smallest sample at or above the given share of all samples.
    private static long Rank(List<long> sorted, int percent) =>
        sorted[Math.Max(0, ((percent * sorted.Count) + 99) / 100 - 1)] / TimeSpan.TicksPerMillisecond;

    // A bucket last used more than a window ago is cleared before reuse.
    private List<long> Bucket(long second)
    {
        var index = (int)(second % Seconds);
        if (_bucketSecond[index] != second)
        {
            _buckets[index].Clear();
            _bucketSecond[index] = second;
        }

        return _buckets[index];
    }
}
