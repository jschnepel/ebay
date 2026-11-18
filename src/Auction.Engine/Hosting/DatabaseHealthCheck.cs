using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Auction.Engine.Hosting;

/// <summary>The engine is healthy only while it can reach PostgreSQL, which it needs for every command.</summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
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
