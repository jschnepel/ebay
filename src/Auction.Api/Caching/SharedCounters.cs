using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Auction.Api.Caching;

/// <summary>
/// Fixed-window counters shared by every API instance through Redis (§8 rate limiting), so a limit holds
/// across replicas. When Redis can't answer, each instance counts on its own instead: limits loosen by the
/// number of instances but never block everyone (REL-02).
/// </summary>
public sealed class SharedCounters(RedisJobs redis, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (long Window, long Count)> _local = new(StringComparer.Ordinal);

    /// <summary>Counts one more event for <paramref name="key"/> in the current window and returns the count so far.</summary>
    public async Task<(long Count, TimeSpan RetryAfter)> IncrementAsync(string key, TimeSpan window)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var size = (long)window.TotalMilliseconds;
        var bucket = now / size;
        var retryAfter = TimeSpan.FromMilliseconds(((bucket + 1) * size) - now);
        var redisKey = $"limit:{key}:{bucket}";
        var shared = await redis.TryAsync<long?>(async db =>
        {
            var count = await db.StringIncrementAsync(redisKey);
            if (count == 1)
            {
                await db.KeyExpireAsync(redisKey, window + TimeSpan.FromSeconds(1));
            }

            return count;
        });
        if (shared is { } sharedCount)
        {
            return (sharedCount, retryAfter);
        }

        var local = _local.AddOrUpdate(key, _ => (bucket, 1), (_, current) => current.Window == bucket ? (bucket, current.Count + 1) : (bucket, 1));
        return (local.Count, retryAfter);
    }

    /// <summary>A flag that expires on its own (a lockout); kept locally too when Redis is down.</summary>
    public async Task SetFlagAsync(string key, TimeSpan lifetime)
    {
        var until = clock.GetUtcNow() + lifetime;
        _flags[key] = until;
        await redis.TryAsync(db => db.StringSetAsync($"flag:{key}", 1, lifetime));
    }

    public async Task<bool> HasFlagAsync(string key)
    {
        if (await redis.TryAsync<bool?>(async db => await db.KeyExistsAsync($"flag:{key}")) is true)
        {
            return true;
        }

        return _flags.TryGetValue(key, out var until) && until > clock.GetUtcNow();
    }

    public async Task ClearAsync(string key, TimeSpan window)
    {
        var bucket = clock.GetUtcNow().ToUnixTimeMilliseconds() / (long)window.TotalMilliseconds;
        _local.TryRemove(key, out _);
        await redis.TryAsync(db => db.KeyDeleteAsync((RedisKey)$"limit:{key}:{bucket}"));
    }

    private readonly ConcurrentDictionary<string, DateTimeOffset> _flags = new(StringComparer.Ordinal);
}
