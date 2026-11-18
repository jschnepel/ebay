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
            await Assertions.Expect(watcher.GetByTestId("current-price")).ToHaveTextAsync("$12.00", new() { Timeout = 12_000 });
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
