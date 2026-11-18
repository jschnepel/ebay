using StackExchange.Redis;

namespace Auction.Api.Caching;

/// <summary>
/// The API's one Redis connection for caches, counters and presence (REL-02). Redis speeds things up but
/// never decides: every caller treats a failure as a miss and goes to PostgreSQL or a local fallback, so
/// bidding and reads keep working without it. Calls give up quickly instead of waiting on a dead server.
/// </summary>
public sealed class RedisJobs(IConfiguration configuration) : IDisposable
{
    private readonly Lazy<ConnectionMultiplexer?> _redis = new(() =>
        configuration.GetConnectionString("Redis") is { Length: > 0 } connection ? ConnectionMultiplexer.Connect(connection) : null);

    /// <summary>The database, or null when Redis isn't configured or isn't connected right now.</summary>
    public IDatabase? Database => _redis.Value is { IsConnected: true } redis ? redis.GetDatabase() : null;

    public bool Configured => _redis.Value is not null;

    /// <summary>Runs a Redis call; any Redis failure is a miss (null), never an error for the caller.</summary>
    public async Task<T?> TryAsync<T>(Func<IDatabase, Task<T>> call)
    {
        if (Database is not { } database)
        {
            return default;
        }

        try
        {
            return await call(database);
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            return default;
        }
    }

    /// <summary>Runs a Redis write; false when Redis couldn't take it.</summary>
    public async Task<bool> TryAsync(Func<IDatabase, Task> call)
    {
        if (Database is not { } database)
        {
            return false;
        }

        try
        {
            await call(database);
            return true;
        }
        catch (Exception e) when (e is RedisException or TimeoutException)
        {
            return false;
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
