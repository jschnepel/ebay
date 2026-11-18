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
    public static LifecycleResult EditTerms(AuctionState state, Guid actorId, AuctionTerms terms) =>
        throw new NotImplementedException();

    public static LifecycleResult Publish(AuctionState state, Guid actorId, DateTimeOffset now, AuctionLimits limits) =>
        throw new NotImplementedException();

    public static LifecycleResult Activate(AuctionState state, DateTimeOffset now) =>
        throw new NotImplementedException();

    public static LifecycleResult Close(AuctionState state, DateTimeOffset now) =>
        throw new NotImplementedException();

    public static LifecycleResult CancelByOwner(AuctionState state, Guid actorId) =>
        throw new NotImplementedException();

    public static LifecycleResult Suspend(AuctionState state) =>
        throw new NotImplementedException();

    public static LifecycleResult Resume(AuctionState state, DateTimeOffset now) =>
        throw new NotImplementedException();
}
