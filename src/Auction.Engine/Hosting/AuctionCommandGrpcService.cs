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

    public override async Task<V1.DeleteDraftResponse> DeleteDraft(V1.DeleteDraftRequest request, ServerCallContext context)
    {
        var outcome = await engine.DeleteDraftAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), context.CancellationToken);
        return new V1.DeleteDraftResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.SuspendResponse> Suspend(V1.SuspendRequest request, ServerCallContext context)
    {
        var outcome = await engine.SuspendAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), request.Reason, context.CancellationToken);
        return new V1.SuspendResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.ResumeResponse> Resume(V1.ResumeRequest request, ServerCallContext context)
    {
        var outcome = await engine.ResumeAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), request.Reason, context.CancellationToken);
        return new V1.ResumeResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.AdminCancelResponse> AdminCancel(V1.AdminCancelRequest request, ServerCallContext context)
    {
        var outcome = await engine.AdminCancelAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), request.Reason, context.CancellationToken);
        return new V1.AdminCancelResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.AttachImageResponse> AttachImage(V1.AttachImageRequest request, ServerCallContext context)
    {
        var outcome = await engine.AttachImageAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), ParseId(request.ImageId, "image_id"), context.CancellationToken);
        return new V1.AttachImageResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.RemoveImageResponse> RemoveImage(V1.RemoveImageRequest request, ServerCallContext context)
    {
        var outcome = await engine.RemoveImageAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), ParseId(request.ImageId, "image_id"), context.CancellationToken);
        return new V1.RemoveImageResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.ReorderImagesResponse> ReorderImages(V1.ReorderImagesRequest request, ServerCallContext context)
    {
        var order = request.ImageIds.Select(id => ParseId(id, "image_ids")).ToList();
        var outcome = await engine.ReorderImagesAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), order, context.CancellationToken);
        return new V1.ReorderImagesResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.SetCoverImageResponse> SetCoverImage(V1.SetCoverImageRequest request, ServerCallContext context)
    {
        var outcome = await engine.SetCoverAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), ParseId(request.ImageId, "image_id"), context.CancellationToken);
        return new V1.SetCoverImageResponse { Result = ToResult(outcome) };
    }

    public override async Task<V1.PlaceBidResponse> PlaceBid(V1.PlaceBidRequest request, ServerCallContext context)
    {
        var outcome = await engine.PlaceBidAsync(
            Context(request.RequestId, request.ActorId), AuctionIdOf(request.AuctionId), new Money(request.AmountCents), context.CancellationToken);
        var result = ToResult(outcome);
        if (outcome.DecidedAt is { } decidedAt)
        {
            result.DecidedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(decidedAt);
        }

        return new V1.PlaceBidResponse { Result = result };
    }
}
