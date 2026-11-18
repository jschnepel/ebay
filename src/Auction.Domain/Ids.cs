namespace Auction.Domain;

/// <summary>Identifies an auction. A distinct type, so it can't be passed where a user id is expected.</summary>
public readonly record struct AuctionId(Guid Value)
{
    /// <summary>A new time-ordered (version 7) id, which keeps index inserts local.</summary>
    public static AuctionId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a user account: a seller, a bidder or an admin.</summary>
public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}
