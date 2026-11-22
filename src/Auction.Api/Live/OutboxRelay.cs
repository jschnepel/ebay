using Auction.Api.Hosting;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Auction.Api.Live;

/// <summary>
/// One outbox event as the relay publishes it: which auction changed, its new version, and how long ago its
/// transaction wrote it, by the database clock.
/// </summary>
public sealed record OutboxEvent(long Id, Guid AuctionId, long Version, string Type, TimeSpan Age);

/// <summary>
/// The single outbox relay (OUT-01). It publishes the engine's committed events as SignalR hints, in
/// commit-safe order, then stamps them published. Only rows written by transactions older than every
/// transaction still running are read (below the snapshot's xmin), ordered by (xid, id), so an event
/// whose transaction commits late is never skipped. A session advisory lock keeps one relay running
/// across API instances; LISTEN wakes it at once, and a short poll covers a missed notification.
/// A hint that fails to send (Redis down) stays unpublished and is retried.
/// </summary>
internal sealed partial class OutboxRelay(
    NpgsqlDataSource dataSource, IHubContext<AuctionHub> hub, Notifications.NotificationStore notifications, Caching.AuctionCache cache,
    ApiOptions options, Admin.AdminKpis kpis, ILogger<OutboxRelay> logger) : BackgroundService
{
    public const string Channel = "auction_outbox";
    public const int BatchSize = 100;

    // An arbitrary application-wide key for pg_try_advisory_lock; only the relay uses it.
    private const long RelayLockKey = 7_316_891_420_551_002;

    private const string PendingSql = """
        SELECT id, auction_id, auction_version, type, clock_timestamp() - created_at
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
            events.Add(new OutboxEvent(reader.GetInt64(0), reader.GetGuid(1), reader.GetInt64(2), reader.GetString(3), reader.GetTimeSpan(4)));
        }

        return events;
    }

    /// <summary>Publishes one batch and stamps it; returns how many events it published.</summary>
    public async Task<int> PublishBatchAsync(CancellationToken cancellationToken)
    {
        var events = await ReadPendingAsync(dataSource, BatchSize, cancellationToken);
        // A span per batch that published something (OBS-01); an empty poll every second would only be noise.
        using var span = events.Count > 0 ? Hosting.Telemetry.Source.StartActivity("outbox.publish") : null;
        span?.SetTag("outbox.events", events.Count).SetTag("auction.ids", string.Join(',', events.Select(e => e.AuctionId).Distinct()));
        var read = TimeProvider.System.GetTimestamp();
        foreach (var change in events)
        {
            // The cache learns of the new version before any client hears of it, so a re-fetch can't get an older one.
            await cache.AnnounceAsync(change.AuctionId, change.Version);
            var hint = new AuctionChanged(change.AuctionId, change.Version);
            await hub.Clients.Groups(AuctionHub.GroupFor(change.AuctionId), AuctionHub.DirectoryGroup)
                .SendAsync(AuctionHub.ChangedMethod, hint, cancellationToken);
            // Publish lag: the event's age when read, plus the time until its hint went out.
            kpis.RecordPublish(change.Age + TimeProvider.System.GetElapsedTime(read));
        }

        if (events.Count > 0)
        {
            // Each user notified by these changes is told to refresh their bell.
            var recipients = await notifications.RecipientsAsync([.. events.Select(e => (e.AuctionId, e.Version))], cancellationToken);
            if (recipients.Count > 0)
            {
                await hub.Clients.Groups([.. recipients.Select(AuctionHub.UserGroup)]).SendAsync(AuctionHub.NotifiedMethod, cancellationToken);
            }

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
