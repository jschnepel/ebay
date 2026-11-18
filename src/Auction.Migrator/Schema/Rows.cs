namespace Auction.Migrator.Schema;

// One class per table. Money is integer cents, times are UTC timestamptz, and enum-like columns hold
// snake_case text guarded by CHECK constraints (see TableConfigurations).

/// <summary>An auction from draft to close. A draft may break the listing rules; publishing checks them and freezes its terms.</summary>
public sealed class AuctionRow
{
    public Guid Id { get; set; }

    public Guid SellerId { get; set; }

    public string Status { get; set; } = "draft";

    public string? Title { get; set; }

    public string Description { get; set; } = "";

    public long? OpeningPriceCents { get; set; }

    public long IncrementCents { get; set; } = 100;

    public string FloorType { get; set; } = "reserve";

    public long? ReserveCents { get; set; }

    public string BidEntry { get; set; } = "proxy";

    public DateTimeOffset? StartsAt { get; set; }

    public DateTimeOffset? EndsAt { get; set; }

    public long? CurrentPriceCents { get; set; }

    public Guid? LeaderId { get; set; }

    public long AcceptedBidCount { get; set; }

    /// <summary>Raised by every change, so clients can ignore an update older than what they show.</summary>
    public long Version { get; set; }

    public string? Outcome { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A bidder's private maximum on a proxy auction. Only the engine and the verifier can read it.</summary>
public sealed class ProxyMaximumRow
{
    public Guid AuctionId { get; set; }

    public Guid BidderId { get; set; }

    public long MaximumCents { get; set; }

    /// <summary>When this amount was accepted; the earlier of two equal maxima leads.</summary>
    public long Sequence { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One step of the public, append-only bid history. Rejected bids are never stored here.</summary>
public sealed class BidRow
{
    public Guid AuctionId { get; set; }

    /// <summary>1, 2, 3, … per auction, with no gaps.</summary>
    public long Seq { get; set; }

    public Guid BidderId { get; set; }

    /// <summary>Null for a bid that was outbid at once, so its maximum stays private.</summary>
    public long? AmountCents { get; set; }

    public string Source { get; set; } = "bidder";

    public DateTimeOffset AcceptedAt { get; set; }

    /// <summary>The engine command that produced this step; a proxy answer shares its trigger's id.</summary>
    public Guid RequestId { get; set; }
}

/// <summary>The recorded outcome of a command, accepted or rejected, replayed for a repeated key.</summary>
public sealed class IdempotencyRecordRow
{
    public Guid ActorId { get; set; }

    public string Key { get; set; } = "";

    /// <summary>SHA-256 of the request, so the same key with a different payload is detected.</summary>
    public byte[] RequestHash { get; set; } = [];

    public string Outcome { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A change event written in the same transaction as the change, relayed to live clients.</summary>
public sealed class OutboxEventRow
{
    public long Id { get; set; }

    public Guid AuctionId { get; set; }

    public long AuctionVersion { get; set; }

    public string Type { get; set; } = "";

    public string Payload { get; set; } = "{}";

    /// <summary>The writing transaction, so the relay reads only rows every earlier transaction has committed.</summary>
    public ulong Xid { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }
}

public sealed class NotificationRow
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? AuctionId { get; set; }

    public string Kind { get; set; } = "";

    public string? Reason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Every admin action, with its reason.</summary>
public sealed class AdminAuditRow
{
    public long Id { get; set; }

    public Guid AdminId { get; set; }

    public string Action { get; set; } = "";

    public string TargetType { get; set; } = "";

    public Guid TargetId { get; set; }

    public string Reason { get; set; } = "";

    public DateTimeOffset At { get; set; }
}

/// <summary>Per-user counters, locked FOR UPDATE so concurrent requests can't overshoot a cap.</summary>
public sealed class UserLimitsRow
{
    public Guid UserId { get; set; }

    public int OpenDrafts { get; set; }

    public DateOnly? PublishedOn { get; set; }

    public int PublishedCount { get; set; }
}

/// <summary>
/// An account. Self-registered accounts have only a display name and an email; seeded, simulator and
/// admin accounts always have a password hash. The email is unique ignoring letter case.
/// </summary>
public sealed class UserRow
{
    public Guid Id { get; set; }

    public string Email { get; set; } = "";

    /// <summary>The email in upper case (invariant culture), for the case-insensitive unique index.</summary>
    public string NormalizedEmail { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Kind { get; set; } = "self_registered";

    public string? PasswordHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DisabledAt { get; set; }
}
