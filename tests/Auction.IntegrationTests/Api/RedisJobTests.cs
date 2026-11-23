using System.Net;
using System.Net.Http.Json;
using Auction.Api.Live;
using Auction.IntegrationTests.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Auction.IntegrationTests.Api;

/// <summary>REL-02 and UI-03: Redis caches and counters that help but never decide, and the directory search.</summary>
public sealed class RedisJobTests(MigratedDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Whether_a_viewer_has_bid_is_remembered_between_views()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var watcher = await api.SignUpAsync();
        var id = await seller.PublishedAsync();
        var redis = api.Services.GetRequiredService<Auction.Api.Caching.RedisJobs>();
        var key = $"has-bid:{id:N}:{watcher.Id:N}";

        await watcher.Http.GetJsonAsync($"/api/v1/auctions/{id}");
        Assert.Equal("0", (string?)await redis.TryAsync(r => r.StringGetAsync(key)));

        // The remembered answer is the one used: flipped by hand, the next view reports it without asking the database.
        await redis.TryAsync(r => r.StringSetAsync(key, "1"));
        Assert.True((await watcher.Http.GetJsonAsync($"/api/v1/auctions/{id}")).GetProperty("youHaveBid").GetBoolean());
    }

    [Fact]
    public async Task A_bid_through_the_api_is_known_at_once_even_after_the_viewer_was_told_they_had_none()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var watcher = await api.SignUpAsync();
        var rival = await api.SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));
        Assert.False((await watcher.Http.GetJsonAsync($"/api/v1/auctions/{id}")).GetProperty("youHaveBid").GetBoolean());

        using (var bid = await watcher.Http.BidAsync(id, 1000))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        using (var outbid = await rival.Http.BidAsync(id, 1100))
        {
            Assert.Equal(HttpStatusCode.Created, outbid.StatusCode);
        }

        var detail = await watcher.Http.GetJsonAsync($"/api/v1/auctions/{id}");
        Assert.Equal((false, true), (detail.GetProperty("youLead").GetBoolean(), detail.GetProperty("youHaveBid").GetBoolean()));
    }

    [Fact]
    public async Task An_auction_cached_by_an_older_release_still_reads_after_a_deploy()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var id = await seller.PublishedAsync();
        var anonymous = api.Anonymous();
        await anonymous.GetJsonAsync($"/api/v1/auctions/{id}");

        // The entry as the release before the viewer's own fields were added wrote it: without youHaveBid.
        var redis = api.Services.GetRequiredService<Auction.Api.Caching.RedisJobs>();
        await redis.TryAsync(async r =>
        {
            var stored = (string?)await r.HashGetAsync($"auction:{id:N}", "json");
            var json = System.Text.Json.Nodes.JsonNode.Parse(stored ?? "{}")!;
            Assert.True(json["Public"]!.AsObject().Remove("YouHaveBid"));
            await r.HashSetAsync($"auction:{id:N}", "json", json.ToJsonString());
        });

        Assert.Equal(HttpStatusCode.OK, await anonymous.StatusOfGetAsync($"/api/v1/auctions/{id}"));
    }

    [Fact]
    public async Task A_cached_auction_and_directory_page_never_show_an_older_version_after_a_change()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));
        var anonymous = api.Anonymous();

        // An open transaction with an xid holds the relay back (it publishes only below the snapshot's xmin),
        // so nothing but the API's own confirmation of each change can keep the cached page current.
        await using var holdBack = await db.OpenAsync(MigratedDatabase.Migrator);
        await using var transaction = await holdBack.BeginTransactionAsync(Ct);
        await using (var xid = new Npgsql.NpgsqlCommand("SELECT pg_current_xact_id()", holdBack, transaction))
        {
            await xid.ExecuteScalarAsync(Ct);
        }

        for (var round = 0; round < 3; round++)
        {
            // Read (and so cache) the page, change it, then read it again at once.
            var before = await anonymous.GetJsonAsync($"/api/v1/auctions/{id}");
            await anonymous.GetJsonAsync("/api/v1/auctions");
            using (var bid = await buyer.Http.BidAsync(id, before.GetProperty("minimumNextBidCents").GetInt64()))
            {
                Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
            }

            var after = await anonymous.GetJsonAsync($"/api/v1/auctions/{id}");
            Assert.True(after.GetProperty("version").GetInt64() > before.GetProperty("version").GetInt64());
            Assert.Equal(round + 1, after.GetProperty("bidCount").GetInt64());
        }

        await transaction.RollbackAsync(Ct);
        var seen = await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT version FROM auctions WHERE id = $1", id);
        Assert.Equal(seen, (await anonymous.GetJsonAsync($"/api/v1/auctions/{id}")).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_revoked_session_fails_at_once_even_after_it_was_cached()
    {
        var api = await db.ApiAsync();
        var user = await api.SignUpAsync();
        Assert.Equal(HttpStatusCode.OK, await user.Http.StatusOfGetAsync("/api/v1/account/me")); // now cached

        var (_, elsewhere) = await api.SignInAsync(user.Email);

        Assert.Equal(HttpStatusCode.OK, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await user.Http.StatusOfGetAsync("/api/v1/account/me"));
    }

    [Fact]
    public async Task Limits_are_counted_across_api_instances()
    {
        // Both instances see the same client address, as two replicas behind one proxy would.
        var limits = new Dictionary<string, string>
        {
            ["Api:RateLimits:SignUpPerAddressPerHour"] = "3",
            [ApiHost.ClientAddressSetting] = $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}",
        };
        await using var first = await db.ApiWithAsync(limits);
        await using var second = await db.ApiWithAsync(limits);
        await first.SignUpAsync();
        await second.SignUpAsync();
        await first.SignUpAsync();

        using var fourth = await second.Anonymous().PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName = "Over", email = $"{Guid.NewGuid():N}@example.test" }, Ct);

        await fourth.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate_limited");
    }

    [Fact]
    public async Task Online_users_are_counted_across_api_instances()
    {
        await using var first = await db.ApiWithAsync(new Dictionary<string, string>());
        await using var second = await db.ApiWithAsync(new Dictionary<string, string>());
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var presenceA = first.Services.GetRequiredService<Presence>();
        var presenceB = second.Services.GetRequiredService<Presence>();
        var before = await presenceA.OnlineUsersAsync();

        await presenceA.ConnectedAsync(a);
        await presenceB.ConnectedAsync(b);
        await presenceB.ConnectedAsync(b); // a second tab counts once

        Assert.Equal(before + 2, await presenceA.OnlineUsersAsync());
        await presenceB.DisconnectedAsync(b);
        Assert.Equal(before + 2, await presenceA.OnlineUsersAsync());
        await presenceB.DisconnectedAsync(b);
        await presenceA.DisconnectedAsync(a);
        Assert.Equal(before, await presenceB.OnlineUsersAsync());
    }

    [Fact]
    public async Task The_directory_searches_titles_filters_by_status_and_keeps_the_ending_soon_order()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var word = $"zq{Guid.NewGuid():N}"[..12];
        var now = DateTimeOffset.UtcNow;
        var soon = await seller.PublishedAsync(Ending($"Soon {word}", now.AddMinutes(30)));
        var sooner = await seller.PublishedAsync(Ending($"Sooner {word.ToUpperInvariant()}", now.AddMinutes(20)));
        var later = await seller.PublishedAsync(Ending($"Later {word}", now.AddHours(5)));
        var other = await seller.PublishedAsync(Ending("Unrelated item", now.AddMinutes(25)));
        var closed = await InsertClosedAsync(seller.Id, $"Closed {word}");
        var literal = await seller.PublishedAsync(Ending($"100% {word}", now.AddHours(3)));

        var matching = await IdsAsync(api, $"/api/v1/auctions?q={word}&pageSize=2");
        var endingSoon = await IdsAsync(api, $"/api/v1/auctions?q={word}&status=ending_soon");
        var closedOnly = await IdsAsync(api, $"/api/v1/auctions?q={word}&status=closed");
        var percent = await IdsAsync(api, $"/api/v1/auctions?q={Uri.EscapeDataString("100%")}");
        var everything = await IdsAsync(api, "/api/v1/auctions?pageSize=5");

        Assert.Equal([closed, sooner, soon, literal, later], matching.Where(new[] { sooner, soon, literal, later, closed }.Contains));
        Assert.DoesNotContain(other, matching);
        Assert.Equal([sooner, soon], endingSoon);
        Assert.Equal([closed], closedOnly);
        Assert.Contains(literal, percent);
        Assert.DoesNotContain(soon, percent);
        Assert.Contains(other, everything); // control: no query, every published auction
        using var invalid = await api.Anonymous().GetAsync(new Uri("/api/v1/auctions?status=draft", UriKind.Relative), Ct);
        await invalid.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_status");
    }

    private static object Ending(string title, DateTimeOffset endsAt) => new
    {
        title,
        description = "",
        openingPriceCents = 1000,
        incrementCents = 100,
        floorType = "minimum_bid",
        bidEntry = "proxy",
        startsAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        endsAt,
    };

    private async Task<Guid> InsertClosedAsync(Guid seller, string title)
    {
        var id = Guid.CreateVersion7();
        await db.ExecuteAsync(
            MigratedDatabase.Migrator,
            """
            INSERT INTO auctions (id, seller_id, status, title, description, opening_price_cents, increment_cents, floor_type, bid_entry,
                                  starts_at, ends_at, outcome, version, published_at, closed_at, created_at, updated_at)
            VALUES ($1, $2, 'closed', $3, '', 1000, 100, 'minimum_bid', 'proxy', now() - interval '2 hours', now() - interval '1 hour',
                    'no_bids', 3, now() - interval '2 hours', now() - interval '1 hour', now(), now())
            """,
            id,
            seller,
            title);
        return id;
    }

    private static async Task<List<Guid>> IdsAsync(ApiHost api, string url)
    {
        var ids = new List<Guid>();
        var next = url;
        while (true)
        {
            var page = await api.Anonymous().GetJsonAsync(next);
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            if (page.GetProperty("nextCursor").GetString() is not { } cursor || ids.Count > 200)
            {
                return ids;
            }

            next = $"{url}&cursor={Uri.EscapeDataString(cursor)}";
        }
    }
}
