using StackExchange.Redis;

namespace Auction.Api.Admin;

/// <summary>
/// Redis as the admin health panel shows it: "degraded" when unreachable, never "down", because the API
/// keeps working without it (REL-02).
/// </summary>
internal sealed class RedisHealth(IConfiguration configuration) : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer?> _redis = new(() =>
        configuration.GetConnectionString("Redis") is { Length: > 0 } connection ? ConnectionMultiplexer.Connect(connection) : null);

    public async Task<HealthItem> CheckAsync()
    {
        try
        {
            if (_redis.Value is not { } redis)
            {
                return new HealthItem("Redis", "not configured", null);
            }

            await redis.GetDatabase().PingAsync();
            return new HealthItem("Redis", "ok", null);
        }
        catch (RedisException e)
        {
            return new HealthItem("Redis", "degraded", e.GetType().Name);
        }
    }

    public void Dispose()
    {
        if (_redis.IsValueCreated)
        {
            _redis.Value?.Dispose();
        }
    }
}
