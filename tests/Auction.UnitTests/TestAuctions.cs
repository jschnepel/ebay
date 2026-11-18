using Auction.Domain;

namespace Auction.UnitTests;

/// <summary>Builders for auctions in a known state. All times are UTC.</summary>
internal static class TestAuctions
{
    public static readonly DateTimeOffset Start = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset End = Start.AddHours(2);
    public static readonly DateTimeOffset During = Start.AddMinutes(30);

    public static readonly Guid Seller = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    public static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    public static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-0000000000c3");

    public static Money Usd(decimal dollars) => new((long)(dollars * 100));

    public static AuctionTerms Terms(
        BidEntry entry = BidEntry.Proxy,
        FloorType floor = FloorType.MinimumBid,
        decimal opening = 10m,
        decimal increment = 1m,
        decimal? reserve = null) => new()
        {
            Title = "Vintage camera",
            OpeningPrice = Usd(opening),
            Increment = Usd(increment),
            FloorType = floor,
            Reserve = reserve is { } r ? Usd(r) : null,
            BidEntry = entry,
            StartsAt = Start,
            EndsAt = End,
        };

    public static AuctionState Active(AuctionTerms terms) =>
        AuctionState.NewDraft(Guid.NewGuid(), Seller, terms) with { Status = AuctionStatus.Active };

    /// <summary>Places each bid during the auction and asserts it was accepted.</summary>
    public static AuctionState WithBids(AuctionState state, params (Guid Bidder, decimal Dollars)[] bids)
    {
        foreach (var (bidder, dollars) in bids)
        {
            var decision = Bidding.Place(state, bidder, Usd(dollars), During, AuctionLimits.Default);
            Assert.True(decision.Accepted, $"setup bid {dollars} by {bidder} was refused: {decision.Rejection}");
            state = decision.State;
        }

        return state;
    }
}
