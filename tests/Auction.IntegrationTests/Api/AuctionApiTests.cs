using System.Net;
using System.Text.Json;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

public sealed class AuctionApiTests(MigratedDatabase db)
{
    [Fact]
    public async Task A_seller_lists_an_item_a_buyer_bids_and_anyone_reads_the_new_price()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync("Seller");
        var buyer = await api.SignUpAsync("Buyer");
        var id = await seller.PublishedAsync(ApiCalls.Terms(reserveCents: 1500));

        using var bid = await buyer.Http.BidAsync(id, 2500);
        Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        var accepted = await bid.JsonAsync();
        Assert.True(accepted.GetProperty("youLead").GetBoolean());
        Assert.Equal(1, accepted.GetProperty("bidSequence").GetInt64());

        var anonymous = api.Anonymous();
        var detail = await anonymous.GetJsonAsync($"/api/v1/auctions/{id}");
        Assert.Equal(("active", 1500L, 1L, true), (
            detail.GetProperty("status").GetString(),
            detail.GetProperty("currentPriceCents").GetInt64(),
            detail.GetProperty("bidCount").GetInt64(),
            detail.GetProperty("reserveMet").GetBoolean()));

        var history = (await anonymous.GetJsonAsync($"/api/v1/auctions/{id}/bids")).GetProperty("items");
        Assert.Equal("Bidder 1", Assert.Single(history.EnumerateArray()).GetProperty("bidder").GetString());
        var own = (await buyer.Http.GetJsonAsync($"/api/v1/auctions/{id}/bids")).GetProperty("items");
        Assert.Equal("You", own[0].GetProperty("bidder").GetString());
    }

    [Fact]
    public async Task A_bid_carrying_a_forged_bidder_id_is_recorded_for_the_signed_in_user()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var victim = await api.SignUpAsync();
        var id = await seller.PublishedAsync();

        using var bid = await buyer.Http.PostCommandAsync(
            $"/api/v1/auctions/{id}/bids", new { amountCents = 1500, bidderId = victim.Id, actorId = victim.Id });

        Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        Assert.Equal(buyer.Id, await db.ScalarAsync<Guid>(MigratedDatabase.Verifier, "SELECT bidder_id FROM bids WHERE auction_id = $1", id));
    }

    [Fact]
    public async Task Another_users_draft_is_not_found_for_every_action()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var other = await api.SignUpAsync();
        var draft = await seller.Http.CreateDraftAsync();

        Assert.Equal(HttpStatusCode.OK, await seller.Http.StatusOfGetAsync($"/api/v1/auctions/{draft}"));
        Assert.Equal(HttpStatusCode.NotFound, await api.Anonymous().StatusOfGetAsync($"/api/v1/auctions/{draft}"));
        Assert.Equal(HttpStatusCode.NotFound, await other.Http.StatusOfGetAsync($"/api/v1/auctions/{draft}"));
        Assert.Equal(HttpStatusCode.NotFound, await other.Http.StatusOfGetAsync($"/api/v1/auctions/{draft}/bids"));
        foreach (var response in new[]
        {
            await other.Http.CommandAsync(HttpMethod.Put, $"/api/v1/auctions/{draft}", ApiCalls.Terms()),
            await other.Http.PostCommandAsync($"/api/v1/auctions/{draft}/publish"),
            await other.Http.PostCommandAsync($"/api/v1/auctions/{draft}/cancel"),
            await other.Http.BidAsync(draft, 1500),
        })
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        Assert.Equal("draft", (await seller.Http.GetJsonAsync($"/api/v1/auctions/{draft}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Another_users_published_auction_can_be_viewed_but_not_edited_published_or_cancelled()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var other = await api.SignUpAsync();
        var id = await seller.PublishedAsync();

        Assert.Equal(HttpStatusCode.OK, await other.Http.StatusOfGetAsync($"/api/v1/auctions/{id}"));
        foreach (var response in new[]
        {
            await other.Http.CommandAsync(HttpMethod.Put, $"/api/v1/auctions/{id}", ApiCalls.Terms()),
            await other.Http.PostCommandAsync($"/api/v1/auctions/{id}/publish"),
            await other.Http.PostCommandAsync($"/api/v1/auctions/{id}/cancel"),
        })
        {
            await response.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        }
    }

    [Fact]
    public async Task A_seller_cannot_bid_on_their_own_auction_and_nobody_can_bid_before_it_starts()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var active = await seller.PublishedAsync();
        var scheduled = await seller.PublishedAsync(ApiCalls.Terms(startIn: TimeSpan.FromHours(1)));

        using var selfBid = await seller.Http.BidAsync(active, 1500);
        using var early = await buyer.Http.BidAsync(scheduled, 1500);
        using var control = await buyer.Http.BidAsync(active, 1500);

        await selfBid.AssertProblemAsync(HttpStatusCode.Conflict, "self_bid");
        await early.AssertProblemAsync(HttpStatusCode.Conflict, "not_active");
        Assert.Equal(HttpStatusCode.Created, control.StatusCode);
    }

    [Fact]
    public async Task A_bid_below_the_minimum_is_refused_with_422()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var id = await seller.PublishedAsync();

        using var low = await (await api.SignUpAsync()).Http.BidAsync(id, 999);

        await low.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "below_minimum");
    }

    [Fact]
    public async Task Only_the_seller_sees_the_reserve_amount_everyone_sees_whether_it_is_met()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(reserveCents: 4321));

        var own = await seller.Http.GetJsonAsync($"/api/v1/auctions/{id}");
        Assert.Equal(4321, own.GetProperty("reserveCents").GetInt64());
        foreach (var client in new[] { buyer.Http, api.Anonymous() })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/auctions/{id}", UriKind.Relative), TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain("reserveCents", body, StringComparison.Ordinal);
            Assert.False(JsonDocument.Parse(body).RootElement.GetProperty("reserveMet").GetBoolean());
        }
    }

    [Fact]
    public async Task Commands_need_an_Idempotency_Key_and_a_repeated_key_replays_or_refuses_a_different_payload()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync();
        var key = Guid.NewGuid().ToString();

        using var missing = await buyer.Http.BidAsync(id, 1500, key: "");
        using var first = await buyer.Http.BidAsync(id, 1500, key);
        using var repeat = await buyer.Http.BidAsync(id, 1500, key);
        using var changed = await buyer.Http.BidAsync(id, 1600, key);

        await missing.AssertProblemAsync(HttpStatusCode.BadRequest, "key_required");
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (first.StatusCode, repeat.StatusCode));
        var replay = await repeat.JsonAsync();
        Assert.True(replay.GetProperty("replayed").GetBoolean());
        var original = await first.JsonAsync();
        Assert.Equal(original.GetProperty("bidSequence").GetInt64(), replay.GetProperty("bidSequence").GetInt64());
        var decidedAt = original.GetProperty("decidedAt").GetDateTimeOffset();
        Assert.Equal(decidedAt, replay.GetProperty("decidedAt").GetDateTimeOffset());
        var history = await buyer.Http.GetJsonAsync($"/api/v1/auctions/{id}/bids");
        Assert.Equal(decidedAt, history.GetProperty("items")[0].GetProperty("acceptedAt").GetDateTimeOffset());
        await changed.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "key_reused");
        Assert.Equal(1, await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT count(*) FROM bids WHERE auction_id = $1", id));
    }

    [Fact]
    public async Task A_replayed_bid_refusal_keeps_its_reason_and_decision_time()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync();
        var key = Guid.NewGuid().ToString();
        using var first = await buyer.Http.BidAsync(id, 1, key);
        using var replay = await buyer.Http.BidAsync(id, 1, key);
        await first.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "below_minimum");
        await replay.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "below_minimum");
        Assert.Equal((await first.JsonAsync()).GetProperty("decidedAt").GetDateTimeOffset(),
            (await replay.JsonAsync()).GetProperty("decidedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_draft_missing_a_term_is_refused_and_an_invalid_listing_cannot_be_published()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();

        using var missing = await seller.Http.PostCommandAsync("/api/v1/auctions", new { title = "No price or dates" });
        await missing.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "validation_failed");
        var errors = (await missing.JsonAsync()).GetProperty("errors");
        Assert.Equal("required", errors.GetProperty("openingPriceCents")[0].GetString());
        Assert.Equal("required", errors.GetProperty("startsAt")[0].GetString());

        var draft = await seller.Http.CreateDraftAsync(ApiCalls.Terms(title: "ab"));
        using var publish = await seller.Http.PostCommandAsync($"/api/v1/auctions/{draft}/publish");
        await publish.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "validation_failed");
        Assert.Equal("too_short", (await publish.JsonAsync()).GetProperty("errors").GetProperty("title")[0].GetString());
    }

    [Fact]
    public async Task The_seller_edits_a_draft_and_cancels_before_the_first_bid_but_not_after()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var draft = await seller.Http.CreateDraftAsync();

        using var edited = await seller.Http.CommandAsync(HttpMethod.Put, $"/api/v1/auctions/{draft}", ApiCalls.Terms(title: "Rangefinder"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal("Rangefinder", (await seller.Http.GetJsonAsync($"/api/v1/auctions/{draft}")).GetProperty("title").GetString());

        var quiet = await seller.PublishedAsync();
        var bidOn = await seller.PublishedAsync();
        using var bid = await (await api.SignUpAsync()).Http.BidAsync(bidOn, 1500);

        using var cancelled = await seller.Http.PostCommandAsync($"/api/v1/auctions/{quiet}/cancel");
        using var refused = await seller.Http.PostCommandAsync($"/api/v1/auctions/{bidOn}/cancel");
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        await refused.AssertProblemAsync(HttpStatusCode.Conflict, "has_bids");
    }

    [Fact]
    public async Task An_admin_account_cannot_sell_or_bid_and_still_cannot_see_drafts()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var published = await seller.PublishedAsync();
        var draft = await seller.Http.CreateDraftAsync();
        var email = await db.InsertPasswordAccountAsync("admin", "an admin password");
        var (admin, signedIn) = await api.SignInAsync(email, "an admin password");
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

        using var create = await admin.PostCommandAsync("/api/v1/auctions", ApiCalls.Terms());
        using var bid = await admin.BidAsync(published, 1500);
        using var draftBid = await admin.BidAsync(draft, 1500);

        await create.AssertProblemAsync(HttpStatusCode.Forbidden, "admin_only");
        await bid.AssertProblemAsync(HttpStatusCode.Forbidden, "admin_only");
        await draftBid.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
    }
}
