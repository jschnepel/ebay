using System.Text.Json;
using StackExchange.Redis;

namespace Auction.Api.Caching;

/// <summary>
/// The read cache for published auctions (REL-02), refreshed by the relay and checked by version, so a
/// cached page is never older than what the relay has already announced: the relay raises each auction's
/// known version before its hint goes out, a cached entry below that version is ignored, and a fill only
/// replaces an entry with one at least as new. Drafts are never cached. Any Redis failure is a miss.
/// </summary>
public sealed class AuctionCache(RedisJobs redis)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    // Stores the entry only if its version is at least both the stored entry's and the announced version.
    private static readonly LuaScript FillScript = LuaScript.Prepare("""
        local known = tonumber(redis.call('GET', @versionKey) or '-1')
        local current = redis.call('HGET', @entryKey, 'v')
        if tonumber(@version) < known then return 0 end
        if current and tonumber(current) > tonumber(@version) then return 0 end
        redis.call('HSET', @entryKey, 'v', @version, 'json', @json)
        redis.call('PEXPIRE', @entryKey, @lifetime)
        return 1
        """);

    // Raises the announced version (never lowers it) and drops an older cached entry.
    private static readonly LuaScript AnnounceScript = LuaScript.Prepare("""
        local known = tonumber(redis.call('GET', @versionKey) or '-1')
        if tonumber(@version) > known then redis.call('SET', @versionKey, @version, 'PX', @lifetime) end
        local current = redis.call('HGET', @entryKey, 'v')
        if current and tonumber(current) < tonumber(@version) then redis.call('DEL', @entryKey) end
        return 1
        """);

    /// <summary>The cached auction if it is at least as new as every announced change, otherwise null.</summary>
    public async Task<T?> GetAsync<T>(Guid id)
        where T : class
    {
        var values = await redis.TryAsync(db => db.ScriptEvaluateAsync(
            "local e = redis.call('HMGET', KEYS[1], 'v', 'json') local k = redis.call('GET', KEYS[2]) return {e[1], e[2], k}",
            [EntryKey(id), VersionKey(id)]));
        if (values is not { Resp2Type: ResultType.Array } || ((RedisResult[]?)values) is not [var version, var json, var known])
        {
            return null;
        }

        if (version.IsNull || json.IsNull || (!known.IsNull && (long)version < (long)known))
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>((string?)json ?? "null");
    }

    public Task FillAsync<T>(Guid id, long version, T value) =>
        redis.TryAsync(db => db.ScriptEvaluateAsync(FillScript, new
        {
            entryKey = (RedisKey)EntryKey(id),
            versionKey = (RedisKey)VersionKey(id),
            version,
            json = JsonSerializer.Serialize(value),
            lifetime = (long)Lifetime.TotalMilliseconds,
        }));

    /// <summary>
    /// The directory's first page from the cache, if it was filled since the last change anywhere: every
    /// announced change starts a new generation, and only the current generation's page is read.
    /// </summary>
    public async Task<T?> GetDirectoryAsync<T>(int pageSize)
        where T : class
    {
        var json = await redis.TryAsync(async db =>
        {
            var generation = (long?)await db.StringGetAsync(DirectoryGeneration) ?? 0;
            return await db.StringGetAsync(DirectoryKey(generation, pageSize));
        });
        return json.HasValue ? JsonSerializer.Deserialize<T>((string?)json ?? "null") : null;
    }

    /// <summary>Reads the generation before the page is queried, so a page read across a change is filed under the old one.</summary>
    public async Task<long?> DirectoryGenerationAsync() =>
        await redis.TryAsync(async db => (long?)await db.StringGetAsync(DirectoryGeneration) ?? 0);

    public Task FillDirectoryAsync<T>(long generation, int pageSize, T page) =>
        redis.TryAsync(db => db.StringSetAsync(DirectoryKey(generation, pageSize), JsonSerializer.Serialize(page), TimeSpan.FromSeconds(30)));

    /// <summary>Called by the relay before a change's hint goes out.</summary>
    public Task AnnounceAsync(Guid id, long version) =>
        redis.TryAsync(async db =>
        {
            await db.ScriptEvaluateAsync(AnnounceScript, new
            {
                entryKey = (RedisKey)EntryKey(id),
                versionKey = (RedisKey)VersionKey(id),
                version,
                lifetime = (long)TimeSpan.FromHours(1).TotalMilliseconds,
            });
            await db.StringIncrementAsync(DirectoryGeneration);
        });

    /// <summary>
    /// Whether the viewer has bid on the auction, if it was looked up lately: every signed-in viewer of a busy
    /// auction asks on each refresh. Bids reach the database only through the API, which records each one here as
    /// it is accepted, so a remembered "no" can be wrong only if that record was lost, and then for a minute.
    /// </summary>
    public async Task<bool?> HasBidAsync(Guid auction, Guid bidder)
    {
        var value = await redis.TryAsync(db => db.StringGetAsync(HasBidKey(auction, bidder)));
        return value.HasValue ? value == "1" : null;
    }

    public Task RememberHasBidAsync(Guid auction, Guid bidder, bool hasBid) =>
        redis.TryAsync(db => db.StringSetAsync(HasBidKey(auction, bidder), hasBid ? "1" : "0", hasBid ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1)));

    private static string HasBidKey(Guid auction, Guid bidder) => $"has-bid:{auction:N}:{bidder:N}";

    private const string DirectoryGeneration = "directory-generation";

    private static string DirectoryKey(long generation, int pageSize) => $"directory:{generation}:{pageSize}";

    private static string EntryKey(Guid id) => $"auction:{id:N}";

    private static string VersionKey(Guid id) => $"auction-version:{id:N}";
}
