namespace Auction.Api.Admin;

/// <summary>
/// Redis as the admin health panel shows it: "degraded" when unreachable, never "down", because the API
/// keeps working without it (REL-02).
/// </summary>
internal sealed class RedisHealth(Caching.RedisJobs redis)
{
    public async Task<HealthItem> CheckAsync()
    {
        if (!redis.Configured)
        {
            return new HealthItem("Redis", "not configured", null);
        }

        var reached = await redis.TryAsync<bool?>(async db =>
        {
            await db.PingAsync();
            return true;
        });
        return reached is true ? new HealthItem("Redis", "ok", null) : new HealthItem("Redis", "degraded", "unreachable");
    }
}
