using Auction.Domain;
using Auction.Engine;
using Auction.IntegrationTests.Database;
using static Auction.IntegrationTests.Engine.EngineScenario;

namespace Auction.IntegrationTests.Engine;

public sealed class AuctionCommandTests(MigratedDatabase db)
{
    private readonly AuctionEngine _engine = db.CreateEngine();
    private readonly UserId _seller = UserId.New();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creating_a_draft_stores_it_privately_with_no_event()
    {
        var created = await _engine.CreateDraftAsync(As(_seller), Terms(await db.NowAsync()), _ct);

        Assert.Equal(CommandError.None, created.Error);
        Assert.Equal(0, created.Version);
        var id = created.AuctionId!.Value;
        Assert.Equal("draft", await db.ScalarAsync<string>(MigratedDatabase.Verifier, "SELECT status FROM auctions WHERE id = $1", id.Value));
        Assert.Equal(_seller.Value, await db.ScalarAsync<Guid>(MigratedDatabase.Verifier, "SELECT seller_id FROM auctions WHERE id = $1", id.Value));
        Assert.Equal(0, await CountAsync(db, "outbox_events", id));
    }

    [Fact]
    public async Task A_draft_with_amounts_out_of_range_is_refused_with_field_errors()
    {
        var terms = Terms(await db.NowAsync()) with { OpeningPrice = new Money(0), Increment = new Money(1_000_000_001) };

        var created = await _engine.CreateDraftAsync(As(_seller), terms, _ct);

        Assert.Equal(CommandError.ValidationFailed, created.Error);
        Assert.Contains(new FieldError("openingPrice", "out_of_range"), created.FieldErrors);
        Assert.Contains(new FieldError("increment", "out_of_range"), created.FieldErrors);
        Assert.Null(created.AuctionId);
    }

    [Fact]
    public async Task The_seller_can_edit_their_draft()
    {
        var id = await DraftAsync(_engine, _seller, Terms(await db.NowAsync()));
        var terms = Terms(await db.NowAsync()) with { Title = "Rangefinder camera" };

        var edited = await _engine.EditDraftAsync(As(_seller), id, terms, _ct);

        Assert.Equal((CommandError.None, 1L), (edited.Error, edited.Version));
        Assert.Equal("Rangefinder camera", await db.ScalarAsync<string>(MigratedDatabase.Verifier, "SELECT title FROM auctions WHERE id = $1", id.Value));
    }

    [Fact]
    public async Task Another_users_draft_looks_like_an_unknown_auction()
    {
        var id = await DraftAsync(_engine, _seller, Terms(await db.NowAsync()));
        var other = UserId.New();

        var edited = await _engine.EditDraftAsync(As(other), id, Terms(await db.NowAsync()), _ct);
        var published = await _engine.PublishAsync(As(other), id, _ct);
        var cancelled = await _engine.CancelAsync(As(other), id, _ct);
        var bid = await BidAsync(_engine, other, id, 1500);
        var unknown = await _engine.PublishAsync(As(other), AuctionId.New(), _ct);

        Assert.Equal(CommandError.NotFound, unknown.Error);
        Assert.All([edited, published, cancelled, bid], o => Assert.Equivalent(unknown with { AuctionId = id }, o, strict: true));
        Assert.Equal(0, await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT version FROM auctions WHERE id = $1", id.Value));
    }

    [Fact]
    public async Task Another_user_cannot_cancel_a_published_auction()
    {
        var id = await ActiveAsync(_engine, _seller, Terms(await db.NowAsync()));

        var cancelled = await _engine.CancelAsync(As(UserId.New()), id, _ct);

        Assert.Equal(CommandError.NotOwner, cancelled.Error);
    }

    [Fact]
    public async Task Events_carry_only_the_auction_and_its_version()
    {
        var id = await ActiveAsync(_engine, _seller, Terms(await db.NowAsync()));
        await BidAsync(_engine, UserId.New(), id, 1500);

        var payload = await db.ScalarAsync<string>(
            MigratedDatabase.Verifier, "SELECT payload::text FROM outbox_events WHERE auction_id = $1 AND auction_version = 2", id.Value);

        Assert.Equal("{\"version\": 2, \"auctionId\": \"" + id.Value + "\"}", payload);
    }

    [Fact]
    public async Task Publishing_with_a_past_start_starts_the_auction_now()
    {
        var id = await DraftAsync(_engine, _seller, Terms(await db.NowAsync()));

        var published = await _engine.PublishAsync(As(_seller), id, _ct);

        Assert.Equal((CommandError.None, 1L), (published.Error, published.Version));
        Assert.Equal("active", await db.ScalarAsync<string>(MigratedDatabase.Verifier, "SELECT status FROM auctions WHERE id = $1", id.Value));
        var startedSecondsAgo = await db.ScalarAsync<double>(
            MigratedDatabase.Verifier,
            "SELECT extract(epoch FROM clock_timestamp() - starts_at)::float8 FROM auctions WHERE id = $1 AND published_at IS NOT NULL",
            id.Value);
        Assert.InRange(startedSecondsAgo, 0, 30);
        Assert.Equal("auction.published", await db.ScalarAsync<string>(
            MigratedDatabase.Verifier, "SELECT type FROM outbox_events WHERE auction_id = $1 AND auction_version = 1", id.Value));
    }

    [Fact]
    public async Task An_invalid_listing_cannot_be_published()
    {
        var id = await DraftAsync(_engine, _seller, Terms(await db.NowAsync()) with { Title = "ab" });

        var published = await _engine.PublishAsync(As(_seller), id, _ct);

        Assert.Equal(CommandError.ValidationFailed, published.Error);
        Assert.Contains(new FieldError("title", "too_short"), published.FieldErrors);
        Assert.Equal("draft", await db.ScalarAsync<string>(MigratedDatabase.Verifier, "SELECT status FROM auctions WHERE id = $1", id.Value));
    }

    [Fact]
    public async Task A_published_auction_can_no_longer_be_edited()
    {
        var id = await ActiveAsync(_engine, _seller, Terms(await db.NowAsync()));

        var edited = await _engine.EditDraftAsync(As(_seller), id, Terms(await db.NowAsync()), _ct);

        Assert.Equal(CommandError.InvalidState, edited.Error);
    }

    [Fact]
    public async Task The_seller_can_cancel_before_the_first_bid()
    {
        var id = await ActiveAsync(_engine, _seller, Terms(await db.NowAsync()));

        var cancelled = await _engine.CancelAsync(As(_seller), id, _ct);

        Assert.Equal((CommandError.None, 2L), (cancelled.Error, cancelled.Version));
        Assert.Equal("cancelled", await db.ScalarAsync<string>(MigratedDatabase.Verifier, "SELECT status FROM auctions WHERE id = $1", id.Value));
        Assert.Equal(2, await CountAsync(db, "outbox_events", id));
    }

    [Fact]
    public async Task The_seller_cannot_cancel_after_a_bid()
    {
        var id = await ActiveAsync(_engine, _seller, Terms(await db.NowAsync()));
        await BidAsync(_engine, UserId.New(), id, 1500);

        var cancelled = await _engine.CancelAsync(As(_seller), id, _ct);

        Assert.Equal(CommandError.HasBids, cancelled.Error);
    }

    [Fact]
    public async Task A_first_proxy_bid_leads_at_the_opening_price_and_keeps_its_maximum_private()
    {
        var id = await ActiveAsync(_engine, _seller, Terms(await db.NowAsync()));
        var bidder = UserId.New();

        var bid = await BidAsync(_engine, bidder, id, 1500);

        Assert.Equal((CommandError.None, 1L, 2L), (bid.Error, bid.BidSequence, bid.Version));
        Assert.Equal((bidder, new Money(1000)), (bid.LeaderId!.Value, bid.CurrentPrice!.Value));
        Assert.Equal(1000, await db.ScalarAsync<long>(MigratedDatabase.Api, "SELECT amount_cents FROM bids WHERE auction_id = $1 AND seq = 1", id.Value));
        Assert.Equal(1500, await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT maximum_cents FROM proxy_maxima WHERE auction_id = $1", id.Value));
        Assert.Equal("auction.bid_accepted", await db.ScalarAsync<string>(
            MigratedDatabase.Verifier, "SELECT type FROM outbox_events WHERE auction_id = $1 AND auction_version = 2", id.Value));
    }

    [Fact]
    public async Task A_bid_outbid_at_once_is_shown_without_its_amount_followed_by_the_automatic_answer()
    {
        var id = await ActiveAsync(_engine, _seller, Terms(await db.NowAsync()));
        var (a, b) = (UserId.New(), UserId.New());
        await BidAsync(_engine, a, id, 5000);

        var outbid = await BidAsync(_engine, b, id, 3000);

        Assert.Equal((CommandError.None, a, new Money(3100)), (outbid.Error, outbid.LeaderId!.Value, outbid.CurrentPrice!.Value));
        Assert.Equal(b.Value, await db.ScalarAsync<Guid>(MigratedDatabase.Api, "SELECT bidder_id FROM bids WHERE auction_id = $1 AND seq = 2 AND amount_cents IS NULL", id.Value));
        Assert.Equal(3100, await db.ScalarAsync<long>(MigratedDatabase.Api, "SELECT amount_cents FROM bids WHERE auction_id = $1 AND seq = 3 AND source = 'proxy'", id.Value));
    }

    [Fact]
    public async Task A_refused_bid_changes_nothing()
    {
        var id = await ActiveAsync(_engine, _seller, Terms(await db.NowAsync()));

        var selfBid = await BidAsync(_engine, _seller, id, 1500);
        var tooLow = await BidAsync(_engine, UserId.New(), id, 999);

        Assert.Equal((CommandError.SelfBid, CommandError.BelowMinimum), (selfBid.Error, tooLow.Error));
        Assert.Equal(0, await CountAsync(db, "bids", id));
        Assert.Equal(1, await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT version FROM auctions WHERE id = $1", id.Value));
    }

    [Fact]
    public async Task Bidding_on_an_unknown_auction_or_on_ones_own_draft_is_refused()
    {
        var draft = await DraftAsync(_engine, _seller, Terms(await db.NowAsync()));

        var unknown = await BidAsync(_engine, UserId.New(), AuctionId.New(), 1500);
        var onOwnDraft = await BidAsync(_engine, _seller, draft, 1500);

        Assert.Equal((CommandError.NotFound, CommandError.AuctionNotActive), (unknown.Error, onOwnDraft.Error));
        Assert.Equal(0, await CountAsync(db, "bids", draft));
    }
}
