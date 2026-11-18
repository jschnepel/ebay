using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Auction.Migrator;

public static class DatabaseMigrator
{
    /// <summary>
    /// Applies any pending migrations. Safe to run on every start: applied migrations are skipped and
    /// existing data is never reset.
    /// </summary>
    public static async Task MigrateAsync(
        string connectionString, ILoggerFactory? loggerFactory, CancellationToken cancellationToken)
    {
        await using var context = AuctionDbContext.Create(connectionString, loggerFactory);
        await context.Database.MigrateAsync(cancellationToken);
    }
}
