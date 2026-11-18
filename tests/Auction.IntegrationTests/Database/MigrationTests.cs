using Auction.Migrator;
using Microsoft.EntityFrameworkCore;

namespace Auction.IntegrationTests.Database;

public sealed class MigrationTests(MigratedDatabase db)
{
    private static readonly string[] Tables =
    [
        "auctions", "proxy_maxima", "bids", "idempotency_records", "outbox_events",
        "notifications", "admin_audit", "user_limits",
    ];

    [Fact]
    public async Task Migrations_create_every_table_in_the_auction_schema()
    {
        foreach (var table in Tables)
        {
            var exists = await db.ScalarAsync<bool>(
                MigratedDatabase.Migrator,
                "SELECT EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'auction' AND tablename = $1)",
                table);

            Assert.True(exists, $"table auction.{table} is missing");
        }
    }

    [Fact]
    public async Task The_migrator_owns_every_table()
    {
        var notOwned = await db.ScalarAsync<long>(
            MigratedDatabase.Migrator,
            "SELECT count(*) FROM pg_tables WHERE schemaname = 'auction' AND tableowner <> 'migrator'");

        Assert.Equal(0, notOwned);
    }

    [Fact]
    public async Task Running_the_migrator_again_keeps_existing_data()
    {
        var id = await db.InsertDraftAsync();

        await DatabaseMigrator.MigrateAsync(
            db.ConnectionStringFor(MigratedDatabase.Migrator), null, TestContext.Current.CancellationToken);

        var count = await db.ScalarAsync<long>(
            MigratedDatabase.Engine, "SELECT count(*) FROM auctions WHERE id = $1", id);
        Assert.Equal(1, count);
    }

    [Fact]
    public void The_model_has_no_changes_missing_from_a_migration()
    {
        using var context = AuctionDbContext.Create(db.ConnectionStringFor(MigratedDatabase.Migrator));

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
