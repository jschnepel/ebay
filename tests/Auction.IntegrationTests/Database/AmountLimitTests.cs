namespace Auction.IntegrationTests.Database;

/// <summary>
/// Money columns hold integer cents from 1 to the hard ceiling of 1,000,000,000 ($10,000,000.00).
/// The configured cap can only be lower, so these checks are the last line of defence.
/// </summary>
public sealed class AmountLimitTests(MigratedDatabase db)
{
    private const long Ceiling = 1_000_000_000;

    private static readonly long[] OutOfRangeCents = [0, -1, Ceiling + 1, long.MaxValue];

    private static readonly long[] InRangeCents = [1, Ceiling];

    public static TheoryData<long> OutOfRange => [.. OutOfRangeCents];

    public static TheoryData<long> InRange => [.. InRangeCents];

    [Theory]
    [MemberData(nameof(OutOfRange))]
    public async Task A_bid_amount_out_of_range_is_refused(long cents)
    {
        var auctionId = await db.InsertActiveAuctionAsync();

        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.InsertBidAsync(auctionId, cents));

        Assert.Equal("ck_bids_amount", error.ConstraintName);
    }

    [Theory]
    [MemberData(nameof(InRange))]
    public async Task A_bid_amount_in_range_is_accepted(long cents)
    {
        var auctionId = await db.InsertActiveAuctionAsync();

        var seq = await db.InsertBidAsync(auctionId, cents);

        var stored = await db.ScalarAsync<long>(
            MigratedDatabase.Engine, "SELECT amount_cents FROM bids WHERE auction_id = $1 AND seq = $2", auctionId, seq);
        Assert.Equal(cents, stored);
    }

    [Theory]
    [InlineData("opening_price_cents", "ck_auctions_opening_price")]
    [InlineData("increment_cents", "ck_auctions_increment")]
    [InlineData("reserve_cents", "ck_auctions_reserve")]
    public async Task An_auction_amount_out_of_range_is_refused(string column, string constraint)
    {
        var id = await db.InsertDraftAsync();

        foreach (var cents in OutOfRangeCents)
        {
            var error = await db.RefusedAsync(
                MigratedDatabase.Engine, $"UPDATE auctions SET {column} = $2 WHERE id = $1", id, cents);

            Assert.Equal(constraint, error.ConstraintName);
        }
    }

    [Theory]
    [InlineData("opening_price_cents")]
    [InlineData("increment_cents")]
    [InlineData("reserve_cents")]
    public async Task An_auction_amount_in_range_is_accepted(string column)
    {
        var id = await db.InsertDraftAsync();

        foreach (var cents in InRangeCents)
        {
            var updated = await db.ExecuteAsync(
                MigratedDatabase.Engine, $"UPDATE auctions SET {column} = $2 WHERE id = $1", id, cents);

            Assert.Equal(1, updated);
        }
    }

    [Fact]
    public async Task A_private_maximum_out_of_range_is_refused()
    {
        var auctionId = await db.InsertActiveAuctionAsync();

        var error = await db.RefusedAsync(
            MigratedDatabase.Engine,
            "INSERT INTO proxy_maxima (auction_id, bidder_id, maximum_cents, sequence) VALUES ($1, gen_random_uuid(), $2, 1)",
            auctionId,
            Ceiling + 1);

        Assert.Equal("ck_proxy_maxima_maximum", error.ConstraintName);
    }
}
