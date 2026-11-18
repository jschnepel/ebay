using Auction.Domain;
using Auction.Engine;
using Auction.IntegrationTests.Database;
using Microsoft.Extensions.Logging.Abstractions;
using static Auction.IntegrationTests.Engine.EngineScenario;

namespace Auction.IntegrationTests.Engine;

/// <summary>
/// A database of its own for the close worker: a sweep advances every due auction, so it must not see
/// the short auctions other test classes are still using.
/// </summary>
public sealed class SweepDatabase : IAsyncLifetime
{
    public MigratedDatabase Db { get; } = new();

    public ValueTask InitializeAsync() => Db.InitializeAsync();

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

public sealed class CloseWorkerTests(SweepDatabase sweep) : IClassFixture<SweepDatabase>
{
    private readonly MigratedDatabase _db = sweep.Db;
    private readonly AuctionEngine _engine = sweep.Db.CreateEngine(ShortAuctions);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_auction_with_no_bids_closes_as_no_bids_at_its_end()
    {
        var now = await _db.NowAsync();
        var id = await ActiveAsync(_engine, UserId.New(), Terms(now, duration: TimeSpan.FromSeconds(2)));

        await SweepAsync();
        Assert.Equal("active", await StatusAsync(id));

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await SweepAsync();

        Assert.Equal(("closed", "no_bids"), (await StatusAsync(id), await OutcomeAsync(id)));
        Assert.Equal("auction.closed", await _db.ScalarAsync<string>(
            MigratedDatabase.Verifier, "SELECT type FROM outbox_events WHERE auction_id = $1 ORDER BY id DESC LIMIT 1", id.Value));
    }

    [Theory]
    [InlineData(FloorType.Reserve, 2500L, "sold")]
    [InlineData(FloorType.Reserve, 1500L, "reserve_not_met")]
    [InlineData(FloorType.MinimumBid, 1500L, "sold")]
    [InlineData(FloorType.Absolute, 1500L, "sold")]
    public async Task An_auction_with_bids_closes_with_the_outcome_its_floor_decides(FloorType floor, long bidCents, string outcome)
    {
        var now = await _db.NowAsync();
        var terms = Terms(now, BidEntry.FixedIncrement, TimeSpan.FromSeconds(2)) with
        {
            FloorType = floor,
            Reserve = floor == FloorType.Reserve ? new Money(2000) : null,
        };
        var id = await ActiveAsync(_engine, UserId.New(), terms);
        var bidder = UserId.New();
        Assert.True((await BidAsync(_engine, bidder, id, bidCents)).Succeeded);

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await SweepAsync();

        Assert.Equal(("closed", outcome), (await StatusAsync(id), await OutcomeAsync(id)));
        Assert.Equal((bidder.Value, bidCents), (
            await _db.ScalarAsync<Guid>(MigratedDatabase.Verifier, "SELECT leader_id FROM auctions WHERE id = $1", id.Value),
            await _db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT current_price_cents FROM auctions WHERE id = $1", id.Value)));
    }

    [Fact]
    public async Task A_proxy_auction_sells_at_the_current_price_not_the_leaders_maximum()
    {
        var now = await _db.NowAsync();
        var id = await ActiveAsync(_engine, UserId.New(), Terms(now, duration: TimeSpan.FromSeconds(2)));
        await BidAsync(_engine, UserId.New(), id, 9000);
        await BidAsync(_engine, UserId.New(), id, 2500);

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await SweepAsync();

        Assert.Equal(("sold", 2600L), (
            await OutcomeAsync(id),
            await _db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT current_price_cents FROM auctions WHERE id = $1", id.Value)));
    }

    [Fact]
    public async Task A_scheduled_auction_becomes_active_at_its_start()
    {
        var now = await _db.NowAsync();
        var terms = Terms(now) with { StartsAt = now.AddSeconds(2), EndsAt = now.AddHours(1) };
        var id = await ActiveAsync(_engine, UserId.New(), terms);
        Assert.Equal("scheduled", await StatusAsync(id));

        await SweepAsync();
        Assert.Equal("scheduled", await StatusAsync(id));

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await SweepAsync();

        Assert.Equal("active", await StatusAsync(id));
        Assert.Equal(CommandError.None, (await BidAsync(_engine, UserId.New(), id, 1500)).Error);
    }

    [Fact]
    public async Task A_scheduled_auction_found_past_its_end_closes_as_no_bids()
    {
        var now = await _db.NowAsync();
        var terms = Terms(now) with { StartsAt = now.AddSeconds(1), EndsAt = now.AddSeconds(2) };
        var id = await ActiveAsync(_engine, UserId.New(), terms);

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await SweepAsync();

        Assert.Equal(("closed", "no_bids"), (await StatusAsync(id), await OutcomeAsync(id)));
    }

    [Fact]
    public async Task A_suspended_auction_still_closes_at_its_end_using_its_bids()
    {
        var now = await _db.NowAsync();
        var id = await ActiveAsync(_engine, UserId.New(), Terms(now, duration: TimeSpan.FromSeconds(2)));
        await BidAsync(_engine, UserId.New(), id, 2500);
        await _db.ExecuteAsync(MigratedDatabase.Engine, "UPDATE auctions SET status = 'suspended' WHERE id = $1", id.Value);

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await SweepAsync();

        Assert.Equal(("closed", "sold"), (await StatusAsync(id), await OutcomeAsync(id)));
    }

    [Fact]
    public async Task Sweeping_again_or_from_two_workers_at_once_closes_each_auction_once()
    {
        var now = await _db.NowAsync();
        var ids = new List<AuctionId>();
        for (var i = 0; i < 10; i++)
        {
            ids.Add(await ActiveAsync(_engine, UserId.New(), Terms(now, duration: TimeSpan.FromSeconds(2))));
        }

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await Task.WhenAll(SweepAsync(), SweepAsync());
        await SweepAsync();

        foreach (var id in ids)
        {
            Assert.Equal("closed", await StatusAsync(id));
            Assert.Equal(2, await _db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT version FROM auctions WHERE id = $1", id.Value));
            Assert.Equal(1, await _db.ScalarAsync<long>(
                MigratedDatabase.Verifier, "SELECT count(*) FROM outbox_events WHERE auction_id = $1 AND type = 'auction.closed'", id.Value));
        }
    }

    [Fact]
    public async Task Drafts_cancelled_and_future_auctions_are_left_alone()
    {
        var now = await _db.NowAsync();
        var seller = UserId.New();
        var draft = await DraftAsync(_engine, seller, Terms(now, duration: TimeSpan.FromSeconds(2)));
        var cancelled = await ActiveAsync(_engine, seller, Terms(now, duration: TimeSpan.FromSeconds(2)));
        await _engine.CancelAsync(As(seller), cancelled, _ct);
        var future = await ActiveAsync(_engine, seller, Terms(now));

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await SweepAsync();

        Assert.Equal(("draft", "cancelled", "active"), (await StatusAsync(draft), await StatusAsync(cancelled), await StatusAsync(future)));
    }

    [Fact]
    public async Task The_close_worker_closes_due_auctions_on_its_own()
    {
        var now = await _db.NowAsync();
        var id = await ActiveAsync(_engine, UserId.New(), Terms(now, duration: TimeSpan.FromSeconds(2)));
        using var worker = new CloseWorker(_engine, ShortAuctions with { SweepInterval = TimeSpan.FromMilliseconds(200) }, NullLogger<CloseWorker>.Instance);

        await worker.StartAsync(_ct);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await StatusAsync(id) != "closed" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, _ct);
        }

        await worker.StopAsync(_ct);
        Assert.Equal("closed", await StatusAsync(id));
    }

    [Fact]
    public async Task The_close_worker_deletes_expired_idempotency_records()
    {
        var id = await ActiveAsync(_engine, UserId.New(), Terms(await _db.NowAsync()));
        var context = As(UserId.New());
        await _engine.PlaceBidAsync(context, id, new Money(1500), _ct);
        await _db.ExecuteAsync(
            MigratedDatabase.Migrator,
            "UPDATE idempotency_records SET created_at = now() - interval '25 hours' WHERE actor_id = $1",
            context.Actor.Value);
        using var worker = new CloseWorker(_engine, ShortAuctions with { SweepInterval = TimeSpan.FromMilliseconds(200) }, NullLogger<CloseWorker>.Instance);

        await worker.StartAsync(_ct);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (await RecordsAsync(context) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, _ct);
        }

        await worker.StopAsync(_ct);
        Assert.Equal(0, await RecordsAsync(context));
    }

    private Task<long> RecordsAsync(CommandContext context) =>
        _db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT count(*) FROM idempotency_records WHERE actor_id = $1", context.Actor.Value);

    [Fact]
    public async Task Notices_go_only_to_the_people_each_change_concerns()
    {
        var now = await _db.NowAsync();
        var (seller, first, second, stranger) = (UserId.New(), UserId.New(), UserId.New(), UserId.New());
        var terms = Terms(now, BidEntry.FixedIncrement, TimeSpan.FromSeconds(2)) with { FloorType = FloorType.MinimumBid, Reserve = null };
        var id = await ActiveAsync(_engine, seller, terms);
        Assert.True((await BidAsync(_engine, first, id, terms.OpeningPrice.Cents)).Succeeded);
        Assert.True((await BidAsync(_engine, second, id, terms.OpeningPrice.Cents + terms.Increment.Cents)).Succeeded);

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await SweepAsync();

        Assert.Equal("outbid,lost", await NoticesAsync(first, id));
        Assert.Equal("won", await NoticesAsync(second, id));
        Assert.Equal("ended_sold", await NoticesAsync(seller, id));
        Assert.Null(await NoticesAsync(stranger, id)); // control
    }

    [Fact]
    public async Task A_reserve_not_met_tells_the_seller_and_every_bidder_and_no_bids_tells_the_seller()
    {
        var now = await _db.NowAsync();
        var (seller, bidder) = (UserId.New(), UserId.New());
        var terms = Terms(now, BidEntry.FixedIncrement, TimeSpan.FromSeconds(2)) with { FloorType = FloorType.Reserve, Reserve = new Money(5000) };
        var reserved = await ActiveAsync(_engine, seller, terms);
        var empty = await ActiveAsync(_engine, seller, Terms(now, duration: TimeSpan.FromSeconds(2)));
        Assert.True((await BidAsync(_engine, bidder, reserved, terms.OpeningPrice.Cents)).Succeeded);

        await _db.WaitUntilAsync(now.AddSeconds(2.2));
        await SweepAsync();

        Assert.Equal("reserve_not_met", await NoticesAsync(seller, reserved));
        Assert.Equal("reserve_not_met", await NoticesAsync(bidder, reserved));
        Assert.Equal("ended_no_bids", await NoticesAsync(seller, empty));
    }

    [Fact]
    public async Task An_admin_suspension_tells_the_seller_and_bidders_its_reason_and_nobody_else()
    {
        var now = await _db.NowAsync();
        var (seller, bidder, stranger, admin) = (UserId.New(), UserId.New(), UserId.New(), UserId.New());
        var id = await ActiveAsync(_engine, seller, Terms(now));
        Assert.True((await BidAsync(_engine, bidder, id, 5000)).Succeeded);

        Assert.True((await _engine.SuspendAsync(As(admin), id, "Checking the photos", _ct)).Succeeded);

        foreach (var user in new[] { seller, bidder })
        {
            Assert.Equal("suspended:Checking the photos", await _db.ScalarAsync<string>(
                MigratedDatabase.Migrator,
                "SELECT string_agg(kind || ':' || reason, ',') FROM notifications WHERE user_id = $1 AND auction_id = $2",
                user.Value,
                id.Value));
        }

        Assert.Null(await NoticesAsync(stranger, id)); // control
        Assert.Null(await NoticesAsync(admin, id));
    }

    private Task<string?> NoticesAsync(UserId user, AuctionId auction) =>
        _db.ScalarAsync<string>(
            MigratedDatabase.Migrator,
            "SELECT string_agg(kind, ',' ORDER BY id) FROM notifications WHERE user_id = $1 AND auction_id = $2",
            user.Value,
            auction.Value);

    private Task<int> SweepAsync() => _engine.AdvanceDueAuctionsAsync(_ct);

    private Task<string?> StatusAsync(AuctionId id) =>
        _db.ScalarAsync<string>(MigratedDatabase.Verifier, "SELECT status FROM auctions WHERE id = $1", id.Value);

    private Task<string?> OutcomeAsync(AuctionId id) =>
        _db.ScalarAsync<string>(MigratedDatabase.Verifier, "SELECT outcome FROM auctions WHERE id = $1", id.Value);
}
