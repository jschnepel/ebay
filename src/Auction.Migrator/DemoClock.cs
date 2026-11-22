using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;

namespace Auction.Migrator;

/// <summary>Where and when the demo-clock resets the demo.</summary>
public sealed record DemoClockOptions(string ConnectionString, string? RedisConnectionString, DemoSeedOptions Seed, TimeOnly AtUtc)
{
    /// <summary>How often it looks for an admin's request and for the scheduled time.</summary>
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// <c>migrator demo-clock</c> (ADM-04): returns the demo to its seeded state every day at a fixed UTC time and
/// whenever an admin's request is waiting in <c>demo_resets</c>. It runs reset-demo as the schema owner, outside
/// the API, then clears Redis, which holds only caches (sessions among them), so nobody stays signed in on a
/// session the reset removed. The live marketplace notices the reset by itself and refills.
/// </summary>
public static partial class DemoClock
{
    private const string PendingSql = "SELECT id FROM auction.demo_resets WHERE completed_at IS NULL ORDER BY requested_at LIMIT 1";
    private const string LastCompletedSql = "SELECT max(completed_at) FROM auction.demo_resets";
    // The requesting visitor's name goes with the reset, like everything else visitors leave behind. The time is
    // the clock's own, so the schedule compares like with like.
    private const string CompleteSql = "UPDATE auction.demo_resets SET completed_at = $2, requested_by = NULL WHERE id = $1";
    private const string ScheduledSql = "INSERT INTO auction.demo_resets (id, requested_at, completed_at) VALUES ($1, $2, $2)";

    /// <summary>
    /// When the next scheduled reset is due: today's time if it hasn't run since then, otherwise tomorrow's. A
    /// time already past counts as missed (and so due now) only once a reset has ever run, so a fresh install
    /// waits for the next scheduled time instead of resetting at once.
    /// </summary>
    public static DateTimeOffset NextScheduled(DateTimeOffset now, TimeOnly atUtc, DateTimeOffset? lastCompleted)
    {
        var utc = now.ToUniversalTime();
        var today = new DateTimeOffset(DateOnly.FromDateTime(utc.UtcDateTime).ToDateTime(atUtc), TimeSpan.Zero);
        if (utc < today)
        {
            return today;
        }

        return lastCompleted is { } last && last < today ? today : today.AddDays(1);
    }

    /// <summary>
    /// The heartbeat the container's health check reads (the chiseled image has no shell): touched on every
    /// check, so a stuck clock turns unhealthy.
    /// </summary>
    public static string HeartbeatPath { get; } = Path.Combine(Path.GetTempPath(), "demo-clock.alive");

    private static readonly TimeSpan HeartbeatMaxAge = TimeSpan.FromMinutes(1);

    public static Task BeatAsync(CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(HeartbeatPath, DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), cancellationToken);

    /// <summary><c>migrator demo-clock-health</c>: 0 when the clock has checked within the last minute.</summary>
    public static int Health() =>
        File.Exists(HeartbeatPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(HeartbeatPath) < HeartbeatMaxAge ? 0 : 1;

    /// <summary>
    /// <c>migrator demo-clock</c>, configured from the environment (DEMO_MODE, DEMO_RESET_AT_UTC, REDIS_CONNECTION
    /// and the seeded accounts' passwords). Outside demo mode it only keeps its heartbeat, so Compose doesn't
    /// restart it in a loop. Exit code 2 when the passwords or the time are missing or malformed.
    /// </summary>
    public static async Task<int> RunCommandAsync(string connectionString, ILogger logger, CancellationToken cancellationToken)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("DEMO_MODE"), "true", StringComparison.OrdinalIgnoreCase))
        {
            MigratorLog.DemoClockIdle(logger);
            while (!cancellationToken.IsCancellationRequested)
            {
                await BeatAsync(CancellationToken.None);
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }

            return 0;
        }

        var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
        var demoPassword = Environment.GetEnvironmentVariable("DEMO_PASSWORD");
        if (string.IsNullOrWhiteSpace(adminPassword) || string.IsNullOrWhiteSpace(demoPassword)
            || !TimeOnly.TryParse(Environment.GetEnvironmentVariable("DEMO_RESET_AT_UTC") ?? "09:00", System.Globalization.CultureInfo.InvariantCulture, out var atUtc))
        {
            MigratorLog.SeedPasswordsMissing(logger);
            return 2;
        }

        var options = new DemoClockOptions(
            connectionString, Environment.GetEnvironmentVariable("REDIS_CONNECTION"),
            new DemoSeedOptions(adminPassword, demoPassword, Environment.GetEnvironmentVariable("SIMULATOR_PASSWORD")), atUtc);
        try
        {
            await RunAsync(options, TimeProvider.System, logger, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        return 0;
    }

    /// <summary>Runs until cancelled, checking every <see cref="DemoClockOptions.PollEvery"/>.</summary>
    public static async Task RunAsync(DemoClockOptions options, TimeProvider time, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var started = time.GetUtcNow();
        LogWaiting(logger, options.AtUtc);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(options, started, time, logger, cancellationToken);
            }
            catch (Exception e) when (e is NpgsqlException or RedisException or InvalidOperationException or TimeoutException)
            {
                LogFailed(logger, e);
            }

            await BeatAsync(cancellationToken);
            await Task.Delay(options.PollEvery, time, cancellationToken);
        }
    }

    /// <summary>
    /// One check: resets when a request is waiting or the scheduled time has come; true when it reset. A clock that
    /// has never reset counts from <paramref name="since"/>, when it started, so the first scheduled time after its
    /// start runs and an earlier one doesn't.
    /// </summary>
    public static async Task<bool> TickAsync(DemoClockOptions options, DateTimeOffset since, TimeProvider time, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        Guid? pending;
        DateTimeOffset? last;
        await using (var connection = new NpgsqlConnection(options.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var pendingQuery = new NpgsqlCommand(PendingSql, connection);
            pending = await pendingQuery.ExecuteScalarAsync(cancellationToken) as Guid?;
            await using var lastQuery = new NpgsqlCommand(LastCompletedSql, connection);
            last = await lastQuery.ExecuteScalarAsync(cancellationToken) is DateTime completed ? new DateTimeOffset(completed, TimeSpan.Zero) : null;
        }

        var now = time.GetUtcNow();
        if (pending is null && NextScheduled(now, options.AtUtc, last ?? since) > now)
        {
            return false;
        }

        await DemoSeed.ResetAsync(options.ConnectionString, options.Seed, cancellationToken);

        // Redis before the record: if the clear fails, the request is still waiting and the next check tries again.
        if (options.RedisConnectionString is { Length: > 0 } redis)
        {
            await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis + ",allowAdmin=true");
            foreach (var endpoint in multiplexer.GetEndPoints())
            {
                await multiplexer.GetServer(endpoint).FlushDatabaseAsync();
            }
        }

        await using (var connection = new NpgsqlConnection(options.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var record = new NpgsqlCommand(pending is not null ? CompleteSql : ScheduledSql, connection);
            record.Parameters.Add(new NpgsqlParameter { Value = pending ?? Guid.CreateVersion7() });
            record.Parameters.Add(new NpgsqlParameter { Value = now });
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        LogReset(logger, pending is null ? "the daily schedule" : "an admin's request");
        return true;
    }

    [LoggerMessage(20, LogLevel.Information, "Demo clock running: daily reset at {AtUtc} UTC, and on an admin's request")]
    private static partial void LogWaiting(ILogger logger, TimeOnly atUtc);

    [LoggerMessage(21, LogLevel.Information, "Demo reset for {Reason}")]
    private static partial void LogReset(ILogger logger, string reason);

    [LoggerMessage(22, LogLevel.Warning, "Demo reset check failed; trying again")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
