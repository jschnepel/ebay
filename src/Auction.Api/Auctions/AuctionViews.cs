using System.Text.Json.Serialization;

namespace Auction.Api.Auctions;

/// <summary>
/// An auction as anyone may see it. Never carries the reserve amount, a bidder's maximum or any user's
/// identity: the leader shows only as <see cref="AuctionDetail.YouLead"/> to the leader themselves.
/// </summary>
public record AuctionSummary
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public required long OpeningPriceCents { get; init; }

    /// <summary>Null until the first accepted bid.</summary>
    public long? CurrentPriceCents { get; init; }

    public required long BidCount { get; init; }

    public required string FloorType { get; init; }

    public required string BidEntry { get; init; }

    /// <summary>For a reserve auction, whether the current price has reached the reserve; otherwise null.</summary>
    public bool? ReserveMet { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset EndsAt { get; init; }

    /// <summary>Rises with every change; a client applies an update only if it is newer than what it shows.</summary>
    public required long Version { get; init; }

    /// <summary>The cover image, served at /api/v1/images/{id}; null without images.</summary>
    public Guid? CoverImageId { get; init; }
}

/// <summary>One auction in full, as the viewer may see it.</summary>
public sealed record AuctionDetail : AuctionSummary
{
    public required string Description { get; init; }

    public required long IncrementCents { get; init; }

    /// <summary>The smallest bid (fixed increment) or maximum (proxy) accepted right now.</summary>
    public required long MinimumNextBidCents { get; init; }

    /// <summary>The hidden reserve: present only for the seller.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ReserveCents { get; init; }

    /// <summary>The viewer's own proxy maximum, which the price doesn't show: present only for that bidder.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? YourMaximumCents { get; init; }

    /// <summary>Every image in display order, the cover among them.</summary>
    public IReadOnlyList<Guid> ImageIds { get; init; } = [];

    /// <summary>sold, reserve_not_met or no_bids once closed.</summary>
    public string? Outcome { get; init; }

    public required bool YouAreSeller { get; init; }

    public required bool YouLead { get; init; }

    /// <summary>
    /// Whether the viewer has an accepted bid here, so an outbid bidder can be told. Not required: the read cache
    /// still holds entries from the release before it, and it's set for each viewer anyway.
    /// </summary>
    public bool YouHaveBid { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary>
/// One auction as stored, before it is shaped for a viewer: the public view plus the seller, the leader and
/// the reserve, which only the API sees. This is what the read cache holds.
/// </summary>
public sealed record DetailRow(AuctionDetail Public, Guid SellerId, Guid? LeaderId, long? ReserveCents);

/// <summary>An auction the viewer has bid on, and whether they lead it now.</summary>
public sealed record BiddingSummary : AuctionSummary
{
    public required bool YouLead { get; init; }

    public static BiddingSummary From(AuctionSummary summary, bool youLead) => new()
    {
        Id = summary.Id,
        Title = summary.Title,
        Status = summary.Status,
        OpeningPriceCents = summary.OpeningPriceCents,
        CurrentPriceCents = summary.CurrentPriceCents,
        BidCount = summary.BidCount,
        FloorType = summary.FloorType,
        BidEntry = summary.BidEntry,
        ReserveMet = summary.ReserveMet,
        StartsAt = summary.StartsAt,
        EndsAt = summary.EndsAt,
        Version = summary.Version,
        CoverImageId = summary.CoverImageId,
        YouLead = youLead,
    };
}

/// <summary>An accepted bid in the public history, with the bidder shown only as a per-auction label.</summary>
public sealed class BidView
{
    public required long Sequence { get; init; }

    /// <summary>"You", or "Bidder 1", "Bidder 2", … in order of each bidder's first bid.</summary>
    public required string Bidder { get; init; }

    /// <summary>Null for a bid outbid at once, so its maximum stays private.</summary>
    public long? AmountCents { get; init; }

    /// <summary>bidder, or proxy for an automatic step.</summary>
    public required string Source { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }
}

/// <summary>The directory's search (UI-03): a title keyword, a status, and how soon "ending soon" is.</summary>
public sealed record DirectoryFilter(string? Query, string? Status, TimeSpan EndingSoonWindow)
{
    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(StringComparer.Ordinal) { "active", "ending_soon", "closed" };

    public bool IsEmpty => Query is null && Status is null;
}

/// <summary>The directory's query string: a page, an optional title keyword and an optional status.</summary>
public sealed record DirectoryQuery(int? PageSize, string? Cursor, string? Q, string? Status);

/// <summary>One page of a list, in a fixed order; <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>The engine's answer to an accepted command.</summary>
public sealed class CommandResponse
{
    public required Guid AuctionId { get; init; }

    public long? Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BidSequence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CurrentPriceCents { get; init; }

    /// <summary>For a bid: whether the bidder leads after it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? YouLead { get; init; }

    /// <summary>True when this repeats the recorded result of an earlier request with the same key.</summary>
    public required bool Replayed { get; init; }
}
