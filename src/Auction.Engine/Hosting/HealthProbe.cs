using Grpc.Core;
using Grpc.Health.V1;
using Grpc.Net.Client;

namespace Auction.Engine.Hosting;

/// <summary>
/// <c>dotnet Auction.Engine.dll --health-check</c>: asks the running engine's gRPC health service whether
/// it is serving. Built into the binary because the chiseled image has no shell or curl for a probe.
/// </summary>
internal static class HealthProbe
{
    public const string Argument = "--health-check";

    private static readonly char[] PortSeparators = [';', ','];

    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(PortSeparators)[0] ?? "8080";
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
        try
        {
            var reply = await new Health.HealthClient(channel)
                .CheckAsync(new HealthCheckRequest(), deadline: DateTime.UtcNow.AddSeconds(3));
            return reply.Status == HealthCheckResponse.Types.ServingStatus.Serving ? 0 : 1;
        }
        catch (RpcException)
        {
            return 1;
        }
    }
}
