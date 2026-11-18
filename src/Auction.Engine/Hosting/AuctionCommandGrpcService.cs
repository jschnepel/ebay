using Auction.Domain;
using Grpc.Core;
using static Auction.Engine.Hosting.ContractMapping;
using V1 = Auction.Contracts.V1;

namespace Auction.Engine.Hosting;

/// <summary>The auction.v1 commands, each passed straight to <see cref="AuctionEngine"/> with the call's cancellation.</summary>
internal sealed class AuctionCommandGrpcService(AuctionEngine engine) : V1.AuctionCommandService.AuctionCommandServiceBase
{
    public override async Task<V1.CreateDraftResponse> CreateDraft(V1.CreateDraftRequest request, ServerCallContext context)
    {
        var outcome = await engine.CreateDraftAsync(
            Context(request.RequestId, request.ActorId), ToTerms(request.Terms), context.CancellationToken);
        return new V1.CreateDraftResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.EditDraftResponse> EditDraft(V1.EditDraftRequest request, ServerCallContext context)
    {
        var outcome = await engine.EditDraftAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), ToTerms(request.Terms), context.CancellationToken);
        return new V1.EditDraftResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.PublishResponse> Publish(V1.PublishRequest request, ServerCallContext context)
    {
        var outcome = await engine.PublishAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), context.CancellationToken);
        return new V1.PublishResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.CancelResponse> Cancel(V1.CancelRequest request, ServerCallContext context)
    {
        var outcome = await engine.CancelAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), context.CancellationToken);
        return new V1.CancelResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.PlaceBidResponse> PlaceBid(V1.PlaceBidRequest request, ServerCallContext context)
    {
        var outcome = await engine.PlaceBidAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), new Money(request.AmountCents), context.CancellationToken);
        return new V1.PlaceBidResponse { Result = ToResult(outcome) };
    }
}
