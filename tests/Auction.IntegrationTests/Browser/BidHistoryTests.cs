using System.Net;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>The auction page's bid history comes in pages of 10 by default, newest first.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class BidHistoryTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task The_bid_history_shows_ten_at_a_time_with_older_and_newer_pages_and_a_choice_of_size()
    {
        // The shared test site caps pages at 5 rows to exercise paging elsewhere; this one has the real limit.
        var (host, site) = await db.SiteWithAsync(new Dictionary<string, string> { ["Api:MaxPageSize"] = "50" });
        await using var _ = host;
        var id = await AuctionWithBidsAsync(12);
        var page = await browsers.NewPageAsync(site);
        var rows = page.GetByTestId("bid-row");

        await page.GotoAsync($"/auctions/{id}");

        await Assertions.Expect(rows).ToHaveCountAsync(10);
        await Assertions.Expect(rows.First).ToContainTextAsync("$21.00");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Newer" })).ToBeDisabledAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Older" }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(2);
        await Assertions.Expect(rows.Last).ToContainTextAsync("$10.00");
        await Assertions.Expect(page.GetByTestId("bid-page")).ToHaveTextAsync("Page 2");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Older" })).ToBeDisabledAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Newer" }).ClickAsync();
        await Assertions.Expect(rows).ToHaveCountAsync(10);
        await Assertions.Expect(rows.First).ToContainTextAsync("$21.00");

        await page.GetByLabel("Rows per page").SelectOptionAsync("25");
        await Assertions.Expect(rows).ToHaveCountAsync(12);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Older" })).ToBeDisabledAsync();
    }

    [Fact]
    public async Task A_short_history_has_no_page_controls()
    {
        var site = await Browsers.SiteAsync(db);
        var id = await AuctionWithBidsAsync(3);
        var page = await browsers.NewPageAsync(site);

        await page.GotoAsync($"/auctions/{id}");

        await Assertions.Expect(page.GetByTestId("bid-row")).ToHaveCountAsync(3);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Older" })).ToHaveCountAsync(0);
    }

    // A fixed-step auction opening at $10.00 with $1.00 steps and the given number of bids, two bidders taking turns.
    private async Task<Guid> AuctionWithBidsAsync(int count)
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var bidders = new[] { await api.SignUpAsync(), await api.SignUpAsync() };
        var id = await seller.PublishedAsync(ApiCalls.Terms(
            title: $"History {Guid.NewGuid():N}"[..16], bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));
        for (var i = 0; i < count; i++)
        {
            using var bid = await bidders[i % 2].Http.BidAsync(id, 1000 + (100 * i));
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        return id;
    }
}
