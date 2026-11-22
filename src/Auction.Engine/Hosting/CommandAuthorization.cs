using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Auction.Engine.Hosting;

/// <summary>The TLS handshake validates the API certificate. Its role assertion applies only to this call.</summary>
internal sealed class CommandAuthorization : Interceptor
{
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        if (context.GetHttpContext().Connection.ClientCertificate is null)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "An API client certificate is required."));
        }

        var requiredRole = request is Contracts.V1.SuspendRequest or Contracts.V1.ResumeRequest or Contracts.V1.AdminCancelRequest
            ? "admin" : "participant";
        if (context.RequestHeaders.GetValue("x-auction-role") != requiredRole)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The account cannot issue this command."));
        }

        return continuation(request, context);
    }
}
