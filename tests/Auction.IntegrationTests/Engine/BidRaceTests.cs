using Auction.Domain;
using Auction.Engine;
using Auction.IntegrationTests.Database;
using Auction.ReferenceModel;
using static Auction.IntegrationTests.Engine.EngineScenario;

namespace Auction.IntegrationTests.Engine;

/// <summary>
/// Fifty bidders race on one auction. Afterwards the accepted bids, replayed in the order the engine
/// accepted them, must agree with the naive reference model, and the sequences must have no gaps.
/// </summary>
public sealed class BidRaceTests(MigratedDatabase db)
{
    private const int Bidders = 50;

    private readonly AuctionEngine _engine = db.CreateEngine(new EngineOptions { LockTimeout = TimeSpan.FromSeconds(10) });

    private sealed record Attempt(UserId Bidder, long Cents, CommandOutcome Outcome);

    [Fact]
    public async Task Fifty_proxy_bidders_racing_agree_with_the_reference_model()
    {
        var seller = UserId.New();
        var terms = Terms(await db.NowAsync(), BidEntry.Proxy);
        var id = await ActiveAsync(_engine, seller, terms);
        var random = new Random(20261118);
        var plans = Enumerable.Range(0, Bidders)
            .Select(_ => (Bidder: UserId.New(), Maxima: Enumerable.Range(0, 3).Select(_ => (long)random.Next(900, 60_000)).ToArray()))
            .ToList();

        var attempts = (await Task.WhenAll(plans.Select(async plan =>
        {
            var mine = new List<Attempt>();
            foreach (var cents in plan.Maxima)
            {
                mine.Add(new Attempt(plan.Bidder, cents, await BidAsync(_engine, plan.Bidder, id, cents)));
            }

            return mine;
        }))).SelectMany(a => a).ToList();

        var accepted = AcceptedInOrder(attempts);
        var reference = Reference(terms);
        var replay = NaiveAuction.Replay(reference, seller.Value, accepted.Select(a => new RefBid(a.Bidder.Value, a.Cents)));
        Assert.All(replay.Accepted, Assert.True);
        await AssertFinalStateAsync(id, replay, accepted.Count);

        // Proxy refusals only get stricter over time (the minimum and each bidder's maximum only rise),
        // so a bid refused during the race must also be refused after the last accepted bid.
        foreach (var refused in attempts.Where(a => !a.Outcome.Succeeded))
        {
            Assert.Contains(refused.Outcome.Error, new[] { CommandError.BelowMinimum, CommandError.MaximumLowered });
            var withRefused = accepted.Append(refused).Select(a => new RefBid(a.Bidder.Value, a.Cents));
            Assert.False(NaiveAuction.Replay(reference, seller.Value, withRefused).Accepted[^1]);
        }
    }

    [Fact]
    public async Task Fifty_fixed_increment_bidders_racing_for_the_next_step_agree_with_the_reference_model()
    {
        var seller = UserId.New();
        var terms = Terms(await db.NowAsync(), BidEntry.FixedIncrement);
        var id = await ActiveAsync(_engine, seller, terms);

        // Every bidder repeatedly reads the price and bids exactly the next step, so most bids collide.
        var attempts = (await Task.WhenAll(Enumerable.Range(0, Bidders).Select(async _ =>
        {
            var bidder = UserId.New();
            var mine = new List<Attempt>();
            for (var i = 0; i < 4; i++)
            {
                var price = await db.ScalarAsync<long?>(MigratedDatabase.Api, "SELECT current_price_cents FROM auctions WHERE id = $1", id.Value);
                var cents = price is { } p ? p + terms.Increment.Cents : terms.OpeningPrice.Cents;
                mine.Add(new Attempt(bidder, cents, await BidAsync(_engine, bidder, id, cents)));
            }

            return mine;
        }))).SelectMany(a => a).ToList();

        var accepted = AcceptedInOrder(attempts);
        var replay = NaiveAuction.Replay(Reference(terms), seller.Value, accepted.Select(a => new RefBid(a.Bidder.Value, a.Cents)));
        Assert.All(replay.Accepted, Assert.True);
        Assert.Equal(
            accepted.Select((_, i) => terms.OpeningPrice.Cents + (i * terms.Increment.Cents)),
            accepted.Select(a => a.Cents));
        Assert.All(attempts.Where(a => !a.Outcome.Succeeded), a => Assert.Equal(CommandError.WrongIncrementAmount, a.Outcome.Error));
        await AssertFinalStateAsync(id, replay, accepted.Count);
    }

    /// <summary>Accepted attempts ordered by the sequence the engine gave them, which must be exactly 1..k.</summary>
    private static List<Attempt> AcceptedInOrder(List<Attempt> attempts)
    {
        var accepted = attempts.Where(a => a.Outcome.Succeeded).OrderBy(a => a.Outcome.BidSequence).ToList();
        Assert.NotEmpty(accepted);
        Assert.Equal(Enumerable.Range(1, accepted.Count).Select(i => (long?)i), accepted.Select(a => a.Outcome.BidSequence));
        return accepted;
    }

    private static RefTerms Reference(AuctionTerms terms) => new(
        terms.OpeningPrice.Cents,
        terms.Increment.Cents,
        (RefFloor)(int)terms.FloorType,
        terms.Reserve?.Cents,
        (RefEntry)(int)terms.BidEntry);

    private async Task AssertFinalStateAsync(AuctionId id, RefResult replay, int acceptedCount)
    {
        var leader = await db.ScalarAsync<Guid>(MigratedDatabase.Verifier, "SELECT leader_id FROM auctions WHERE id = $1", id.Value);
        var price = await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT current_price_cents FROM auctions WHERE id = $1", id.Value);
        var count = await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT accepted_bid_count FROM auctions WHERE id = $1", id.Value);
        var version = await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT version FROM auctions WHERE id = $1", id.Value);
        Assert.Equal((replay.Leader, replay.PriceCents), (leader, price));
        Assert.Equal((acceptedCount, acceptedCount + 1L), (count, version));

        // History and events have no gaps: bid steps 1..n, and one event per version 1..v.
        var bidGaps = await db.ScalarAsync<long>(
            MigratedDatabase.Verifier,
            "SELECT count(*) - max(seq) FROM bids WHERE auction_id = $1",
            id.Value);
        var eventVersions = await db.ScalarAsync<long>(
            MigratedDatabase.Verifier,
            "SELECT count(DISTINCT auction_version) FROM outbox_events WHERE auction_id = $1 AND auction_version BETWEEN 1 AND $2",
            id.Value,
            version);
        Assert.Equal((0L, version), (bidGaps, eventVersions));
        Assert.Equal(version, await CountAsync(db, "outbox_events", id));
    }
}
