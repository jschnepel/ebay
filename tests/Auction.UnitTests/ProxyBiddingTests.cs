using Auction.Domain;
using static Auction.UnitTests.TestAuctions;

namespace Auction.UnitTests;

/// <summary>Proxy bidding: the leader holds the highest maximum; price = min(top, second + increment).</summary>
public sealed class ProxyBiddingTests
{
    [Fact]
    public void First_bidder_leads_at_the_opening_price_whatever_their_maximum()
    {
        var state = WithBids(Active(Terms(opening: 10m)), (A, 50m));

        Assert.Equal(A, state.LeaderId);
        Assert.Equal(Usd(10m), state.CurrentPrice);
    }

    [Fact]
    public void Spec_example_A_50_then_B_30_leaves_A_leading_at_31()
    {
        var state = WithBids(Active(Terms(opening: 10m, increment: 1m)), (A, 50m), (B, 30m));

        Assert.Equal(A, state.LeaderId);
        Assert.Equal(Usd(31m), state.CurrentPrice);
    }

    [Fact]
    public void Spec_example_equal_later_maximum_does_not_take_the_lead_and_price_is_the_tied_amount()
    {
        var state = WithBids(Active(Terms(opening: 10m, increment: 1m)), (A, 50m), (B, 30m), (C, 50m));

        Assert.Equal(A, state.LeaderId);
        Assert.Equal(Usd(50m), state.CurrentPrice);
    }

    [Fact]
    public void A_higher_maximum_takes_the_lead_at_the_old_maximum_plus_increment()
    {
        var state = WithBids(Active(Terms(opening: 10m, increment: 1m)), (A, 50m), (B, 80m));

        Assert.Equal(B, state.LeaderId);
        Assert.Equal(Usd(51m), state.CurrentPrice);
    }

    [Fact]
    public void Price_never_exceeds_the_leaders_maximum()
    {
        var state = WithBids(Active(Terms(opening: 10m, increment: 5m)), (A, 50m), (B, 48m));

        Assert.Equal(A, state.LeaderId);
        Assert.Equal(Usd(50m), state.CurrentPrice); // min(50, 48 + 5)
    }

    [Fact]
    public void A_maximum_below_the_minimum_next_bid_is_refused()
    {
        var state = WithBids(Active(Terms(opening: 10m, increment: 1m)), (A, 50m), (B, 30m)); // price 31

        var low = Bidding.Place(state, C, Usd(31.99m), During, AuctionLimits.Default);
        var atMinimum = Bidding.Place(state, C, Usd(32m), During, AuctionLimits.Default);

        Assert.Equal(BidRejection.BelowMinimum, low.Rejection);
        Assert.Same(state, low.State);
        Assert.True(atMinimum.Accepted); // control
    }

    [Fact]
    public void A_first_maximum_below_the_opening_price_is_refused()
    {
        var state = Active(Terms(opening: 10m));

        Assert.Equal(BidRejection.BelowMinimum, Bidding.Place(state, A, Usd(9.99m), During, AuctionLimits.Default).Rejection);
        Assert.True(Bidding.Place(state, A, Usd(10m), During, AuctionLimits.Default).Accepted); // control
    }

    [Fact]
    public void A_bidder_cannot_lower_their_own_maximum()
    {
        var state = WithBids(Active(Terms(opening: 10m)), (A, 50m), (B, 20m), (A, 60m)); // price 21, A max 60

        var lowered = Bidding.Place(state, A, Usd(55m), During, AuctionLimits.Default);
        var raised = Bidding.Place(state, A, Usd(70m), During, AuctionLimits.Default);

        Assert.Equal(BidRejection.MaximumLowered, lowered.Rejection);
        Assert.True(raised.Accepted); // control
    }

    [Fact]
    public void Leader_raising_their_maximum_keeps_the_price_and_adds_no_public_step()
    {
        var state = WithBids(Active(Terms(opening: 10m)), (A, 50m), (B, 30m)); // A leads at 31

        var raise = Bidding.Place(state, A, Usd(90m), During, AuctionLimits.Default);

        Assert.True(raise.Accepted);
        Assert.Equal(Usd(31m), raise.State.CurrentPrice);
        Assert.Empty(raise.Steps);
        Assert.Equal(Usd(90m), raise.State.Maxima.Single(m => m.BidderId == A).Maximum);
    }

    [Fact]
    public void An_immediately_outbid_bid_records_the_bidders_amount_and_the_proxy_answer()
    {
        var state = WithBids(Active(Terms(opening: 10m)), (A, 50m));

        var decision = Bidding.Place(state, B, Usd(30m), During, AuctionLimits.Default);

        Assert.Equal(
            [new BidStep(B, Usd(30m), BidSource.Bidder), new BidStep(A, Usd(31m), BidSource.Proxy)],
            decision.Steps);
    }

    [Fact]
    public void A_new_leaders_step_shows_the_price_not_their_hidden_maximum()
    {
        var state = WithBids(Active(Terms(opening: 10m)), (A, 50m));

        var decision = Bidding.Place(state, B, Usd(80m), During, AuctionLimits.Default);

        Assert.Equal([new BidStep(B, Usd(51m), BidSource.Bidder)], decision.Steps);
    }

    [Fact]
    public void Under_reserve_the_price_rises_to_the_reserve_once_the_leaders_maximum_reaches_it()
    {
        var terms = Terms(floor: FloorType.Reserve, opening: 10m, reserve: 100m);

        var below = WithBids(Active(terms), (A, 99m));
        var reached = WithBids(Active(terms), (A, 150m));

        Assert.Equal(Usd(10m), below.CurrentPrice);
        Assert.False(below.ReserveMet);
        Assert.Equal(Usd(100m), reached.CurrentPrice);
        Assert.True(reached.ReserveMet);
    }

    [Fact]
    public void The_reserve_lift_never_exceeds_the_leaders_maximum()
    {
        var terms = Terms(floor: FloorType.Reserve, opening: 10m, reserve: 100m);

        var exactlyReserve = WithBids(Active(terms), (A, 100m));
        var aboveReserve = WithBids(Active(terms), (A, 120m));
        var refused = Bidding.Place(exactlyReserve, B, Usd(40m), During, AuctionLimits.Default);

        Assert.Equal(Usd(100m), exactlyReserve.CurrentPrice); // lifted to the reserve, which equals the maximum
        Assert.Equal(Usd(100m), aboveReserve.CurrentPrice);   // lifted to the reserve, not to the maximum
        Assert.Equal(BidRejection.BelowMinimum, refused.Rejection); // the lifted price sets the next minimum
    }

    [Fact]
    public void Minimum_next_bid_is_the_opening_price_then_price_plus_increment()
    {
        var empty = Active(Terms(opening: 10m, increment: 2.5m));
        var bid = WithBids(empty, (A, 50m), (B, 20m)); // price 22.50

        Assert.Equal(Usd(10m), Bidding.MinimumNextBid(empty));
        Assert.Equal(Usd(25m), Bidding.MinimumNextBid(bid));
    }
}
