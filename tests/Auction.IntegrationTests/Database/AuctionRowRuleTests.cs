namespace Auction.IntegrationTests.Database;

/// <summary>
/// The database backs up the domain rules on the auction row: a draft may be incomplete, a published
/// auction must be complete and valid, its terms are frozen, and only a draft can be deleted.
/// </summary>
public sealed class AuctionRowRuleTests(MigratedDatabase db)
{
    private const string Publish =
        "UPDATE auctions SET status = 'scheduled', published_at = now() WHERE id = $1";

    [Fact]
    public async Task An_incomplete_draft_can_be_saved_and_edited()
    {
        var id = await db.InsertDraftAsync();

        var updated = await db.ExecuteAsync(
            MigratedDatabase.Engine, "UPDATE auctions SET title = 'Fi' WHERE id = $1", id);

        Assert.Equal(1, updated);
    }

    [Fact]
    public async Task An_incomplete_draft_cannot_be_published()
    {
        var id = await db.InsertDraftAsync();

        var error = await db.RefusedAsync(MigratedDatabase.Engine, Publish, id);

        Assert.Equal("ck_auctions_published_complete", error.ConstraintName);
    }

    [Fact]
    public async Task A_complete_draft_can_be_published()
    {
        var id = await InsertCompleteDraftAsync(reserveCents: 1000);

        var updated = await db.ExecuteAsync(MigratedDatabase.Engine, Publish, id);

        Assert.Equal(1, updated);
    }

    [Theory]
    [InlineData("reserve", null)]
    [InlineData("reserve", 999L)]
    [InlineData("minimum_bid", 2000L)]
    [InlineData("absolute", 2000L)]
    public async Task A_reserve_that_breaks_the_floor_rule_cannot_be_published(string floorType, long? reserveCents)
    {
        var id = await InsertCompleteDraftAsync(reserveCents);
        await db.ExecuteAsync(
            MigratedDatabase.Engine, "UPDATE auctions SET floor_type = $2 WHERE id = $1", id, floorType);

        var error = await db.RefusedAsync(MigratedDatabase.Engine, Publish, id);

        Assert.Equal("ck_auctions_reserve_rule", error.ConstraintName);
    }

    [Theory]
    [InlineData("minimum_bid")]
    [InlineData("absolute")]
    public async Task A_floor_without_a_reserve_can_be_published(string floorType)
    {
        var id = await InsertCompleteDraftAsync(reserveCents: null);
        await db.ExecuteAsync(
            MigratedDatabase.Engine, "UPDATE auctions SET floor_type = $2 WHERE id = $1", id, floorType);

        var updated = await db.ExecuteAsync(MigratedDatabase.Engine, Publish, id);

        Assert.Equal(1, updated);
    }

    [Fact]
    public async Task The_end_must_come_after_the_start()
    {
        var id = await db.InsertDraftAsync();

        var error = await db.RefusedAsync(
            MigratedDatabase.Engine,
            "UPDATE auctions SET starts_at = now(), ends_at = now() WHERE id = $1",
            id);

        Assert.Equal("ck_auctions_dates", error.ConstraintName);
    }

    [Theory]
    [InlineData("title = 'Another title'")]
    [InlineData("description = 'Changed'")]
    [InlineData("opening_price_cents = 500")]
    [InlineData("increment_cents = 50")]
    [InlineData("reserve_cents = 5000")]
    [InlineData("floor_type = 'absolute', reserve_cents = NULL")]
    [InlineData("bid_entry = 'fixed_increment'")]
    [InlineData("starts_at = starts_at - interval '1 hour'")]
    [InlineData("ends_at = ends_at + interval '1 hour'")]
    [InlineData("seller_id = gen_random_uuid()")]
    [InlineData("status = 'draft', published_at = NULL")]
    public async Task Published_terms_cannot_change(string change)
    {
        var id = await db.InsertActiveAuctionAsync();

        var error = await db.RefusedAsync(MigratedDatabase.Engine, $"UPDATE auctions SET {change} WHERE id = $1", id);

        Assert.Equal("auctions_terms_frozen", error.ConstraintName);
    }

    [Fact]
    public async Task A_published_auction_still_takes_bids_and_closes()
    {
        var id = await db.InsertActiveAuctionAsync();

        var bid = await db.ExecuteAsync(
            MigratedDatabase.Engine,
            """
            UPDATE auctions SET current_price_cents = 1000, leader_id = gen_random_uuid(),
                                accepted_bid_count = 1, version = version + 1
            WHERE id = $1
            """,
            id);
        var close = await db.ExecuteAsync(
            MigratedDatabase.Engine,
            "UPDATE auctions SET status = 'closed', outcome = 'reserve_not_met', closed_at = now() WHERE id = $1",
            id);

        Assert.Equal((1, 1), (bid, close));
    }

    [Fact]
    public async Task A_closed_auction_must_record_its_outcome()
    {
        var id = await db.InsertActiveAuctionAsync();

        var error = await db.RefusedAsync(
            MigratedDatabase.Engine, "UPDATE auctions SET status = 'closed', closed_at = now() WHERE id = $1", id);

        Assert.Equal("ck_auctions_closed_outcome", error.ConstraintName);
    }

    [Fact]
    public async Task The_seller_cannot_lead_their_own_auction()
    {
        var id = await db.InsertActiveAuctionAsync();

        var error = await db.RefusedAsync(
            MigratedDatabase.Engine,
            "UPDATE auctions SET current_price_cents = 1000, leader_id = seller_id, accepted_bid_count = 1 WHERE id = $1",
            id);

        Assert.Equal("ck_auctions_leader_not_seller", error.ConstraintName);
    }

    [Fact]
    public async Task A_leader_comes_with_a_price_and_a_bid_count()
    {
        var id = await db.InsertActiveAuctionAsync();

        var error = await db.RefusedAsync(
            MigratedDatabase.Engine, "UPDATE auctions SET leader_id = gen_random_uuid() WHERE id = $1", id);

        Assert.Equal("ck_auctions_leader", error.ConstraintName);
    }

    [Fact]
    public async Task An_unknown_status_is_refused()
    {
        var id = await db.InsertActiveAuctionAsync();

        var error = await db.RefusedAsync(
            MigratedDatabase.Engine, "UPDATE auctions SET status = 'paused' WHERE id = $1", id);

        Assert.Equal("ck_auctions_status", error.ConstraintName);
    }

    [Fact]
    public async Task A_draft_can_be_deleted()
    {
        var id = await db.InsertDraftAsync();

        var deleted = await db.ExecuteAsync(MigratedDatabase.Engine, "DELETE FROM auctions WHERE id = $1", id);

        Assert.Equal(1, deleted);
    }

    [Fact]
    public async Task A_published_auction_cannot_be_deleted()
    {
        var id = await db.InsertActiveAuctionAsync();

        var error = await db.RefusedAsync(MigratedDatabase.Engine, "DELETE FROM auctions WHERE id = $1", id);

        Assert.Equal("auctions_delete_draft_only", error.ConstraintName);
    }

    private async Task<Guid> InsertCompleteDraftAsync(long? reserveCents)
    {
        var id = Guid.NewGuid();
        await db.ExecuteAsync(
            MigratedDatabase.Engine,
            """
            INSERT INTO auctions (id, seller_id, title, opening_price_cents, reserve_cents, starts_at, ends_at)
            VALUES ($1, gen_random_uuid(), 'Film camera', 1000, $2, now() + interval '1 hour', now() + interval '1 day')
            """,
            id,
            reserveCents);
        return id;
    }
}
