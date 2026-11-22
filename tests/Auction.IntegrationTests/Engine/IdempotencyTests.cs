using Auction.Domain;
using Auction.Engine;
using Auction.IntegrationTests.Database;
using static Auction.IntegrationTests.Engine.EngineScenario;

namespace Auction.IntegrationTests.Engine;

/// <summary>
/// A repeated request with the same key returns the recorded outcome, accepted or rejected, even when
/// the rules would now decide differently. Keys are scoped per actor.
/// </summary>
public sealed class IdempotencyTests(MigratedDatabase db)
{
    private readonly AuctionEngine _engine = db.CreateEngine(ShortAuctions);
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_fresh_equal_maximum_is_refused_but_retrying_the_original_key_replays_success()
    {
        var id = await ActiveAsync(db, _engine);
        var firstKey = As(UserId.New());
        var maximum = new Money(1500);
        var first = await _engine.PlaceBidAsync(firstKey, id, maximum, _ct);
        var duplicate = await _engine.PlaceBidAsync(As(firstKey.Actor), id, maximum, _ct);
        var replay = await _engine.PlaceBidAsync(firstKey, id, maximum, _ct);

        Assert.True(first.Succeeded);
        Assert.Equal(CommandError.MaximumUnchanged, duplicate.Error);
        Assert.True(replay.Succeeded && replay.Replayed);
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal(1, await CountAsync(db, "bids", id));
        Assert.Equal(1L, await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT accepted_bid_count FROM auctions WHERE id = $1", id.Value));
    }

    [Fact]
    public async Task Repeating_a_bid_returns_the_recorded_outcome_and_records_one_bid()
    {
        var id = await ActiveAsync(db, _engine);
        var context = As(UserId.New());

        var first = await _engine.PlaceBidAsync(context, id, new Money(1500), _ct);
        var again = await _engine.PlaceBidAsync(context, id, new Money(1500), _ct);

        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.Equivalent(first, again with { Replayed = false }, strict: true);
        Assert.Equal(1, await CountAsync(db, "bids", id));
    }

    [Fact]
    public async Task A_bid_receipt_saved_before_decision_times_replays_its_original_record_time()
    {
        var id = await ActiveAsync(db, _engine);
        var context = As(UserId.New());
        await _engine.PlaceBidAsync(context, id, new Money(1500), _ct);
        var recordedAt = await db.NowAsync();
        await db.ExecuteAsync(MigratedDatabase.Migrator,
            "UPDATE idempotency_records SET outcome = outcome - 'DecidedAt', created_at = $3 WHERE actor_id = $1 AND key = $2",
            context.Actor.Value, context.IdempotencyKey, recordedAt);

        var replay = await _engine.PlaceBidAsync(context, id, new Money(1500), _ct);
        Assert.True(replay.Replayed);
        Assert.Equal(recordedAt, replay.DecidedAt);
        Assert.Equal(1, await CountAsync(db, "bids", id));
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_request_is_refused()
    {
        var id = await ActiveAsync(db, _engine);
        var context = As(UserId.New());
        await _engine.PlaceBidAsync(context, id, new Money(1500), _ct);

        var different = await _engine.PlaceBidAsync(context, id, new Money(1600), _ct);

        Assert.Equal(CommandError.KeyReused, different.Error);
        Assert.Equal(1500, await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT maximum_cents FROM proxy_maxima WHERE auction_id = $1", id.Value));
    }

    [Fact]
    public async Task The_same_key_from_another_user_is_independent()
    {
        var id = await ActiveAsync(db, _engine);
        const string key = "same-key-for-both";

        var a = await _engine.PlaceBidAsync(new CommandContext(UserId.New(), key), id, new Money(1500), _ct);
        var b = await _engine.PlaceBidAsync(new CommandContext(UserId.New(), key), id, new Money(2500), _ct);

        Assert.Equal((CommandError.None, false), (a.Error, a.Replayed));
        Assert.Equal((CommandError.None, false), (b.Error, b.Replayed));
    }

    [Fact]
    public async Task A_rejection_is_recorded_and_replayed_too()
    {
        var id = await ActiveAsync(db, _engine);
        var context = As(UserId.New());

        var first = await _engine.PlaceBidAsync(context, id, new Money(999), _ct);
        var again = await _engine.PlaceBidAsync(context, id, new Money(999), _ct);

        Assert.Equal((CommandError.BelowMinimum, CommandError.BelowMinimum), (first.Error, again.Error));
        Assert.True(again.Replayed);
    }

    [Fact]
    public async Task Two_identical_requests_racing_record_one_bid_and_get_the_same_outcome()
    {
        var id = await ActiveAsync(db, _engine);
        var context = As(UserId.New());

        var outcomes = await Task.WhenAll(
            _engine.PlaceBidAsync(context, id, new Money(1500), _ct),
            _engine.PlaceBidAsync(context, id, new Money(1500), _ct));

        Assert.Equivalent(outcomes[0] with { Replayed = false }, outcomes[1] with { Replayed = false }, strict: true);
        Assert.Single(outcomes, o => o.Replayed);
        Assert.Equal(1, await CountAsync(db, "bids", id));
    }

    [Fact]
    public async Task An_accepted_bid_retried_after_the_deadline_still_returns_its_acceptance()
    {
        var now = await db.NowAsync();
        var id = await ActiveAsync(_engine, UserId.New(), Terms(now, duration: TimeSpan.FromSeconds(3)));
        var context = As(UserId.New());
        var accepted = await _engine.PlaceBidAsync(context, id, new Money(1500), _ct);
        await db.WaitUntilAsync(now.AddSeconds(3.5));

        var retried = await _engine.PlaceBidAsync(context, id, new Money(1500), _ct);
        var fresh = await BidAsync(_engine, UserId.New(), id, 2500);

        Assert.Equal((CommandError.None, CommandError.None, true), (accepted.Error, retried.Error, retried.Replayed));
        Assert.Equal(CommandError.AuctionEnded, fresh.Error);
    }

    [Fact]
    public async Task One_key_raced_across_two_auctions_records_one_bid_and_refuses_the_other()
    {
        for (var round = 0; round < 5; round++)
        {
            var first = await ActiveAsync(db, _engine);
            var second = await ActiveAsync(db, _engine);
            var context = As(UserId.New());

            var outcomes = await Task.WhenAll(
                _engine.PlaceBidAsync(context, first, new Money(1500), _ct),
                _engine.PlaceBidAsync(context, second, new Money(1500), _ct));

            Assert.Single(outcomes, o => o.Succeeded);
            Assert.Single(outcomes, o => o.Error == CommandError.KeyReused);
            Assert.Equal(1, await CountAsync(db, "bids", first) + await CountAsync(db, "bids", second));

            var winner = outcomes[0].Succeeded ? first : second;
            var replay = await _engine.PlaceBidAsync(context, winner, new Money(1500), _ct);
            Assert.Equal((CommandError.None, true), (replay.Error, replay.Replayed));
        }
    }

    [Fact]
    public async Task A_key_used_on_one_auction_is_refused_on_another()
    {
        var first = await ActiveAsync(db, _engine);
        var second = await ActiveAsync(db, _engine);
        var context = As(UserId.New());
        await _engine.PlaceBidAsync(context, first, new Money(1500), _ct);

        var reused = await _engine.PlaceBidAsync(context, second, new Money(1500), _ct);

        Assert.Equal(CommandError.KeyReused, reused.Error);
        Assert.Equal(0, await CountAsync(db, "bids", second));
    }

    [Fact]
    public async Task Records_older_than_the_retention_are_swept_and_their_keys_can_be_used_again()
    {
        var id = await ActiveAsync(db, _engine);
        var stale = As(UserId.New());
        var recent = As(UserId.New());
        await _engine.PlaceBidAsync(stale, id, new Money(1500), _ct);
        await _engine.PlaceBidAsync(recent, id, new Money(1600), _ct);
        await AgeRecordAsync(stale, TimeSpan.FromHours(25));
        await AgeRecordAsync(recent, TimeSpan.FromHours(23));

        var swept = await _engine.SweepExpiredKeysAsync(_ct);
        var reused = await _engine.PlaceBidAsync(stale, id, new Money(2500), _ct);
        var replayed = await _engine.PlaceBidAsync(recent, id, new Money(1600), _ct);

        Assert.True(swept >= 1);
        Assert.Equal((CommandError.None, false), (reused.Error, reused.Replayed));
        Assert.True(replayed.Replayed);
    }

    [Fact]
    public async Task Repeating_a_create_returns_the_same_draft()
    {
        var context = As(UserId.New());
        var terms = Terms(await db.NowAsync());

        var created = await Task.WhenAll(
            _engine.CreateDraftAsync(context, terms, _ct),
            _engine.CreateDraftAsync(context, terms, _ct));
        var again = await _engine.CreateDraftAsync(context, terms, _ct);

        Assert.Equal(created[0].AuctionId, created[1].AuctionId);
        Assert.Equal(created[0].AuctionId, again.AuctionId);
        Assert.Equal(1, await db.ScalarAsync<long>(
            MigratedDatabase.Verifier, "SELECT count(*) FROM auctions WHERE seller_id = $1", context.Actor.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_key_is_refused(string key)
    {
        var id = await ActiveAsync(db, _engine);

        var bid = await _engine.PlaceBidAsync(new CommandContext(UserId.New(), key), id, new Money(1500), _ct);

        Assert.Equal(CommandError.InvalidKey, bid.Error);
    }

    private Task<int> AgeRecordAsync(CommandContext context, TimeSpan age) =>
        db.ExecuteAsync(
            MigratedDatabase.Migrator,
            "UPDATE idempotency_records SET created_at = now() - $3 WHERE actor_id = $1 AND key = $2",
            context.Actor.Value,
            context.IdempotencyKey,
            age);

    [Fact]
    public async Task A_key_longer_than_255_characters_is_refused()
    {
        var created = await _engine.CreateDraftAsync(
            new CommandContext(UserId.New(), new string('k', 256)), Terms(await db.NowAsync()), _ct);

        Assert.Equal(CommandError.InvalidKey, created.Error);
    }
}
