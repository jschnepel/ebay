using Auction.Migrator;
using Microsoft.Extensions.Logging;
using Npgsql;

// EF Core logs its probe for the history table as a failed command on an empty database; real failures
// still surface as the exception logged below.
using var loggerFactory = LoggerFactory.Create(logging => logging
    .AddJsonConsole()
    .AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None));
var logger = loggerFactory.CreateLogger("Auction.Migrator");

var connectionString = Environment.GetEnvironmentVariable("MIGRATOR_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    MigratorLog.ConnectionStringMissing(logger);
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    await DatabaseMigrator.MigrateAsync(connectionString, loggerFactory, cancellation.Token);
    MigratorLog.UpToDate(logger);
    return 0;
}
catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or OperationCanceledException)
{
    MigratorLog.Failed(logger, ex);
    return 1;
}
