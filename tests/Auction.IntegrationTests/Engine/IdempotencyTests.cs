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

    [Fact]
    public async Task A_key_longer_than_255_characters_is_refused()
    {
        var created = await _engine.CreateDraftAsync(
            new CommandContext(UserId.New(), new string('k', 256)), Terms(await db.NowAsync()), _ct);

        Assert.Equal(CommandError.InvalidKey, created.Error);
    }
}
