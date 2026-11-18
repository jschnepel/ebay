using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>Open browsers stay consistent without a reload (INT-02, RT-01, A5's 2-second bound).</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class LiveBrowserTests(MigratedDatabase db, Browsers browsers)
{
    private static readonly LocatorAssertionsToHaveTextOptions WithinBound = new() { Timeout = 2_000 };

    [Fact]
    public async Task Every_open_browser_shows_each_new_price_within_two_seconds_without_a_reload()
    {
        var site = await Browsers.SiteAsync(db);
        var seller = await browsers.NewPageAsync(site);
        var first = await browsers.NewPageAsync(site);
        var second = await browsers.NewPageAsync(site);
        var directory = await browsers.NewPageAsync(site);
        var title = $"Live lamp {Guid.NewGuid():N}"[..18];

        await seller.SignUpAsync("Seller");
        var auction = await seller.ListAndPublishAsync(title, openingDollars: "10.00", reserveDollars: "12.00");
        await seller.GotoAsync(auction);
        await first.SignUpAsync("First buyer");
        await first.GotoAsync(auction);
        await second.SignUpAsync("Second buyer");
        await second.GotoAsync(auction);
        await directory.GotoAsync("/");
        var row = await directory.DirectoryRowAsync(title);
        await Assertions.Expect(row).ToContainTextAsync("$10.00 (opening)");

        await first.BidAsync("20.00");
        foreach (var page in new[] { seller, second })
        {
            await Assertions.Expect(page.GetByTestId("current-price")).ToHaveTextAsync("$12.00", WithinBound);
        }

        await Assertions.Expect(row.GetByTestId("price")).ToHaveTextAsync("$12.00", WithinBound);

        await second.BidAsync("30.00");
        foreach (var page in new[] { seller, first })
        {
            await Assertions.Expect(page.GetByTestId("current-price")).ToHaveTextAsync("$21.00", WithinBound);
        }

        await Assertions.Expect(row.GetByTestId("price")).ToHaveTextAsync("$21.00", WithinBound);
    }

    [Fact]
    public async Task A_browser_that_was_offline_shows_the_bids_it_missed_once_it_is_back()
    {
        var site = await Browsers.SiteAsync(db);
        var seller = await browsers.NewPageAsync(site);
        var buyer = await browsers.NewPageAsync(site);
        await seller.SignUpAsync("Seller");
        var auction = await seller.ListAndPublishAsync($"Offline {Guid.NewGuid():N}"[..16], openingDollars: "5.00", reserveDollars: "6.00");
        await seller.GotoAsync(auction);
        await buyer.SignUpAsync("Buyer");
        await buyer.GotoAsync(auction);

        await seller.Context.SetOfflineAsync(true);
        await buyer.BidAsync("9.00");
        await Assertions.Expect(buyer.GetByTestId("current-price")).ToHaveTextAsync("$6.00");
        await seller.Context.SetOfflineAsync(false);

        // The hint was lost while offline; the reconnect (or, at the latest, the 10-second reconcile) re-fetches.
        await Assertions.Expect(seller.GetByTestId("current-price")).ToHaveTextAsync("$6.00", new() { Timeout = 12_000 });
    }
}
