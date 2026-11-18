using System.Collections.Immutable;

namespace Auction.Domain;

/// <summary>Lifecycle states. Closed and Cancelled are terminal.</summary>
public enum AuctionStatus
{
    Draft,
    Scheduled,
    Active,
    Suspended,
    Closed,
    Cancelled,
}

/// <summary>The one outcome a closed auction records.</summary>
public enum CloseOutcome
{
    Sold,
    ReserveNotMet,
    NoBids,
}

/// <summary>
/// A bidder's private maximum under <see cref="BidEntry.Proxy"/>. <see cref="Sequence"/> is the order in
/// which this amount was accepted, so the earlier of two equal maxima leads.
/// </summary>
public sealed record ProxyMaximum(UserId BidderId, Money Maximum, long Sequence);

/// <summary>
/// One auction's state. Immutable: every rule returns a new state, so the engine can apply it inside its
/// locked transaction and the tests can compare states directly.
/// </summary>
public sealed record AuctionState
{
    public required AuctionId Id { get; init; }

    public required UserId SellerId { get; init; }

    public required AuctionTerms Terms { get; init; }

    public AuctionStatus Status { get; init; } = AuctionStatus.Draft;

    /// <summary>Null until the first accepted bid.</summary>
    public Money? CurrentPrice { get; init; }

    public UserId? LeaderId { get; init; }

    /// <summary>Private maxima, one per bidder (proxy auctions only).</summary>
    public ImmutableList<ProxyMaximum> Maxima { get; init; } = [];

    public long AcceptedBidCount { get; init; }

    public CloseOutcome? Outcome { get; init; }

    /// <summary>The public "reserve met" badge. False for floor types without a reserve.</summary>
    public bool ReserveMet =>
        Terms.FloorType == FloorType.Reserve
        && CurrentPrice is { } price
        && Terms.Reserve is { } reserve
        && price.IsAtLeast(reserve);

    public static AuctionState NewDraft(AuctionId id, UserId sellerId, AuctionTerms terms) =>
        new() { Id = id, SellerId = sellerId, Terms = terms };
}
