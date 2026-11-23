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

    [Theory]
    [InlineData("ix_bids_accepted_at", "btree")]
    [InlineData("ix_outbox_events_cancelled_created_at", "btree")]
    [InlineData("ix_auctions_published_at", "btree")]
    [InlineData("ix_auctions_closed_at", "btree")]
    [InlineData("ix_auctions_seller_id_published_at", "btree")]
    [InlineData("ix_admin_audit_at", "btree")]
    public async Task The_event_log_has_a_time_index_on_each_source(string index, string method)
    {
        var actual = await db.ScalarAsync<string>(
            MigratedDatabase.Migrator,
            "SELECT am.amname::text FROM pg_class c JOIN pg_am am ON am.oid = c.relam JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'auction' AND c.relname = $1",
            index);

        Assert.Equal(method, actual);
    }

    [Fact]
    public async Task The_event_logs_newest_bids_are_read_from_the_index_not_the_whole_range()
    {
        // The planner is told not to scan the table, so the plan shows whether an index can serve the order at all:
        // a BRIN index can't, and then every bid in the range is read and sorted for one page.
        await using var connection = await db.OpenAsync(MigratedDatabase.Migrator);
        await using var command = new Npgsql.NpgsqlCommand(
            "SET LOCAL enable_seqscan = off; EXPLAIN SELECT accepted_at FROM bids WHERE accepted_at >= now() - interval '7 days' ORDER BY accepted_at DESC LIMIT 50",
            connection,
            await connection.BeginTransactionAsync(TestContext.Current.CancellationToken));
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var plan = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            plan.Add(reader.GetString(0));
        }

        Assert.Contains(plan, line => line.Contains("Backward using ix_bids_accepted_at", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, line => line.Contains("Sort", StringComparison.Ordinal));
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
