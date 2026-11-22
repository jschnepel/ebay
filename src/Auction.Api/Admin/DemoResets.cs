using Auction.Api.Hosting;
using Npgsql;

namespace Auction.Api.Admin;

/// <summary>The demo's resets as the admin area shows them: one waiting, the last done, and the next scheduled.</summary>
public sealed record DemoState(bool ResetPending, DateTimeOffset? LastResetAt, DateTimeOffset NextScheduledAt);

/// <summary>
/// Demo resets (ADM-04). An admin's request is only recorded here; the demo-clock service runs the reset as the
/// schema owner outside the API, as it does every day at <see cref="ApiOptions.DemoResetAtUtc"/>. At most one
/// request waits at a time (a unique index), so a second one is refused until the first is done.
/// </summary>
internal sealed class DemoResets(NpgsqlDataSource database, ApiOptions options, TimeProvider time)
{
    private const string StateSql = """
        SELECT EXISTS (SELECT 1 FROM auction.demo_resets WHERE completed_at IS NULL), max(completed_at)
        FROM auction.demo_resets
        """;

    private const string RequestSql = "INSERT INTO auction.demo_resets (id, requested_at, requested_by) VALUES ($1, now(), $2)";

    public async Task<DemoState> StateAsync(CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(StateSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var pending = reader.GetBoolean(0);
        DateTimeOffset? last = await reader.IsDBNullAsync(1, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(1, cancellationToken);
        return new DemoState(pending, last, NextScheduled(time.GetUtcNow(), options.DemoResetAtUtc, last));
    }

    /// <summary>Records an admin's request; false when another request is already waiting.</summary>
    public async Task<bool> RequestAsync(string requestedBy, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(RequestSql);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter { Value = requestedBy });
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    // The same rule the demo-clock follows: today's time unless it has run since, otherwise tomorrow's.
    internal static DateTimeOffset NextScheduled(DateTimeOffset now, TimeOnly atUtc, DateTimeOffset? lastCompleted)
    {
        var utc = now.ToUniversalTime();
        var today = new DateTimeOffset(DateOnly.FromDateTime(utc.UtcDateTime).ToDateTime(atUtc), TimeSpan.Zero);
        if (utc < today)
        {
            return today;
        }

        return lastCompleted is { } last && last < today ? today : today.AddDays(1);
    }
}
