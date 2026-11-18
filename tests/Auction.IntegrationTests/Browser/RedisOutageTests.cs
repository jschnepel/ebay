using System.Net;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>
/// Redis down (RT-01, REL-02): bidding and reads still work, live hints stop, and open views heal on the
/// 10-second reconcile. Runs alone, after the parallel tests, because it stops the shared Redis.
/// </summary>
[Collection(RedisOutage.Name)]
public sealed class RedisOutageTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task With_Redis_stopped_bids_succeed_pages_load_and_open_browsers_reconcile_within_ten_seconds()
    {
        if (Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 })
        {
            // Against an external stack the test can't stop its Redis.
            return;
        }

        var site = await Browsers.SiteAsync(db);
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(reserveCents: 1200));
        var watcher = await browsers.NewPageAsync(site);
        await watcher.GotoAsync($"/auctions/{id}");
        await Assertions.Expect(watcher.GetByTestId("current-price")).ToHaveTextAsync("$10.00 (opening)");

        await db.Redis.StopAsync(TestContext.Current.CancellationToken);
        try
        {
            using (var bid = await buyer.Http.BidAsync(id, 2000))
            {
                Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
            }

            Assert.Equal(1200, (await api.Anonymous().GetJsonAsync($"/api/v1/auctions/{id}")).GetProperty("currentPriceCents").GetInt64());

            // Sessions are checked in PostgreSQL and limits counted locally: a signed-in user still works,
            // and a new sign-in still ends the old session.
            Assert.Equal(HttpStatusCode.OK, await seller.Http.StatusOfGetAsync("/api/v1/account/me"));
            Assert.Equal(HttpStatusCode.Created, (await seller.Http.PostCommandAsync("/api/v1/auctions", ApiCalls.Terms())).StatusCode);
            var (_, again) = await api.SignInAsync(buyer.Email);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, await buyer.Http.StatusOfGetAsync("/api/v1/account/me"));
            await Assertions.Expect(watcher.GetByTestId("current-price")).ToHaveTextAsync("$12.00", new() { Timeout = 12_000 });

            // Admin health shows Redis degraded, never the whole system down (REL-02).
            var adminEmail = await db.InsertPasswordAccountAsync("admin", "outage-admin-password");
            var (admin, signedIn) = await api.SignInAsync(adminEmail, "outage-admin-password");
            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            var health = (await admin.GetJsonAsync("/api/v1/admin/health")).GetProperty("components").EnumerateArray()
                .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("state").GetString());
            Assert.Equal(("degraded", "ok", "ok"), (health["Redis"], health["Engine"], health["PostgreSQL"]));
        }
        finally
        {
            await db.Redis.StartAsync(TestContext.Current.CancellationToken);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RedisOutage
{
    public const string Name = "Redis outage";

    private RedisOutage()
    {
    }
}
