using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

/// <summary>The admin Users screen: who each account is, how active, and filters by kind and state.</summary>
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
