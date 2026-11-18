namespace Auction.Web.Client;

// The API's JSON shapes as the client reads them. Amounts are US cents; times are UTC instants.

public sealed record Account(Guid Id, string DisplayName, string Kind)
{
    public bool IsAdmin => Kind == "admin";
}

public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

public record AuctionSummary
{
    public Guid Id { get; init; }

    public string Title { get; init; } = "";

    public string Status { get; init; } = "";

    public long OpeningPriceCents { get; init; }

    public long? CurrentPriceCents { get; init; }

    public long BidCount { get; init; }

    public string FloorType { get; init; } = "";

    public string BidEntry { get; init; } = "";

    public bool? ReserveMet { get; init; }

    public DateTimeOffset StartsAt { get; init; }

    public DateTimeOffset EndsAt { get; init; }

    public long Version { get; init; }
}

public sealed record BiddingSummary : AuctionSummary
{
    public bool YouLead { get; init; }
}

public sealed record AuctionDetail : AuctionSummary
{
    public string Description { get; init; } = "";

    public long IncrementCents { get; init; }

    public long MinimumNextBidCents { get; init; }

    public long? ReserveCents { get; init; }

    public string? Outcome { get; init; }

    public bool YouAreSeller { get; init; }

    public bool YouLead { get; init; }
}

public sealed record BidView(long Sequence, string Bidder, long? AmountCents, string Source, DateTimeOffset AcceptedAt);

public sealed record CommandResponse(Guid AuctionId, long? Version, long? CurrentPriceCents, bool? YouLead, bool Replayed);

public sealed record DraftTerms
{
    public string Title { get; init; } = "";

    public string Description { get; init; } = "";

    public long? OpeningPriceCents { get; init; }

    public long? IncrementCents { get; init; }

    public string FloorType { get; init; } = "reserve";

    public long? ReserveCents { get; init; }

    public string BidEntry { get; init; } = "proxy";

    public DateTimeOffset? StartsAt { get; init; }

    public DateTimeOffset? EndsAt { get; init; }
}

/// <summary>An RFC 9457 problem as the API sends it, with its <c>code</c> and any field errors.</summary>
public sealed record Problem
{
    public int Status { get; init; }

    public string? Title { get; init; }

    public string? Code { get; init; }

    public Dictionary<string, string[]>? Errors { get; init; }

    public static Problem Unreachable { get; } = new() { Status = 0, Title = "The server can't be reached. Check the connection and try again." };
}
