using System.Text.Json.Serialization;
using Auction.Domain;

namespace Auction.Engine;

/// <summary>Why a command was refused. <see cref="None"/> means it succeeded.</summary>
public enum CommandError
{
    None,

    /// <summary>The idempotency key is empty or longer than 255 characters.</summary>
    InvalidKey,

    /// <summary>The key was already used by this actor for a different request.</summary>
    KeyReused,

    /// <summary>The auction stayed locked past the lock timeout. Nothing was recorded; safe to retry.</summary>
    Busy,

    NotFound,
    NotOwner,
    InvalidState,
    ValidationFailed,
    HasBids,
    AuctionNotActive,
    AuctionEnded,
    SelfBid,
    AboveCap,
    BelowMinimum,
    WrongIncrementAmount,
    MaximumLowered,
}

/// <summary>
/// The result of a command. Stored under the idempotency key, accepted or rejected, and returned as-is
/// when the same request is repeated.
/// </summary>
public sealed record CommandOutcome
{
    public CommandError Error { get; init; }

    public AuctionId? AuctionId { get; init; }

    /// <summary>The auction's version after the command.</summary>
    public long? Version { get; init; }

    /// <summary>For an accepted bid: its place in the order of accepted bids (1, 2, 3, …).</summary>
    public long? BidSequence { get; init; }

    public UserId? LeaderId { get; init; }

    public Money? CurrentPrice { get; init; }

    public IReadOnlyList<FieldError> FieldErrors { get; init; } = [];

    /// <summary>True when this outcome was recorded by an earlier request with the same key.</summary>
    [JsonIgnore]
    public bool Replayed { get; init; }

    [JsonIgnore]
    public bool Succeeded => Error == CommandError.None;
}

/// <summary>Who is acting, and the idempotency key their client sent.</summary>
public sealed record CommandContext(UserId Actor, string IdempotencyKey);

public sealed record EngineOptions
{
    public AuctionLimits Limits { get; init; } = AuctionLimits.Default;

    /// <summary>How long a command waits for the auction lock before it gives up with <see cref="CommandError.Busy"/>.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Attempts per command when a lock times out or a deadlock is detected.</summary>
    public int MaxAttempts { get; init; } = 3;
}
