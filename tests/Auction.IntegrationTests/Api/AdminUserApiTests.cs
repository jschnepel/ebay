using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

/// <summary>The admin Users screen and each user's page: who they are, what they've listed and what they've bid on.</summary>
public sealed class AdminUserApiTests(MigratedDatabase db)
{
    [Fact]
    public async Task The_users_list_shows_email_last_sign_in_and_counts()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("List admin", role: "admin");
        var seller = await api.SignUpAsync($"Counted {Guid.NewGuid():N}"[..16]);
        var buyer = await api.SignUpAsync($"Counter {Guid.NewGuid():N}"[..16]);
        var first = await seller.PublishedAsync(ApiCalls.Terms(title: "Counted one"));
        await seller.PublishedAsync(ApiCalls.Terms(title: "Counted two"));
        await seller.Http.CreateDraftAsync();
        using (var bid = await buyer.Http.BidAsync(first, 1500))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        var sellerRow = await RowAsync(admin.Http, seller.Id);
        var buyerRow = await RowAsync(admin.Http, buyer.Id);

        Assert.Equal((seller.Email, 2L, 0L), (sellerRow.GetProperty("email").GetString(), sellerRow.GetProperty("listingCount").GetInt64(), sellerRow.GetProperty("bidCount").GetInt64()));
        Assert.Equal((0L, 1L), (buyerRow.GetProperty("listingCount").GetInt64(), buyerRow.GetProperty("bidCount").GetInt64()));
        Assert.True(DateTimeOffset.UtcNow - sellerRow.GetProperty("lastSignInAt").GetDateTimeOffset() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task The_users_list_filters_by_kind_state_and_email()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Filtering admin", role: "admin");
        var user = await api.SignUpAsync("To be disabled");
        using (var disable = await admin.Http.PostAsJsonAsync($"/api/v1/admin/users/{user.Id}/disable", new { reason = "test" }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        }

        var admins = await AllAsync(admin.Http, "kind=visitor_admin");
        Assert.Contains(admins, u => u.GetProperty("id").GetGuid() == admin.Id);
        Assert.All(admins, u => Assert.Equal("visitor_admin", u.GetProperty("kind").GetString()));

        var disabled = await AllAsync(admin.Http, "state=disabled");
        Assert.Contains(disabled, u => u.GetProperty("id").GetGuid() == user.Id);
        Assert.All(disabled, u => Assert.True(u.GetProperty("disabled").GetBoolean()));

        var byEmail = await AllAsync(admin.Http, $"q={Uri.EscapeDataString(user.Email)}");
        Assert.Equal(user.Id, Assert.Single(byEmail).GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.BadRequest, await admin.Http.StatusOfGetAsync("/api/v1/admin/users?state=sleeping"));
    }

    [Fact]
    public async Task The_users_search_takes_wildcards_literally_and_has_a_length_limit()
    {
        var api = await db.ApiWithAsync(new Dictionary<string, string> { ["Api:MaxPageSize"] = "50" });
        var admin = await api.SignUpAsync("Search admin", role: "admin");
        var marker = Guid.NewGuid().ToString("N")[..8];
        await api.SignUpAsync($"Plain {marker}");

        var wildcard = await admin.Http.GetJsonAsync($"/api/v1/admin/users?pageSize=50&q={Uri.EscapeDataString($"_ {marker}")}");
        using var tooLong = await admin.Http.GetAsync(new Uri($"/api/v1/admin/users?q={new string('a', 101)}", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(0, wildcard.GetProperty("items").GetArrayLength());
        await tooLong.AssertProblemAsync(HttpStatusCode.BadRequest, "invalid_search");
    }

    [Fact]
    public async Task A_users_page_counts_their_listings_sales_and_bids()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Profile admin", role: "admin");
        var seller = await api.SignUpAsync("Profiled seller");
        var buyer = await api.SignUpAsync("Profiled buyer");
        var sold = await seller.PublishedAsync(ApiCalls.Terms(title: "Sold lot"));
        await seller.PublishedAsync(ApiCalls.Terms(title: "Unsold lot"));
        using (var bid = await buyer.Http.BidAsync(sold, 2500))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        await db.ExecuteAsync(MigratedDatabase.Migrator, "UPDATE auctions SET status = 'closed', outcome = 'sold', closed_at = clock_timestamp() WHERE id = $1", sold);

        var sellerProfile = await admin.Http.GetJsonAsync($"/api/v1/admin/users/{seller.Id}");
        var buyerProfile = await admin.Http.GetJsonAsync($"/api/v1/admin/users/{buyer.Id}");

        Assert.Equal(("Profiled seller", seller.Email, 2L, 1L), (sellerProfile.GetProperty("displayName").GetString(), sellerProfile.GetProperty("email").GetString(), sellerProfile.GetProperty("listings").GetInt64(), sellerProfile.GetProperty("sold").GetInt64()));
        Assert.Equal((1L, 1L, 1L), (buyerProfile.GetProperty("bids").GetInt64(), buyerProfile.GetProperty("auctionsBidOn").GetInt64(), buyerProfile.GetProperty("won").GetInt64()));
    }

    [Fact]
    public async Task A_users_listings_leave_out_drafts()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Listings admin", role: "admin");
        var seller = await api.SignUpAsync("Listing seller");
        var published = await seller.PublishedAsync(ApiCalls.Terms(title: "Shown lot"));
        await seller.Http.CreateDraftAsync(ApiCalls.Terms(title: "Hidden draft"));

        var listings = await AllAsync(admin.Http, null, $"/api/v1/admin/users/{seller.Id}/listings");

        var only = Assert.Single(listings);
        Assert.Equal((published, "Shown lot", "active"), (only.GetProperty("auctionId").GetGuid(), only.GetProperty("title").GetString(), only.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_users_bidding_shows_their_standing()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Bidding admin", role: "admin");
        var seller = await api.SignUpAsync("Bidding seller");
        var buyer = await api.SignUpAsync("Bidding buyer");
        var rival = await api.SignUpAsync("Bidding rival");
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: "Contested lot", bidEntry: "fixed_increment"));
        using (var bid = await buyer.Http.BidAsync(id, 1000))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        var leading = Assert.Single(await AllAsync(admin.Http, null, $"/api/v1/admin/users/{buyer.Id}/bidding"));
        Assert.Equal(("Contested lot", "leading", 1000L), (leading.GetProperty("title").GetString(), leading.GetProperty("standing").GetString(), leading.GetProperty("theirHighestCents").GetInt64()));

        using (var bid = await rival.Http.BidAsync(id, 1100))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        var outbid = Assert.Single(await AllAsync(admin.Http, null, $"/api/v1/admin/users/{buyer.Id}/bidding"));
        Assert.Equal(("outbid", 1100L), (outbid.GetProperty("standing").GetString(), outbid.GetProperty("currentPriceCents").GetInt64()));
    }

    [Fact]
    public async Task An_unknown_user_is_404_and_non_admins_get_404_for_every_user_route()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Unknown admin", role: "admin");
        var user = await api.SignUpAsync("Curious user");

        using (var unknown = await admin.Http.GetAsync($"/api/v1/admin/users/{Guid.NewGuid()}", TestContext.Current.CancellationToken))
        {
            await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        // The same routes answer an admin, so the 404s are the admin check and not a missing route.
        foreach (var path in new[] { "", "/listings", "/bidding" })
        {
            Assert.Equal(HttpStatusCode.OK, await admin.Http.StatusOfGetAsync($"/api/v1/admin/users/{user.Id}{path}"));
            Assert.Equal(HttpStatusCode.NotFound, await user.Http.StatusOfGetAsync($"/api/v1/admin/users/{user.Id}{path}"));
            Assert.Equal(HttpStatusCode.NotFound, await api.Anonymous().StatusOfGetAsync($"/api/v1/admin/users/{user.Id}{path}"));
        }
    }

    private static async Task<JsonElement> RowAsync(HttpClient admin, Guid id) =>
        Assert.Single(await AllAsync(admin, $"q={id}"), u => u.GetProperty("id").GetGuid() == id);

    // Every page, following the cursor; the test host's pages are small.
    private static async Task<List<JsonElement>> AllAsync(HttpClient admin, string? query, string path = "/api/v1/admin/users")
    {
        var items = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var url = path + (query is null ? "" : $"{separator}{query}");
            if (cursor is not null)
            {
                url += $"{(url.Contains('?', StringComparison.Ordinal) ? '&' : '?')}cursor={cursor}";
            }

            var page = await admin.GetJsonAsync(url);
            items.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return items;
    }
}
