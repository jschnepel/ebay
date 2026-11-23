using Npgsql;

namespace Auction.Api.Accounts;

/// <summary>
/// Writes account activity to <c>auction.account_events</c> for the admin event log: sign-ups, sign-ins, failed
/// sign-ins and why, sign-outs, and sessions ended by a newer sign-in or a disable. The database stamps the time;
/// no password, IP or user agent is passed in. A write never stops because the client hung up, so a request can't
/// keep itself out of the log that way. A write that fails is logged and dropped: the log can't be worth more than
/// being able to sign in, and by then the sign-in's own changes (the new session, the ones it ended) are committed.
/// </summary>
internal sealed partial class AccountEvents(NpgsqlDataSource dataSource, ILogger<AccountEvents> logger)
{
    private const int TimeoutSeconds = 5;

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

    public Task RecordAsync(string type, Guid? userId, string? email, string? detail, Guid? sessionId) =>
        InsertAsync(type, userId, email, detail, [sessionId]);

    /// <summary>One row per session, for a sign-in or disable that ended several.</summary>
    public Task RecordManyAsync(string type, IReadOnlyList<Guid> sessionIds, Guid userId, string? detail) =>
        sessionIds.Count == 0
            ? Task.CompletedTask
            : InsertAsync(type, userId, null, detail, [.. sessionIds.Select(id => (Guid?)id)]);

    private async Task InsertAsync(string type, Guid? userId, string? email, string? detail, Guid?[] sessionIds)
    {
        try
        {
            await WriteAsync(type, userId, email, detail, sessionIds);
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            NotRecorded(logger, e, type);
        }
    }

    [LoggerMessage(1, LogLevel.Error, "An account event ({Type}) couldn't be written to the event log")]
    private static partial void NotRecorded(ILogger logger, Exception exception, string type);

    private async Task WriteAsync(string type, Guid? userId, string? email, string? detail, Guid?[] sessionIds)
    {
        await using var command = dataSource.CreateCommand(InsertSql);
        command.CommandTimeout = TimeoutSeconds;
        command.Parameters.Add(new NpgsqlParameter { Value = type });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)userId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)email ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)detail ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = sessionIds, DataTypeName = "uuid[]" });
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
