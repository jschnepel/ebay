using Auction.Api.Admin;

namespace Auction.IntegrationTests.Api;

/// <summary>UI-04: each KPI is the exact p50/p95 of the samples in its rolling 300 × 1-second window, on a fake clock.</summary>
public sealed class LatencyWindowTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void An_empty_window_reports_not_measured_never_zero()
    {
        var summary = new LatencyWindow(new FakeClock(DateTimeOffset.UnixEpoch.AddDays(1))).Summarize();

        Assert.Equal(new LatencySummary(0, null, null), summary);
    }

    [Fact]
    public void Percentiles_are_the_exact_nearest_rank_of_the_samples_in_the_window()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch.AddDays(1));
        var window = new LatencyWindow(clock);

        // 1..100 ms over 100 seconds: nearest-rank p50 is 50 ms and p95 is 95 ms.
        for (var ms = 1; ms <= 100; ms++)
        {
            window.Record(TimeSpan.FromMilliseconds(ms));
            clock.Now = clock.Now.AddSeconds(1);
        }

        Assert.Equal(new LatencySummary(100, 50, 95), window.Summarize());
    }

    [Fact]
    public void Samples_older_than_five_minutes_leave_the_window()
    {
        var clock = new FakeClock(DateTimeOffset.UnixEpoch.AddDays(1));
        var window = new LatencyWindow(clock);
        window.Record(TimeSpan.FromMilliseconds(900));
        clock.Now = clock.Now.AddSeconds(299);
        window.Record(TimeSpan.FromMilliseconds(10));

        Assert.Equal(new LatencySummary(2, 10, 900), window.Summarize());

        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(new LatencySummary(1, 10, 10), window.Summarize());

        clock.Now = clock.Now.AddSeconds(300);
        Assert.Equal(new LatencySummary(0, null, null), window.Summarize());
    }
}
