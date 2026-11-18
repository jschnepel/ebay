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

/// <summary>
/// One public bid-history step. <see cref="Amount"/> is always a visible price, never a private maximum.
/// It's null for a bidder who was outbid at once: their bid is shown, its amount isn't.
/// </summary>
public sealed record BidStep(Guid BidderId, Money? Amount, BidSource Source);

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
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(limits);

        var gate = CheckEligibility(state, bidderId, amount, decisionTime, limits);
        if (gate != BidRejection.None)
        {
            return Reject(state, gate);
        }

        return state.Terms.BidEntry == BidEntry.Proxy
            ? PlaceProxy(state, bidderId, amount)
            : PlaceFixed(state, bidderId, amount);
    }

    /// <summary>The smallest acceptable bid (fixed increment) or maximum (proxy) right now.</summary>
    public static Money MinimumNextBid(AuctionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.CurrentPrice is { } price ? price.Add(state.Terms.Increment) : state.Terms.OpeningPrice;
    }

    // A bid is open when startsAt <= decisionTime < endsAt, by database time. Sellers can't bid on their own auction.
    private static BidRejection CheckEligibility(
        AuctionState state, Guid bidderId, Money amount, DateTimeOffset decisionTime, AuctionLimits limits)
    {
        if (state.Status != AuctionStatus.Active || decisionTime < state.Terms.StartsAt)
        {
            return BidRejection.AuctionNotActive;
        }

        if (decisionTime >= state.Terms.EndsAt)
        {
            return BidRejection.AuctionEnded;
        }

        if (bidderId == state.SellerId)
        {
            return BidRejection.SelfBid;
        }

        return amount.Cents > limits.AmountCapCents ? BidRejection.AboveCap : BidRejection.None;
    }

    // FIXED_INCREMENT: the first bid is at least the opening price; every later bid is exactly price + increment.
    private static BidDecision PlaceFixed(AuctionState state, Guid bidderId, Money amount)
    {
        if (state.CurrentPrice is null && !amount.IsAtLeast(state.Terms.OpeningPrice))
        {
            return Reject(state, BidRejection.BelowMinimum);
        }

        if (state.CurrentPrice is not null && amount != MinimumNextBid(state))
        {
            return Reject(state, BidRejection.WrongIncrementAmount);
        }

        var next = state with
        {
            CurrentPrice = amount,
            LeaderId = bidderId,
            AcceptedBidCount = state.AcceptedBidCount + 1,
        };
        return new BidDecision(BidRejection.None, next, [new BidStep(bidderId, amount, BidSource.Bidder)]);
    }

    // PROXY: the amount is the bidder's private maximum. It may never be lowered, and must reach the
    // minimum next bid. Raising it to the same amount keeps the original order for tie-breaking.
    private static BidDecision PlaceProxy(AuctionState state, Guid bidderId, Money maximum)
    {
        var own = state.Maxima.Find(m => m.BidderId == bidderId);
        if (own is not null && !maximum.IsAtLeast(own.Maximum))
        {
            return Reject(state, BidRejection.MaximumLowered);
        }

        if (!maximum.IsAtLeast(MinimumNextBid(state)))
        {
            return Reject(state, BidRejection.BelowMinimum);
        }

        var sequence = state.AcceptedBidCount + 1;
        var maxima = own switch
        {
            null => state.Maxima.Add(new ProxyMaximum(bidderId, maximum, sequence)),
            _ when maximum.Cents > own.Maximum.Cents => state.Maxima.Replace(own, own with { Maximum = maximum, Sequence = sequence }),
            _ => state.Maxima,
        };
        var (leaderId, price) = ProxyResolver.Resolve(maxima, state.Terms);
        var next = state with
        {
            Maxima = maxima,
            LeaderId = leaderId,
            CurrentPrice = price,
            AcceptedBidCount = sequence,
        };
        return new BidDecision(BidRejection.None, next, ProxySteps(state, next, bidderId));
    }

    // Public history shows visible prices only; maxima are private. A bidder outbid at once
    // gets a step without an amount, followed by the leader's automatic answer at the new price. The price can
    // be capped by the leader's maximum, so it doesn't always reveal the outbid amount.
    private static List<BidStep> ProxySteps(AuctionState before, AuctionState after, Guid bidderId)
    {
        var price = after.CurrentPrice!.Value;
        if (after.LeaderId != bidderId)
        {
            return [new BidStep(bidderId, null, BidSource.Bidder), new BidStep(after.LeaderId!.Value, price, BidSource.Proxy)];
        }

        var unchanged = before.LeaderId == bidderId && before.CurrentPrice == price;
        return unchanged ? [] : [new BidStep(bidderId, price, BidSource.Bidder)];
    }

    private static BidDecision Reject(AuctionState state, BidRejection reason) => new(reason, state, []);
}

/// <summary>
/// The proxy price rule: the highest maximum leads, the earlier one on a tie; the price
/// is min(top, second + increment), or the opening price with one bidder; under RESERVE, once the leader's
/// maximum reaches the reserve, the price rises to at least the reserve.
/// </summary>
internal static class ProxyResolver
{
    public static (Guid LeaderId, Money Price) Resolve(IReadOnlyList<ProxyMaximum> maxima, AuctionTerms terms)
    {
        var ordered = maxima.OrderByDescending(m => m.Maximum.Cents).ThenBy(m => m.Sequence).ToList();
        var top = ordered[0];
        var price = ordered.Count == 1
            ? terms.OpeningPrice
            : Money.Min(top.Maximum, ordered[1].Maximum.Add(terms.Increment));

        if (terms.FloorType == FloorType.Reserve && terms.Reserve is { } reserve && top.Maximum.IsAtLeast(reserve))
        {
            price = Money.Max(price, reserve);
        }

        return (top.BidderId, price);
    }
}
