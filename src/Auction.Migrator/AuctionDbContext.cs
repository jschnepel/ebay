using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Auction.Migrator;

/// <summary>
/// The schema as an EF Core model. It exists to generate and apply migrations; the engine writes with
/// raw Npgsql and never goes through this context.
/// </summary>
public sealed class AuctionDbContext(DbContextOptions<AuctionDbContext> options) : DbContext(options)
{
    public const string Schema = "auction";

    public static AuctionDbContext Create(string connectionString, ILoggerFactory? loggerFactory = null)
    {
        var builder = new DbContextOptionsBuilder<AuctionDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("ef_migrations_history", Schema));
        if (loggerFactory is not null)
        {
            builder.UseLoggerFactory(loggerFactory);
        }

        return new AuctionDbContext(builder.Options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
    }
}
