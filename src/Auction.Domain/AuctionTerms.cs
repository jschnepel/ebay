namespace Auction.Domain;

/// <summary>How the floor works. <see cref="Reserve"/> is the default.</summary>
public enum FloorType
{
    Reserve,
    MinimumBid,
    Absolute,
}

/// <summary>How bids are entered.</summary>
public enum BidEntry
{
    Proxy,
    FixedIncrement,
}

/// <summary>
/// The seller's terms. Editable while the auction is a draft and frozen at publish.
/// </summary>
public sealed record AuctionTerms
{
    public required string Title { get; init; }

    public string Description { get; init; } = "";

    public required Money OpeningPrice { get; init; }

    /// <summary>Default $1.00, minimum 1 cent.</summary>
    public Money Increment { get; init; } = new(100);

    public FloorType FloorType { get; init; } = FloorType.Reserve;

    /// <summary>Hidden reserve; required for <see cref="FloorType.Reserve"/>, not allowed otherwise.</summary>
    public Money? Reserve { get; init; }

    public BidEntry BidEntry { get; init; } = BidEntry.Proxy;

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset EndsAt { get; init; }

    public int ImageCount { get; init; }
}
