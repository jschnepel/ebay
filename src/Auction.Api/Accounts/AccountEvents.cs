using Npgsql;

namespace Auction.Api.Accounts;

/// <summary>
/// Writes account activity to <c>auction.account_events</c> for the admin event log: sign-ups, sign-ins, failed
/// sign-ins and why, sign-outs, and sessions ended by a newer sign-in or a disable. The database stamps the time;
/// no password, IP or user agent is passed in. Callers pass CancellationToken.None: a client that hangs up mid-request
/// mustn't be able to keep its own failed sign-in out of the log.
/// </summary>
internal sealed class AccountEvents(NpgsqlDataSource dataSource)
{
    public const string SignUp = "sign_up";
    public const string SignIn = "sign_in";
    public const string SignInFailed = "sign_in_failed";
    public const string SignOut = "sign_out";
    public const string SessionReplaced = "session_replaced";
    public const string SessionEnded = "session_ended";

    private const string InsertSql = """
        INSERT INTO auction.account_events (type, user_id, email, detail, session_id)
        SELECT $1, $2, $3, $4, unnest($5::uuid[])
        """;

    public Task RecordAsync(string type, Guid? userId, string? email, string? detail, Guid? sessionId, CancellationToken cancellationToken) =>
        InsertAsync(type, userId, email, detail, [sessionId], cancellationToken);

    /// <summary>One row per session, for a sign-in or disable that ended several.</summary>
    public Task RecordManyAsync(string type, IReadOnlyList<Guid> sessionIds, Guid userId, string? detail, CancellationToken cancellationToken) =>
        sessionIds.Count == 0
            ? Task.CompletedTask
            : InsertAsync(type, userId, null, detail, [.. sessionIds.Select(id => (Guid?)id)], cancellationToken);

    private async Task InsertAsync(string type, Guid? userId, string? email, string? detail, Guid?[] sessionIds, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(InsertSql);
        command.Parameters.Add(new NpgsqlParameter { Value = type });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)userId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)email ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)detail ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = sessionIds, DataTypeName = "uuid[]" });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
