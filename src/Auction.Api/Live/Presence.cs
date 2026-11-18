using System.Collections.Concurrent;
using StackExchange.Redis;

namespace Auction.Api.Live;

/// <summary>
/// Signed-in users with a live connection, for the admin activity view. Each instance keeps its own users
/// in a shared Redis sorted set, refreshed by a heartbeat, so the count covers every instance; a user with
/// several tabs counts once. Without Redis the count is this instance's alone.
/// </summary>
public sealed class Presence(Caching.RedisJobs redis, TimeProvider clock) : BackgroundService
{
    private const string Key = "presence";
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Expiry = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<Guid, int> _connections = new();

    public async Task<int> OnlineUsersAsync()
    {
        var cutoff = clock.GetUtcNow().Add(-Expiry).ToUnixTimeMilliseconds();
        var shared = await redis.TryAsync<long?>(async db => await db.SortedSetLengthAsync(Key, cutoff, long.MaxValue));
        return shared is { } count ? (int)count : _connections.Count;
    }

    public async Task ConnectedAsync(Guid userId)
    {
        _connections.AddOrUpdate(userId, 1, (_, count) => count + 1);
        await redis.TryAsync(db => db.SortedSetAddAsync(Key, userId.ToString("N"), clock.GetUtcNow().ToUnixTimeMilliseconds()));
    }

    public async Task DisconnectedAsync(Guid userId)
    {
        while (_connections.TryGetValue(userId, out var count))
        {
            if (count > 1 && _connections.TryUpdate(userId, count - 1, count))
            {
                return;
            }

            if (count <= 1 && _connections.TryRemove(new KeyValuePair<Guid, int>(userId, count)))
            {
                await redis.TryAsync(db => db.SortedSetRemoveAsync(Key, userId.ToString("N")));
                return;
            }
        }
    }

    // Refreshes this instance's users and drops anyone whose instance stopped refreshing them.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Heartbeat);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = clock.GetUtcNow();
            var entries = _connections.Keys.Select(id => new SortedSetEntry(id.ToString("N"), now.ToUnixTimeMilliseconds())).ToArray();
            await redis.TryAsync(async db =>
            {
                if (entries.Length > 0)
                {
                    await db.SortedSetAddAsync(Key, entries);
                }

                await db.SortedSetRemoveRangeByScoreAsync(Key, 0, now.Add(-Expiry).ToUnixTimeMilliseconds());
            });
        }
    }
}
