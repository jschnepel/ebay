namespace Auction.Domain;

/// <summary>Why a lifecycle command was refused. <see cref="None"/> means it succeeded.</summary>
public enum LifecycleError
{
    None,
    NotOwner,
    InvalidState,
    NotDue,
    HasBids,
    Ended,
    ValidationFailed,
}

/// <summary>The result of a lifecycle command.</summary>
public sealed record LifecycleResult(LifecycleError Error, AuctionState State, IReadOnlyList<FieldError> FieldErrors)
{
    public bool Succeeded => Error == LifecycleError.None;
}

/// <summary>
/// Lifecycle transitions: publish, activate, close, cancel, suspend and resume.
/// Each command checks the state it starts from, so running it twice changes nothing.
/// </summary>
public static class Lifecycle
{
    /// <summary>Owner-only, and only while a draft: published terms are frozen.</summary>
    public static LifecycleResult EditTerms(AuctionState state, UserId actorId, AuctionTerms terms)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(terms);
        if (actorId != state.SellerId)
        {
            return Fail(state, LifecycleError.NotOwner);
        }

        return state.Status == AuctionStatus.Draft ? Ok(state with { Terms = terms }) : Fail(state, LifecycleError.InvalidState);
    }

    /// <summary>
    /// Draft → Scheduled, or Active when the start has passed ("start now": the start becomes <paramref name="now"/>).
    /// <paramref name="now"/> is the database time read under the auction lock.
    /// </summary>
    public static LifecycleResult Publish(AuctionState state, UserId actorId, DateTimeOffset now, AuctionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (actorId != state.SellerId)
        {
            return Fail(state, LifecycleError.NotOwner);
        }

        if (state.Status != AuctionStatus.Draft)
        {
            return Fail(state, LifecycleError.InvalidState);
        }

        var errors = ListingValidator.ValidateForPublish(state.Terms, now, limits);
        if (errors.Count > 0)
        {
            return new LifecycleResult(LifecycleError.ValidationFailed, state, errors);
        }

        return state.Terms.StartsAt > now
            ? Ok(state with { Status = AuctionStatus.Scheduled })
            : Ok(state with { Status = AuctionStatus.Active, Terms = state.Terms with { StartsAt = now } });
    }

    /// <summary>Scheduled → Active at the start. Past the end it closes instead.</summary>
    public static LifecycleResult Activate(AuctionState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status != AuctionStatus.Scheduled)
        {
            return Fail(state, LifecycleError.InvalidState);
        }

        if (now < state.Terms.StartsAt)
        {
            return Fail(state, LifecycleError.NotDue);
        }

        return now >= state.Terms.EndsAt ? Close(state, now) : Ok(state with { Status = AuctionStatus.Active });
    }

    /// <summary>Scheduled, Active or Suspended → Closed at the end, with one outcome.</summary>
    public static LifecycleResult Close(AuctionState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status is not (AuctionStatus.Scheduled or AuctionStatus.Active or AuctionStatus.Suspended))
        {
            return Fail(state, LifecycleError.InvalidState);
        }

        if (now < state.Terms.EndsAt)
        {
            return Fail(state, LifecycleError.NotDue);
        }

        return Ok(state with { Status = AuctionStatus.Closed, Outcome = DecideOutcome(state) });
    }

    /// <summary>Owner-only, from Scheduled or Active, before any accepted bid.</summary>
    public static LifecycleResult CancelByOwner(AuctionState state, UserId actorId)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (actorId != state.SellerId)
        {
            return Fail(state, LifecycleError.NotOwner);
        }

        if (state.Status is not (AuctionStatus.Scheduled or AuctionStatus.Active))
        {
            return Fail(state, LifecycleError.InvalidState);
        }

        return state.AcceptedBidCount > 0
            ? Fail(state, LifecycleError.HasBids)
            : Ok(state with { Status = AuctionStatus.Cancelled });
    }

    /// <summary>Admin moderation: Active → Suspended. The end time is not extended.</summary>
    public static LifecycleResult Suspend(AuctionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Status == AuctionStatus.Active
            ? Ok(state with { Status = AuctionStatus.Suspended })
            : Fail(state, LifecycleError.InvalidState);
    }

    /// <summary>Admin moderation: Suspended → Active, only before the end.</summary>
    public static LifecycleResult Resume(AuctionState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Status != AuctionStatus.Suspended)
        {
            return Fail(state, LifecycleError.InvalidState);
        }

        return now >= state.Terms.EndsAt ? Fail(state, LifecycleError.Ended) : Ok(state with { Status = AuctionStatus.Active });
    }

    private static CloseOutcome DecideOutcome(AuctionState state)
    {
        if (state.AcceptedBidCount == 0)
        {
            return CloseOutcome.NoBids;
        }

        return state.Terms.FloorType == FloorType.Reserve && !state.ReserveMet ? CloseOutcome.ReserveNotMet : CloseOutcome.Sold;
    }

    private static LifecycleResult Ok(AuctionState state) => new(LifecycleError.None, state, []);

    private static LifecycleResult Fail(AuctionState state, LifecycleError error) => new(error, state, []);
}
