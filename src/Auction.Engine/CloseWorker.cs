using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Auction.Engine;

/// <summary>
/// Drives every time-based transition: Scheduled → Active at the start, and Active or Suspended → Closed
/// at the end. Polls every <see cref="EngineOptions.SweepInterval"/>; safe to run on several instances.
/// </summary>
public sealed partial class CloseWorker(AuctionEngine engine, EngineOptions options, ILogger<CloseWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.CloseWorkerEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.SweepInterval);
        do
        {
            await SweepUntilCaughtUpAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SweepUntilCaughtUpAsync(CancellationToken stoppingToken)
    {
        try
        {
            int advanced;
            do
            {
                advanced = await engine.AdvanceDueAuctionsAsync(stoppingToken);
                if (advanced > 0)
                {
                    LogAdvanced(logger, advanced);
                }
            }
            while (advanced >= options.SweepBatchSize);
        }
        catch (NpgsqlException e)
        {
            // The next tick tries again; a database outage must not stop the worker.
            LogSweepFailed(logger, e);
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Advanced {Count} due auctions")]
    private static partial void LogAdvanced(ILogger logger, int count);

    [LoggerMessage(2, LogLevel.Warning, "Sweep for due auctions failed; retrying on the next tick")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
