using Auction.Domain;
using Npgsql;

namespace Auction.Api.Accounts;

/// <summary>
/// Server-side sessions (AUTH-02): one active session per account. Starting a session revokes every other
/// session of that account in the same transaction, so the newest sign-in always wins; a request is
/// accepted only while its session is unrevoked and its account enabled. A valid session is cached in
/// Redis for a minute, and every revocation deletes its entry in the same step, so revocation takes effect
/// at once; with Redis down each request reads PostgreSQL.
/// </summary>
internal sealed class SessionStore(NpgsqlDataSource dataSource, Caching.RedisJobs redis)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);

    private const string RevokeAllSql =
        "UPDATE auction.sessions SET revoked_at = now() WHERE user_id = $1 AND revoked_at IS NULL RETURNING id";

    private const string RevokeOthersSql =
        "UPDATE auction.sessions SET revoked_at = now() WHERE user_id = $1 AND revoked_at IS NULL RETURNING id";

    private const string InsertSql = "INSERT INTO auction.sessions (id, user_id, created_at) VALUES ($1, $2, now())";

    private const string ValidSql = """
        SELECT s.revoked_at IS NULL AND u.disabled_at IS NULL
        FROM auction.sessions s JOIN auction.users u ON u.id = s.user_id
        WHERE s.id = $1 AND s.user_id = $2
        """;

    private const string RevokeSql = "UPDATE auction.sessions SET revoked_at = now() WHERE id = $1 AND revoked_at IS NULL";

    /// <summary>Starts a session and returns it with the sessions it ended.</summary>
    public async Task<(Guid Session, IReadOnlyList<Guid> Ended)> StartAsync(UserId user, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var ended = new List<Guid>();
        await using (var revoke = new NpgsqlCommand(RevokeOthersSql, connection, transaction))
        {
            revoke.Parameters.Add(new NpgsqlParameter { Value = user.Value });
            await using var reader = await revoke.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ended.Add(reader.GetGuid(0));
            }
        }

        var session = Guid.CreateVersion7();
        await using (var insert = new NpgsqlCommand(InsertSql, connection, transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = session });
            insert.Parameters.Add(new NpgsqlParameter { Value = user.Value });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        await ForgetAsync(ended);
        return (session, ended);
    }

    /// <summary>Ends every session of the account (it was disabled) and returns them.</summary>
    public async Task<IReadOnlyList<Guid>> RevokeAllAsync(UserId user, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(RevokeAllSql);
        command.Parameters.Add(new NpgsqlParameter { Value = user.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ended = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ended.Add(reader.GetGuid(0));
        }

        await ForgetAsync(ended);
        return ended;
    }

    public async Task<bool> IsValidAsync(Guid session, UserId user, CancellationToken cancellationToken)
    {
        var cached = await redis.TryAsync(db => db.StringGetAsync(Key(session)));
        if (cached.HasValue)
        {
            return cached == user.Value.ToString("N");
        }

        await using var command = dataSource.CreateCommand(ValidSql);
        command.Parameters.Add(new NpgsqlParameter { Value = session });
        command.Parameters.Add(new NpgsqlParameter { Value = user.Value });
        var valid = await command.ExecuteScalarAsync(cancellationToken) is true;
        if (valid)
        {
            await redis.TryAsync(db => db.StringSetAsync(Key(session), user.Value.ToString("N"), CacheLifetime));
        }

        return valid;
    }

    public async Task RevokeAsync(Guid session, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(RevokeSql);
        command.Parameters.Add(new NpgsqlParameter { Value = session });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await ForgetAsync([session]);
    }

    private static string Key(Guid session) => $"session:{session:N}";

    private Task ForgetAsync(List<Guid> sessions) =>
        sessions.Count == 0
            ? Task.CompletedTask
            : redis.TryAsync(db => db.KeyDeleteAsync([.. sessions.Select(s => (StackExchange.Redis.RedisKey)Key(s))]));
}
