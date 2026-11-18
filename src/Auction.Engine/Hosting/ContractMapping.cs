using Auction.Domain;
using Grpc.Core;
using V1 = Auction.Contracts.V1;

namespace Auction.Engine.Hosting;

/// <summary>
/// Between the auction.v1 messages and the engine's types. A malformed request (an id that isn't a UUID,
/// missing terms) is INVALID_ARGUMENT; everything else is decided by the engine and returned as a result.
/// </summary>
internal static class ContractMapping
{
    public static CommandContext Context(string requestId, string actorId) =>
        new(new UserId(ParseId(actorId, "actor_id")), requestId);

    public static AuctionId AuctionIdOf(string auctionId) => new(ParseId(auctionId, "auction_id"));

    public static AuctionTerms ToTerms(V1.AuctionTerms? terms)
    {
        if (terms is null || terms.StartsAt is null || terms.EndsAt is null)
        {
            throw Invalid("terms, with starts_at and ends_at, are required");
        }

        return new AuctionTerms
        {
            Title = terms.Title,
            Description = terms.Description,
            OpeningPrice = new Money(terms.OpeningPriceCents),
            Increment = new Money(terms.IncrementCents),
            FloorType = terms.FloorType switch
            {
                V1.FloorType.Reserve => FloorType.Reserve,
                V1.FloorType.MinimumBid => FloorType.MinimumBid,
                V1.FloorType.Absolute => FloorType.Absolute,
                _ => throw Invalid("floor_type is required"),
            },
            Reserve = terms.HasReserveCents ? new Money(terms.ReserveCents) : null,
            BidEntry = terms.BidEntry switch
            {
                V1.BidEntry.Proxy => BidEntry.Proxy,
                V1.BidEntry.FixedIncrement => BidEntry.FixedIncrement,
                _ => throw Invalid("bid_entry is required"),
            },
            StartsAt = terms.StartsAt.ToDateTimeOffset(),
            EndsAt = terms.EndsAt.ToDateTimeOffset(),
        };
    }

    public static V1.CommandResult ToResult(CommandOutcome outcome)
    {
        var result = new V1.CommandResult
        {
            Succeeded = outcome.Succeeded,
            Error = outcome.Succeeded ? V1.CommandError.Unspecified : Enum.Parse<V1.CommandError>(outcome.Error.ToString()),
            AuctionId = outcome.AuctionId?.ToString() ?? "",
            LeaderId = outcome.LeaderId?.ToString() ?? "",
            Replayed = outcome.Replayed,
        };
        result.FieldErrors.AddRange(outcome.FieldErrors.Select(e => new V1.FieldError { Field = e.Field, Code = e.Code }));
        if (outcome.Version is { } version)
        {
            result.Version = version;
        }

        if (outcome.BidSequence is { } sequence)
        {
            result.BidSequence = sequence;
        }

        if (outcome.CurrentPrice is { } price)
        {
            result.CurrentPriceCents = price.Cents;
        }

        return result;
    }

    private static Guid ParseId(string value, string field) =>
        Guid.TryParse(value, out var id) ? id : throw Invalid($"{field} must be a UUID");

    private static RpcException Invalid(string detail) => new(new Status(StatusCode.InvalidArgument, detail));
}
