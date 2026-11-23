using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auction.IntegrationTests.Database;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
    public async Task A_bid_through_the_API_reaches_the_engine_over_gRPC_under_the_requests_trace_id_and_is_stored()
    {
        var api = await db.ApiAsync();
        var engine = await db.EngineServiceAsync();
        var buyer = await api.SignUpAsync();
        var id = await (await api.SignUpAsync()).PublishedAsync();
        var traceId = ActivityTraceId.CreateRandom().ToHexString();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/auctions/{id}/bids") { Content = JsonContent.Create(new { amountCents = 1500 }) };
        request.Headers.Add(ApiCalls.KeyHeader, Guid.NewGuid().ToString());
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        using var response = await buyer.Http.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Console logging is buffered, so the engine's line may take a moment to appear.
        string? line = null;
        for (var i = 0; i < 50 && line is null; i++)
        {
            line = (await engine.LogsAsync()).Split('\n').FirstOrDefault(l => l.Contains(traceId, StringComparison.Ordinal));
            await Task.Delay(line is null ? 100 : 0, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(line);
        Assert.Contains("/auction.v1.AuctionCommandService/PlaceBid", line, StringComparison.Ordinal);
        Assert.Equal(1L, await db.ScalarAsync<long>(
            MigratedDatabase.Verifier, "SELECT count(*) FROM bids WHERE auction_id = $1 AND bidder_id = $2", id, buyer.Id));
    }

    [Fact]
    public async Task The_APIs_own_database_connection_can_read_auctions_but_not_write_them_or_their_bids()
    {
        var api = await db.ApiAsync();
        var id = await (await api.SignUpAsync()).PublishedAsync();
        await using var connection = await api.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);

        async Task<string?> RunAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection) { Parameters = { new() { Value = id } } };
            try
            {
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                return null;
            }
            catch (PostgresException e)
            {
                return e.SqlState;
            }
        }

        Assert.Null(await RunAsync("SELECT title FROM auctions WHERE id = $1"));
        Assert.All(
            [
                await RunAsync("UPDATE auctions SET title = 'Changed' WHERE id = $1"),
                await RunAsync("DELETE FROM auctions WHERE id = $1"),
                await RunAsync("INSERT INTO bids (auction_id, seq, bidder_id, amount_cents, source, accepted_at, request_id) VALUES ($1, 1, gen_random_uuid(), 5000, 'bidder', now(), gen_random_uuid())"),
            ],
            state => Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, state));
        Assert.Equal(0L, await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT count(*) FROM bids WHERE auction_id = $1", id));
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
        Assert.Equal((await first.JsonAsync()).GetProperty("bidSequence").GetInt64(), replay.GetProperty("bidSequence").GetInt64());
        await changed.AssertProblemAsync(HttpStatusCode.UnprocessableEntity, "key_reused");
        Assert.Equal(1, await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT count(*) FROM bids WHERE auction_id = $1", id));
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
    public async Task The_detail_tells_a_bidder_who_was_outbid_that_they_have_bid()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var first = await api.SignUpAsync();
        var second = await api.SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));
        using (var bid = await first.Http.BidAsync(id, 1000))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        using (var bid = await second.Http.BidAsync(id, 1100))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        async Task<(bool Lead, bool HasBid)> StandingAsync(HttpClient http)
        {
            var detail = await http.GetJsonAsync($"/api/v1/auctions/{id}");
            return (detail.GetProperty("youLead").GetBoolean(), detail.GetProperty("youHaveBid").GetBoolean());
        }

        Assert.Equal((false, true), await StandingAsync(first.Http));
        Assert.Equal((true, true), await StandingAsync(second.Http));
        Assert.Equal((false, false), await StandingAsync(seller.Http));
        Assert.Equal((false, false), await StandingAsync(api.Anonymous()));
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
