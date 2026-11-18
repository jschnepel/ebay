namespace Auction.IntegrationTests.Database;

/// <summary>
/// Accepted bids are append-only. The trigger holds even for the table owner, so nothing short of
/// TRUNCATE (used only by the demo reset) can rewrite history.
/// </summary>
public sealed class BidHistoryTests(MigratedDatabase db)
{
    [Fact]
    public async Task An_accepted_bid_is_recorded_and_readable()
    {
        var auctionId = await db.InsertActiveAuctionAsync();

        var seq = await db.InsertBidAsync(auctionId, 1500);

        var amount = await db.ScalarAsync<long>(
            MigratedDatabase.Api, "SELECT amount_cents FROM bids WHERE auction_id = $1 AND seq = $2", auctionId, seq);
        Assert.Equal(1500, amount);
    }

    [Fact]
    public async Task A_step_with_no_public_amount_is_recorded()
    {
        var auctionId = await db.InsertActiveAuctionAsync();

        var seq = await db.InsertBidAsync(auctionId, amountCents: null);

        Assert.Equal(1, seq);
    }

    [Fact]
    public async Task A_bid_cannot_be_updated_even_by_the_owner()
    {
        var auctionId = await db.InsertActiveAuctionAsync();
        await db.InsertBidAsync(auctionId, 1500);

        var error = await db.RefusedAsync(
            MigratedDatabase.Migrator, "UPDATE bids SET amount_cents = 9999 WHERE auction_id = $1", auctionId);

        Assert.Equal("bids_append_only", error.ConstraintName);
    }

    [Fact]
    public async Task A_bid_cannot_be_deleted_even_by_the_owner()
    {
        var auctionId = await db.InsertActiveAuctionAsync();
        await db.InsertBidAsync(auctionId, 1500);

        var error = await db.RefusedAsync(
            MigratedDatabase.Migrator, "DELETE FROM bids WHERE auction_id = $1", auctionId);

        Assert.Equal("bids_append_only", error.ConstraintName);
    }

    [Fact]
    public async Task Sequence_numbers_are_unique_per_auction()
    {
        var auctionId = await db.InsertActiveAuctionAsync();
        await db.InsertBidAsync(auctionId, 1500);

        var error = await db.RefusedAsync(
            MigratedDatabase.Engine,
            """
            INSERT INTO bids (auction_id, seq, bidder_id, amount_cents, source, accepted_at, request_id)
            VALUES ($1, 1, gen_random_uuid(), 1600, 'bidder', now(), gen_random_uuid())
            """,
            auctionId);

        Assert.Equal("pk_bids", error.ConstraintName);
    }

    [Fact]
    public async Task A_bid_must_belong_to_an_existing_auction()
    {
        var error = await db.RefusedAsync(
            MigratedDatabase.Engine,
            """
            INSERT INTO bids (auction_id, seq, bidder_id, amount_cents, source, accepted_at, request_id)
            VALUES (gen_random_uuid(), 1, gen_random_uuid(), 1600, 'bidder', now(), gen_random_uuid())
            """);

        Assert.Equal("fk_bids_auctions_auction_id", error.ConstraintName);
    }
}
