using System.Net;
using Auction.Verifier;

namespace Auction.Simulator;

/// <summary>One bid request and what came of it.</summary>
public sealed record BidAttempt(Guid AuctionId, Guid BidderId, long AmountCents, HttpStatusCode Status, string? Code, TimeSpan Elapsed, bool Retried);

/// <summary>Intended load, as the profile asked for it. Times are whole milliseconds.</summary>
public sealed record IntendedLoad(int Auctions, int Bidders, long DurationMs, long ThinkMinMs, long ThinkMaxMs);

/// <summary>Actual load, as measured. Latencies are null when nothing was measured, never zero.</summary>
public sealed record ActualLoad(
    long LoadMs, int Attempts, int Accepted, IReadOnlyDictionary<string, int> Refused, int Retried, long AttemptsPerMinute,
    int AuctionsWithBids, long? BidLatencyP50Ms, long? BidLatencyP95Ms);

/// <summary>Where each auction ended up.</summary>
public sealed record AuctionResult(Guid Id, string Status, string BidEntry, long? PriceCents);

/// <summary>
/// The scenario report (SIM-02, SIM-16): the profile and seed, intended against actual load, outcome counts,
/// and the verifier's verdict. <see cref="Passed"/> needs completed setup, progress (accepted bids) and a
/// verifier pass.
/// </summary>
public sealed record SimulationReport(
    string Profile, int Seed, string? SetupError, IntendedLoad Intended, ActualLoad? Actual, IReadOnlyList<AuctionResult> Auctions,
    VerifierReport? Verification)
{
    public bool Verified => Verification is not null;

    public bool Passed => SetupError is null && Actual is { Accepted: > 0 } && Verification is { Passed: true };

    public static SimulationReport SetupFailed(Profile profile, SimulationOptions options, string error) =>
        new(profile.Name, options.Seed, error, Intent(profile, options.Duration ?? profile.Duration), null, [], null);

    public static SimulationReport Completed(
        Profile profile, SimulationOptions options, (TimeSpan Planned, TimeSpan Measured) load, IReadOnlyList<BidAttempt> attempts,
        IReadOnlyList<AuctionView> auctions, VerifierReport? verification)
    {
        var (duration, loadTime) = load;
        var accepted = attempts.Count(a => a.Status == HttpStatusCode.Created);
        var refused = attempts
            .Where(a => a.Status != HttpStatusCode.Created)
            .GroupBy(a => a.Code ?? ((int)a.Status).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(g => g.Key, g => g.Count());
        var latencies = attempts.Select(a => a.Elapsed.Ticks).Order().ToList();
        var loadMs = (long)loadTime.TotalMilliseconds;
        var actual = new ActualLoad(
            loadMs,
            attempts.Count,
            accepted,
            refused,
            attempts.Count(a => a.Retried),
            loadMs > 0 ? attempts.Count * 60_000L / loadMs : 0,
            attempts.Where(a => a.Status == HttpStatusCode.Created).Select(a => a.AuctionId).Distinct().Count(),
            Percentile(latencies, 50),
            Percentile(latencies, 95));
        return new SimulationReport(
            profile.Name,
            options.Seed,
            null,
            Intent(profile, duration),
            actual,
            [.. auctions.Select(a => new AuctionResult(a.Id, a.Status, a.BidEntry, a.CurrentPriceCents))],
            verification);
    }

    // Nearest-rank percentile (per cent, 1 to 100) over the sorted samples in ticks; null with no samples.
    private static long? Percentile(List<long> sortedTicks, int percent) =>
        sortedTicks.Count == 0 ? null : sortedTicks[Math.Max(0, ((percent * sortedTicks.Count) + 99) / 100 - 1)] / TimeSpan.TicksPerMillisecond;

    private static IntendedLoad Intent(Profile profile, TimeSpan duration) =>
        new(profile.Auctions, profile.Bidders, (long)duration.TotalMilliseconds, (long)profile.ThinkMin.TotalMilliseconds, (long)profile.ThinkMax.TotalMilliseconds);
}
