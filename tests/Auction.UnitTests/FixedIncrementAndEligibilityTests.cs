using Auction.Domain;
using static Auction.UnitTests.TestAuctions;

namespace Auction.UnitTests;

/// <summary>Fixed-increment bidding, deadlines and the self-bid rule.</summary>
public sealed class FixedIncrementAndEligibilityTests
{
    private static AuctionState FixedAt20() =>
        WithBids(Active(Terms(BidEntry.FixedIncrement, opening: 20m, increment: 5m)), (A, 20m));

    [Fact]
    public void Spec_example_wrong_step_amount_is_refused_and_the_exact_step_is_accepted()
    {
        var state = FixedAt20();

        var tooHigh = Bidding.Place(state, B, Usd(26m), During, AuctionLimits.Default);
        var tooLow = Bidding.Place(state, B, Usd(24m), During, AuctionLimits.Default);
        var exact = Bidding.Place(state, B, Usd(25m), During, AuctionLimits.Default);

        Assert.Equal(BidRejection.WrongIncrementAmount, tooHigh.Rejection);
        Assert.Equal(BidRejection.WrongIncrementAmount, tooLow.Rejection);
        Assert.True(exact.Accepted); // control
        Assert.Equal(B, exact.State.LeaderId);
        Assert.Equal(Usd(25m), exact.State.CurrentPrice);
        Assert.Equal([new BidStep(B, Usd(25m), BidSource.Bidder)], exact.Steps);
    }

    [Fact]
    public void First_fixed_bid_must_be_at_least_the_opening_price()
    {
        var state = Active(Terms(BidEntry.FixedIncrement, opening: 20m, increment: 5m));

        Assert.Equal(BidRejection.BelowMinimum, Bidding.Place(state, A, Usd(19.99m), During, AuctionLimits.Default).Rejection);
        Assert.True(Bidding.Place(state, A, Usd(20m), During, AuctionLimits.Default).Accepted); // control
    }

    [Fact]
    public void A_bid_decided_at_or_after_the_end_is_refused_and_just_before_is_accepted()
    {
        var state = Active(Terms(opening: 10m));

        var atEnd = Bidding.Place(state, A, Usd(10m), End, AuctionLimits.Default);
        var after = Bidding.Place(state, A, Usd(10m), End.AddTicks(1), AuctionLimits.Default);
        var justBefore = Bidding.Place(state, A, Usd(10m), End.AddTicks(-1), AuctionLimits.Default);

        Assert.Equal(BidRejection.AuctionEnded, atEnd.Rejection);
        Assert.Equal(BidRejection.AuctionEnded, after.Rejection);
        Assert.True(justBefore.Accepted); // control
    }

    [Fact]
    public void A_bid_before_the_start_is_refused()
    {
        var state = Active(Terms(opening: 10m));

        Assert.Equal(BidRejection.AuctionNotActive, Bidding.Place(state, A, Usd(10m), Start.AddTicks(-1), AuctionLimits.Default).Rejection);
        Assert.True(Bidding.Place(state, A, Usd(10m), Start, AuctionLimits.Default).Accepted); // control
    }

    [Theory]
    [InlineData(AuctionStatus.Draft)]
    [InlineData(AuctionStatus.Scheduled)]
    [InlineData(AuctionStatus.Suspended)]
    [InlineData(AuctionStatus.Closed)]
    [InlineData(AuctionStatus.Cancelled)]
    public void Bids_on_an_auction_that_is_not_active_are_refused(AuctionStatus status)
    {
        var state = Active(Terms(opening: 10m)) with { Status = status };

        Assert.Equal(BidRejection.AuctionNotActive, Bidding.Place(state, A, Usd(10m), During, AuctionLimits.Default).Rejection);
    }

    [Fact]
    public void The_seller_cannot_bid_on_their_own_auction()
    {
        var state = Active(Terms(opening: 10m));

        Assert.Equal(BidRejection.SelfBid, Bidding.Place(state, Seller, Usd(10m), During, AuctionLimits.Default).Rejection);
        Assert.True(Bidding.Place(state, A, Usd(10m), During, AuctionLimits.Default).Accepted); // control
    }

    [Fact]
    public void An_amount_above_the_cap_is_refused()
    {
        var state = Active(Terms(opening: 10m));
        var cap = new Money(AuctionLimits.Default.AmountCapCents);

        Assert.Equal(BidRejection.AboveCap, Bidding.Place(state, A, cap.Add(new Money(1)), During, AuctionLimits.Default).Rejection);
        Assert.True(Bidding.Place(state, A, cap, During, AuctionLimits.Default).Accepted); // control
    }

    [Fact]
    public void A_refused_bid_leaves_the_state_unchanged_and_records_no_steps()
    {
        var state = FixedAt20();

        var decision = Bidding.Place(state, B, Usd(26m), During, AuctionLimits.Default);

        Assert.Same(state, decision.State);
        Assert.Empty(decision.Steps);
    }
}
