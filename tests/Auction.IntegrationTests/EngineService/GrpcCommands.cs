using Auction.Domain;
using Auction.Engine;
using Google.Protobuf.WellKnownTypes;
using V1 = Auction.Contracts.V1;

namespace Auction.IntegrationTests.EngineService;

/// <summary>
/// The engine's commands over gRPC, mapped back to engine outcomes so the same assertions run against the
/// in-process engine and the container.
/// </summary>
internal sealed class GrpcCommands(V1.AuctionCommandService.AuctionCommandServiceClient client)
{
    private static DateTime Deadline => DateTime.UtcNow.AddSeconds(30);

    public async Task<CommandOutcome> CreateDraftAsync(CommandContext context, AuctionTerms terms)
    {
        var request = new V1.CreateDraftRequest { RequestId = context.IdempotencyKey, ActorId = context.Actor.ToString(), Terms = ToContract(terms) };
        return ToOutcome((await client.CreateDraftAsync(request, deadline: Deadline)).Result);
    }

    public async Task<CommandOutcome> PublishAsync(CommandContext context, AuctionId auctionId)
    {
        var request = new V1.PublishRequest { RequestId = context.IdempotencyKey, ActorId = context.Actor.ToString(), AuctionId = auctionId.ToString() };
        return ToOutcome((await client.PublishAsync(request, deadline: Deadline)).Result);
    }

    public async Task<CommandOutcome> PlaceBidAsync(CommandContext context, AuctionId auctionId, long cents)
    {
        var request = new V1.PlaceBidRequest
        {
            RequestId = context.IdempotencyKey,
            ActorId = context.Actor.ToString(),
            AuctionId = auctionId.ToString(),
            AmountCents = cents,
        };
        return ToOutcome((await client.PlaceBidAsync(request, deadline: Deadline)).Result);
    }

    public static V1.AuctionTerms ToContract(AuctionTerms terms)
    {
        var contract = new V1.AuctionTerms
        {
            Title = terms.Title,
            Description = terms.Description,
            OpeningPriceCents = terms.OpeningPrice.Cents,
            IncrementCents = terms.Increment.Cents,
            FloorType = terms.FloorType switch
            {
                FloorType.Reserve => V1.FloorType.Reserve,
                FloorType.MinimumBid => V1.FloorType.MinimumBid,
                _ => V1.FloorType.Absolute,
            },
            BidEntry = terms.BidEntry == BidEntry.Proxy ? V1.BidEntry.Proxy : V1.BidEntry.FixedIncrement,
            StartsAt = Timestamp.FromDateTimeOffset(terms.StartsAt),
            EndsAt = Timestamp.FromDateTimeOffset(terms.EndsAt),
        };
        if (terms.Reserve is { } reserve)
        {
            contract.ReserveCents = reserve.Cents;
        }

        return contract;
    }

    private static CommandOutcome ToOutcome(V1.CommandResult result) => new()
    {
        Error = result.Succeeded ? CommandError.None : System.Enum.Parse<CommandError>(result.Error.ToString()),
        AuctionId = result.AuctionId.Length > 0 ? new AuctionId(Guid.Parse(result.AuctionId)) : null,
        Version = result.HasVersion ? result.Version : null,
        BidSequence = result.HasBidSequence ? result.BidSequence : null,
        LeaderId = result.LeaderId.Length > 0 ? new UserId(Guid.Parse(result.LeaderId)) : null,
        CurrentPrice = result.HasCurrentPriceCents ? new Money(result.CurrentPriceCents) : null,
        FieldErrors = [.. result.FieldErrors.Select(e => new FieldError(e.Field, e.Code))],
        Replayed = result.Replayed,
    };
}
