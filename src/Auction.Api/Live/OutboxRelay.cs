using Auction.Api.Hosting;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Auction.Api.Live;

/// <summary>One outbox event as the relay publishes it: which auction changed, and its new version.</summary>
public sealed record OutboxEvent(long Id, Guid AuctionId, long Version, string Type);

/// <summary>
/// The single outbox relay (OUT-01). It publishes the engine's committed events as SignalR hints, in
/// commit-safe order, then stamps them published. Only rows written by transactions older than every
/// transaction still running are read (below the snapshot's xmin), ordered by (xid, id), so an event
/// whose transaction commits late is never skipped. A session advisory lock keeps one relay running
/// across API instances; LISTEN wakes it at once, and a short poll covers a missed notification.
/// A hint that fails to send (Redis down) stays unpublished and is retried.
/// </summary>
internal sealed partial class OutboxRelay(
    NpgsqlDataSource dataSource, IHubContext<AuctionHub> hub, ApiOptions options, ILogger<OutboxRelay> logger) : BackgroundService
{
    public const string Channel = "auction_outbox";
    public const int BatchSize = 100;

    // An arbitrary application-wide key for pg_try_advisory_lock; only the relay uses it.
    private const long RelayLockKey = 7_316_891_420_551_002;

    private const string PendingSql = """
        SELECT id, auction_id, auction_version, type
        FROM auction.outbox_events
        WHERE published_at IS NULL AND xid < pg_snapshot_xmin(pg_current_snapshot())
        ORDER BY xid, id
        LIMIT $1
        """;

    private const string StampSql = "UPDATE auction.outbox_events SET published_at = now() WHERE id = ANY($1)";

    /// <summary>The committed, unpublished events in publishing order.</summary>
    public static async Task<IReadOnlyList<OutboxEvent>> ReadPendingAsync(NpgsqlDataSource dataSource, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(PendingSql);
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<OutboxEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(new OutboxEvent(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3)));
        }

        return events;
    }

    /// <summary>Publishes one batch and stamps it; returns how many events it published.</summary>
    public async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        var events = await ReadPendingAsync(dataSource, BatchSize, cancellationToken);
        foreach (var change in events)
        {
            var hint = new AuctionChanged(change.AuctionId, change.Version);
            await hub.Clients.Groups(AuctionHub.GroupFor(change.AuctionId), AuctionHub.DirectoryGroup)
                .SendAsync(AuctionHub.ChangedMethod, hint, cancellationToken);
        }

        if (events.Count > 0)
        {
            await using var stamp = dataSource.CreateCommand(StampSql);
            stamp.Parameters.Add(new NpgsqlParameter { Value = events.Select(e => e.Id).ToArray() });
            await stamp.ExecuteNonQueryAsync(cancellationToken);
        }

        return events.Count;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayWhileLeaderAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogRelayFailed(logger, e);
            }

            await Task.Delay(options.RelayRetryDelay, stoppingToken);
        }
    }

    private async Task RelayWhileLeaderAsync(CancellationToken stoppingToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
        await using (var tryLock = new NpgsqlCommand($"SELECT pg_try_advisory_lock({RelayLockKey})", connection))
        {
            if (await tryLock.ExecuteScalarAsync(stoppingToken) is not true)
            {
                return;
            }
        }

        await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
        {
            await listen.ExecuteNonQueryAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            while (await PublishBatchAsync(stoppingToken) == BatchSize)
            {
                // A full batch: more may be waiting.
            }

            await connection.WaitAsync(options.RelayPollInterval, stoppingToken);
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "The outbox relay failed; it retries, and unpublished events stay queued")]
    private static partial void LogRelayFailed(ILogger logger, Exception exception);
}
