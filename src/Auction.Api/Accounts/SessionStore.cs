using Auction.Domain;
using Npgsql;

namespace Auction.Api.Accounts;

/// <summary>
/// Server-side sessions (AUTH-02): one active session per account. Starting a session revokes every other
/// session of that account in the same transaction, so the newest sign-in always wins; a request is
/// accepted only while its session is unrevoked and its account enabled. Every validation reads the committed PostgreSQL state.
/// </summary>
internal sealed class SessionStore(NpgsqlDataSource dataSource)
{
    private const string RevokeAllSql =
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
        // Two simultaneous sign-ins must take turns even if the account has no session yet.
        await using (var accountLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))", connection, transaction))
        {
            accountLock.Parameters.Add(new NpgsqlParameter { Value = user.Value.ToString("N") });
            await accountLock.ExecuteNonQueryAsync(cancellationToken);
        }

        var ended = new List<Guid>();
        await using (var revoke = new NpgsqlCommand(RevokeAllSql, connection, transaction))
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

        return ended;
    }

    public async Task<bool> IsValidAsync(Guid session, UserId user, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ValidSql);
        command.Parameters.Add(new NpgsqlParameter { Value = session });
        command.Parameters.Add(new NpgsqlParameter { Value = user.Value });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task RevokeAsync(Guid session, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(RevokeSql);
        command.Parameters.Add(new NpgsqlParameter { Value = session });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
