using Grpc.Health.V1;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Auction.Api.Hosting;

/// <summary>
/// Readiness (HOST-01): the API serves only while PostgreSQL and the engine answer. Redis isn't part of it:
/// without Redis the API still works, and admin health shows it as degraded (REL-02).
/// </summary>
internal static class Readiness
{
    public const string Tag = "ready";

    public static IHealthChecksBuilder AddReadiness(this IHealthChecksBuilder checks) =>
        checks.AddCheck<DatabaseReady>("postgresql", tags: [Tag]).AddCheck<EngineReady>("engine", tags: [Tag]);

    private sealed class DatabaseReady(NpgsqlDataSource dataSource) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await using var command = dataSource.CreateCommand("SELECT 1");
                await command.ExecuteScalarAsync(cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (NpgsqlException e)
            {
                return HealthCheckResult.Unhealthy("PostgreSQL is unreachable.", e);
            }
        }
    }

    private sealed class EngineReady(Health.HealthClient engine) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var reply = await engine.CheckAsync(new HealthCheckRequest(), deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: cancellationToken);
                return reply.Status == HealthCheckResponse.Types.ServingStatus.Serving
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("The engine isn't serving.");
            }
            catch (Grpc.Core.RpcException e)
            {
                return HealthCheckResult.Unhealthy("The engine is unreachable.", e);
            }
        }
    }
}
