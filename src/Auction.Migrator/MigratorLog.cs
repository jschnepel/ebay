using Microsoft.Extensions.Logging;

namespace Auction.Migrator;

internal static partial class MigratorLog
{
    [LoggerMessage(1, LogLevel.Error, "MIGRATOR_CONNECTION_STRING is not set")]
    public static partial void ConnectionStringMissing(ILogger logger);

    [LoggerMessage(2, LogLevel.Information, "Database schema is up to date")]
    public static partial void UpToDate(ILogger logger);

    [LoggerMessage(3, LogLevel.Error, "Migration failed")]
    public static partial void Failed(ILogger logger, Exception exception);
}
