namespace Auction.Api.Images;

/// <summary>
/// Deletes uploaded images nobody attached within an hour (UI-05), so they stop counting toward the quotas.
/// A blob that an attach links at the same moment can't be deleted: its foreign key serializes the two.
/// </summary>
internal sealed partial class ImageSweeper(ImageStore store, ILogger<ImageSweeper> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                while (await store.SweepAsync(stoppingToken) > 0)
                {
                    // A full batch may leave more.
                }
            }
            catch (Npgsql.NpgsqlException e)
            {
                LogSweepFailed(logger, e);
            }
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "The image sweep failed; it tries again at the next interval")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
