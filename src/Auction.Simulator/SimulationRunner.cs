using System.Collections.Concurrent;
using Auction.Verifier;
using Npgsql;

namespace Auction.Simulator;

/// <summary>Where and how to run one profile.</summary>
public sealed record SimulationOptions(Uri BaseAddress, string Password, int Seed)
{
    /// <summary>The verifier's read-only connection; without it the run reports "not verified".</summary>
    public string? VerifierConnectionString { get; init; }

    /// <summary>Overrides the profile's duration (tests use shorter runs).</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Waits for the auctions to close before verifying, so outcomes are checked too.</summary>
    public bool WaitForClose { get; init; } = true;
}

/// <summary>
/// Runs a profile in phases: setup (sign in every virtual user and confirm its identity, list and publish the
/// auctions), load (bidders bid until the duration ends), drain (wait for the auctions to close), verify.
/// </summary>
public sealed class SimulationRunner(Profile profile, SimulationOptions options) : IDisposable
{
    private readonly ConcurrentBag<BidAttempt> _attempts = [];
    private readonly CancellationTokenSource _stop = new();

    public Profile Profile => profile;

    /// <summary>Bids sent so far, including refused ones.</summary>
    public int Attempts => _attempts.Count;

    /// <summary>Bids the API accepted so far.</summary>
    public int Accepted => _attempts.Count(a => a.Status == System.Net.HttpStatusCode.Created);

    /// <summary>Ends the load early; the run still reports and verifies what it did, without waiting for its auctions to close.</summary>
    public void Stop() => _stop.Cancel();

    public bool Stopped => _stop.IsCancellationRequested;

    public void Dispose() => _stop.Dispose();

    public async Task<SimulationReport> RunAsync(CancellationToken cancellationToken)
    {
        if (profile.Bidders > SimulatorAccounts.MaxBidders)
        {
            throw new InvalidOperationException($"At most {SimulatorAccounts.MaxBidders} bidders are seeded.");
        }

        var duration = options.Duration ?? profile.Duration;
        var startedAt = DateTimeOffset.UtcNow;
        using var seller = new VirtualUser(options.BaseAddress);
        var bidders = Enumerable.Range(1, profile.Bidders).Select(_ => new VirtualUser(options.BaseAddress)).ToList();
        try
        {
            var signedIn = await SignInAllAsync(seller, bidders, cancellationToken);
            if (signedIn < profile.Bidders + 1)
            {
                return SimulationReport.SetupFailed(profile, options, $"{signedIn} of {profile.Bidders + 1} virtual users signed in as their seeded accounts.");
            }

            var auctions = await ListAuctionsAsync(seller, duration, cancellationToken);
            if (auctions.Count < profile.Auctions)
            {
                return SimulationReport.SetupFailed(profile, options, $"{auctions.Count} of {profile.Auctions} auctions were listed.");
            }

            var loadStarted = TimeProvider.System.GetTimestamp();
            using (var load = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token))
            {
                load.CancelAfter(duration);
                // Seeded on purpose, so a run can be repeated; nothing here is security-sensitive.
#pragma warning disable S2245
                await Task.WhenAll(bidders.Select((bidder, i) => BidLoopAsync(bidder, auctions, new Random(options.Seed + i), load.Token)));
#pragma warning restore S2245
            }

            var loadTime = TimeProvider.System.GetElapsedTime(loadStarted);
            var final = await DrainAsync(seller, auctions, cancellationToken);
            var verification = await VerifyAsync(startedAt, seller.Account!.Id, cancellationToken);
            return SimulationReport.Completed(profile, options, (duration, loadTime), [.. _attempts], final, verification);
        }
        finally
        {
            bidders.ForEach(b => b.Dispose());
        }
    }

    private async Task<int> SignInAllAsync(VirtualUser seller, List<VirtualUser> bidders, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(bidders
            .Select((bidder, i) => bidder.SignInAsync(SimulatorAccounts.BidderEmail(i + 1), options.Password, cancellationToken))
            .Append(seller.SignInAsync(SimulatorAccounts.SellerEmail, options.Password, cancellationToken)));
        return results.Count(ok => ok);
    }

    // Terms vary by seed: both bidding models and every floor type appear across auctions.
    private async Task<List<Guid>> ListAuctionsAsync(VirtualUser seller, TimeSpan duration, CancellationToken cancellationToken)
    {
#pragma warning disable S2245 // seeded on purpose, so a run's listings can be repeated
        var random = new Random(options.Seed);
#pragma warning restore S2245
        var ids = new List<Guid>();
        for (var i = 0; i < profile.Auctions; i++)
        {
            var proxy = profile.Auctions == 1 ? random.Next(2) == 0 : i % 2 == 0;
            var floor = (i % 3) switch { 0 => "reserve", 1 => "minimum_bid", _ => "absolute" };
            var opening = 100L * random.Next(5, 50);
            var terms = new
            {
                title = $"Simulated item {i + 1} ({profile.Name}, seed {options.Seed})",
                description = "Listed by the load simulator.",
                openingPriceCents = opening,
                incrementCents = 100L,
                floorType = floor,
                reserveCents = floor == "reserve" ? opening * 3 : (long?)null,
                bidEntry = proxy ? "proxy" : "fixed_increment",
                startsAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                // Long enough for the load; a minute is the shortest auction the engine allows.
                endsAt = DateTimeOffset.UtcNow + Max(duration + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(65)),
            };
            var created = await seller.CreateDraftAsync(terms, Guid.NewGuid().ToString(), cancellationToken);
            if (created.Body?.GetProperty("auctionId").GetGuid() is not { } id)
            {
                continue;
            }

            if ((await seller.PublishAsync(id, Guid.NewGuid().ToString(), cancellationToken)).Succeeded)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task BidLoopAsync(VirtualUser bidder, List<Guid> auctions, Random random, CancellationToken load)
    {
        while (!load.IsCancellationRequested)
        {
            var id = auctions[random.Next(auctions.Count)];
            try
            {
                if (await bidder.AuctionAsync(id, load) is { Status: "active" } auction)
                {
                    await PlaceAsync(bidder, auction, random, load);
                }

                await Task.Delay(Think(random), load);
            }
            catch (OperationCanceledException) when (load.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // A fixed-increment bid is exactly the next step; a proxy maximum goes a few increments above it.
    // One retry with the same key after "busy" or a lost answer, which the engine replays if it was recorded.
    private async Task PlaceAsync(VirtualUser bidder, AuctionView auction, Random random, CancellationToken load)
    {
        var amount = auction.BidEntry == "proxy"
            ? auction.MinimumNextBidCents + (auction.IncrementCents * random.Next(0, 4))
            : auction.MinimumNextBidCents;
        var key = Guid.NewGuid().ToString();
        var result = await bidder.BidAsync(auction.Id, amount, key, load);
        var retried = false;
        if (result.Retryable && !load.IsCancellationRequested)
        {
            retried = true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), load);
            result = await bidder.BidAsync(auction.Id, amount, key, load);
        }

        _attempts.Add(new BidAttempt(auction.Id, bidder.Account!.Id, amount, result.Status, result.Code, result.Elapsed, retried));
    }

    private TimeSpan Think(Random random) =>
        profile.ThinkMax <= TimeSpan.Zero
            ? TimeSpan.Zero
            : profile.ThinkMin + TimeSpan.FromTicks(random.NextInt64((profile.ThinkMax - profile.ThinkMin).Ticks + 1));

    private async Task<List<AuctionView>> DrainAsync(VirtualUser seller, List<Guid> auctions, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (true)
        {
            var views = new List<AuctionView>();
            foreach (var id in auctions)
            {
                if (await seller.AuctionAsync(id, cancellationToken) is { } view)
                {
                    views.Add(view);
                }
            }

            if (!options.WaitForClose || Stopped || views.TrueForAll(v => v.Status == "closed") || DateTimeOffset.UtcNow > deadline)
            {
                return views;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    // The simulator's own auctions from this run; the standalone verifier checks everything.
    private async Task<VerifierReport?> VerifyAsync(DateTimeOffset since, Guid sellerId, CancellationToken cancellationToken)
    {
        if (options.VerifierConnectionString is not { Length: > 0 } connection)
        {
            return null;
        }

        await using var dataSource = NpgsqlDataSource.Create(connection);
        return await InvariantVerifier.VerifyAsync(dataSource, new VerifierScope(since.AddSeconds(-5), sellerId), cancellationToken);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
