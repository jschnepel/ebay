using Auction.Domain;
using Auction.ReferenceModel;
using CsCheck;
using static Auction.UnitTests.TestAuctions;

namespace Auction.UnitTests;

/// <summary>
/// Property tests: for random terms and random bid sequences, the domain and the independent naive
/// reference model must agree on every acceptance, the leader, the price and the outcome; and the domain's
/// invariants hold after every accepted bid.
/// </summary>
public sealed class BiddingPropertyTests
{
    private static readonly UserId[] People = [Seller, A, B, C];

    private sealed record Scenario(AuctionTerms Terms, RefTerms Reference, IReadOnlyList<(UserId Bidder, Money Amount)> Bids);

    private static readonly Gen<AuctionTerms> GenTerms =
        Gen.Select(Gen.Long[1, 5_000], Gen.Long[1, 500], Gen.Enum<FloorType>(), Gen.Long[0, 20_000], Gen.Enum<BidEntry>())
            .Select((opening, increment, floor, reserveOffset, entry) => new AuctionTerms
            {
                Title = "Property auction",
                OpeningPrice = new Money(opening),
                Increment = new Money(increment),
                FloorType = floor,
                Reserve = floor == FloorType.Reserve ? new Money(opening + reserveOffset) : null,
                BidEntry = entry,
                StartsAt = Start,
                EndsAt = End,
            });

    // Amounts cluster on opening + k * increment so fixed-increment bids are sometimes exactly right,
    // with occasional off-step jitter so the refusal paths are exercised too.
    private static Gen<(UserId, Money)> GenBid(AuctionTerms terms) =>
        Gen.Select(Gen.Int[0, People.Length - 1], Gen.Long[0, 60], Gen.Long[0, 3], Gen.Long[0, terms.Increment.Cents])
            .Select((who, steps, jitterChance, jitter) => (
                People[who],
                new Money(terms.OpeningPrice.Cents + (steps * terms.Increment.Cents) + (jitterChance == 0 ? jitter : 0))));

    private static readonly Gen<Scenario> GenScenario =
        GenTerms.SelectMany(terms => GenBid(terms).List[0, 40].Select(bids => new Scenario(
            terms,
            new RefTerms(
                terms.OpeningPrice.Cents,
                terms.Increment.Cents,
                (RefFloor)(int)terms.FloorType,
                terms.Reserve?.Cents,
                (RefEntry)(int)terms.BidEntry),
            bids)));

    [Fact]
    public void Domain_agrees_with_the_naive_reference_model()
    {
        GenScenario.Sample(scenario =>
        {
            var (state, accepted) = Run(scenario);
            var expected = NaiveAuction.Replay(scenario.Reference, Seller.Value, scenario.Bids.Select(b => new RefBid(b.Bidder.Value, b.Amount.Cents)));
            var closed = Lifecycle.Close(state, End).State;

            Assert.Equal(expected.Accepted, accepted);
            Assert.Equal(expected.Leader, closed.LeaderId?.Value);
            Assert.Equal(expected.PriceCents, closed.CurrentPrice?.Cents);
            Assert.Equal(expected.Outcome, closed.Outcome.ToString());
        }, iter: 2_000);
    }

    [Fact]
    public void Invariants_hold_after_every_accepted_bid()
    {
        GenScenario.Sample(scenario =>
        {
            var state = Active(scenario.Terms);
            foreach (var (bidder, amount) in scenario.Bids)
            {
                var before = state;
                var decision = Bidding.Place(state, bidder, amount, During, AuctionLimits.Default);
                if (!decision.Accepted)
                {
                    Assert.Same(before, decision.State);
                    continue;
                }

                state = decision.State;
                AssertInvariants(before, state);
            }
        }, iter: 2_000);
    }

    private static (AuctionState State, List<bool> Accepted) Run(Scenario scenario)
    {
        var state = Active(scenario.Terms);
        var accepted = new List<bool>();
        foreach (var (bidder, amount) in scenario.Bids)
        {
            var decision = Bidding.Place(state, bidder, amount, During, AuctionLimits.Default);
            accepted.Add(decision.Accepted);
            state = decision.State;
        }

        return (state, accepted);
    }

    private static void AssertInvariants(AuctionState before, AuctionState after)
    {
        var price = after.CurrentPrice!.Value;
        Assert.NotEqual(Seller, after.LeaderId);
        Assert.True(price.IsAtLeast(after.Terms.OpeningPrice), "price fell below the opening price");
        Assert.True(before.CurrentPrice is null || price.IsAtLeast(before.CurrentPrice.Value), "price went down");
        Assert.Equal(before.AcceptedBidCount + 1, after.AcceptedBidCount);

        if (after.Terms.BidEntry == BidEntry.Proxy)
        {
            var leaderMax = after.Maxima.Single(m => m.BidderId == after.LeaderId).Maximum;
            Assert.True(leaderMax.IsAtLeast(price), "price exceeded the leader's maximum");
            Assert.Equal(after.Maxima.Count, after.Maxima.Select(m => m.BidderId).Distinct().Count());
        }
    }
}
