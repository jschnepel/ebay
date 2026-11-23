namespace Auction.Web.Client;

// The API's JSON shapes as the client reads them. Amounts are US cents; times are UTC instants.

public sealed record Account(Guid Id, string DisplayName, string Kind)
{
    /// <summary>The seeded admin, or an admin a visitor created in the demo: both see only the admin area.</summary>
    public bool IsAdmin => Kind is "admin" or "visitor_admin";
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

    public Guid? CoverImageId { get; init; }
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

    public IReadOnlyList<Guid> ImageIds { get; init; } = [];

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

// The admin area's shapes.

public sealed record RecentBid(Guid AuctionId, string Title, long? AmountCents, string Source, DateTimeOffset AcceptedAt);

public sealed record Activity(long ActiveAuctions, long BidsLastFiveMinutes, IReadOnlyList<RecentBid> RecentBids, int OnlineUsers);

public sealed record HealthItem(string Name, string State, string? Detail);

public sealed record AdminHealth(IReadOnlyList<HealthItem> Components, long OutboxBacklog);

public sealed record LatencySummary(int Count, long? P50Ms, long? P95Ms);

public sealed record AdminKpis(int WindowSeconds, LatencySummary ApiRequest, LatencySummary Bid, LatencySummary EngineCall, LatencySummary PublishLag);

public sealed record AuditEntry(long Id, DateTimeOffset At, Guid AdminId, string AdminName, string Action, string TargetType, Guid TargetId, string Reason);

public sealed record SimulatorOutcome(
    Guid RunId, string Profile, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool StoppedEarly, bool Passed, int Accepted, string? Error);

public sealed record SimulatorLive(
    string State, DateTimeOffset? Since, DateTimeOffset? ResumesAt, int ListingsLive, int BidsAccepted, DateTimeOffset? LastVerifiedAt, bool? LastVerifyPassed);

public sealed record SimulatorRun(string State, Guid? RunId, string? Profile, DateTimeOffset? StartedAt, int Attempts, int Accepted, SimulatorOutcome? Last)
{
    /// <summary>The always-on live marketplace; null where it isn't enabled.</summary>
    public SimulatorLive? Live { get; init; }
}

public sealed record DemoState(bool ResetPending, DateTimeOffset? LastResetAt, DateTimeOffset NextScheduledAt);

public sealed record SimulatorView(bool Available, IReadOnlyList<string> Profiles, SimulatorRun? Run);

public sealed record ModeratedAuction(
    Guid Id, string Title, string Status, Guid SellerId, string SellerName, long? CurrentPriceCents, long BidCount, DateTimeOffset EndsAt, long Version);

public sealed record UserSummary(
    Guid Id, string DisplayName, string Email, string Kind, bool Disabled, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt,
    long ListingCount, long BidCount);

public sealed record NotificationView(long Id, Guid? AuctionId, string? AuctionTitle, string Kind, string? Reason, DateTimeOffset CreatedAt, bool Read);

public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, string? NextCursor, long Unread);

public sealed record UploadedImage(Guid Id, string ContentType, int Width, int Height);
