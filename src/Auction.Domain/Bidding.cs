namespace Auction.Domain;

/// <summary>Why a bid was refused. <see cref="None"/> means accepted.</summary>
public enum BidRejection
{
    None,
    AuctionNotActive,
    AuctionEnded,
    SelfBid,
    AboveCap,
    BelowMinimum,
    WrongIncrementAmount,
    MaximumLowered,
}

/// <summary>Who caused a history step: the bidder themselves, or the engine bidding for them.</summary>
public enum BidSource
{
    Bidder,
    Proxy,
}

/// <summary>One public bid-history step. Amounts here are visible prices, never a leader's hidden maximum.</summary>
public sealed record BidStep(Guid BidderId, Money Amount, BidSource Source);

/// <summary>The result of one bid: the new state and its history steps, or the reason it was refused.</summary>
public sealed record BidDecision(BidRejection Rejection, AuctionState State, IReadOnlyList<BidStep> Steps)
{
    public bool Accepted => Rejection == BidRejection.None;
}

/// <summary>
/// Bid placement. <c>decisionTime</c> is the database's <c>clock_timestamp()</c> read
/// after taking the auction lock; this class never reads a clock itself.
/// </summary>
public static class Bidding
{
    public static BidDecision Place(
        AuctionState state, Guid bidderId, Money amount, DateTimeOffset decisionTime, AuctionLimits limits)
    {
        throw new NotImplementedException();
    }

    /// <summary>The smallest acceptable bid (fixed increment) or maximum (proxy) right now.</summary>
    public static Money MinimumNextBid(AuctionState state)
    {
        throw new NotImplementedException();
    }
}
