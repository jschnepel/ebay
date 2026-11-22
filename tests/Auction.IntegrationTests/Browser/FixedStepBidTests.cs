using System.Net;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>
/// The bid card follows the auction's bidding mode: a fixed-step auction offers one button for the exact next
/// step, since any other amount is refused; a proxy auction asks for a maximum.
/// </summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class FixedStepBidTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task A_fixed_step_auction_offers_one_button_for_the_next_step_and_no_amount_box()
    {
        var (page, id) = await FixedStepAuctionAsync();

        await page.GotoAsync($"/auctions/{id}");

        await Assertions.Expect(page.GetByTestId("fixed-step-note")).ToContainTextAsync("Fixed steps of $1.00");
        await Assertions.Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Your" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("bid-next-step")).ToHaveTextAsync("Bid $10.00");

        await page.BidNextStepAsync();

        await Assertions.Expect(page.GetByTestId("bid-result")).ToContainTextAsync("highest bidder");
        await Assertions.Expect(page.GetByTestId("bid-next-step")).ToHaveTextAsync("Bid $11.00");
    }

    [Fact]
    public async Task When_someone_bids_first_the_card_says_so_and_offers_the_new_step()
    {
        var (page, id) = await FixedStepAuctionAsync();
        var rival = await (await db.ApiAsync()).SignUpAsync();
        // No live updates, so the page still shows the step the rival is about to take.
        await page.RouteAsync("**/hubs/**", route => route.AbortAsync());
        await page.GotoAsync($"/auctions/{id}");
        await Assertions.Expect(page.GetByTestId("bid-next-step")).ToHaveTextAsync("Bid $10.00");

        using (var first = await rival.Http.BidAsync(id, 1000))
        {
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        }

        await page.BidNextStepAsync();

        await Assertions.Expect(page.GetByTestId("bid-result")).ToContainTextAsync("Someone bid first. The next step is now $11.00.");
        await Assertions.Expect(page.GetByTestId("bid-next-step")).ToHaveTextAsync("Bid $11.00");
    }

    [Fact]
    public async Task A_proxy_auction_keeps_the_amount_box_for_a_maximum()
    {
        var site = await Browsers.SiteAsync(db);
        var seller = await (await db.ApiAsync()).SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: $"Proxy card {Guid.NewGuid():N}"[..18]));
        var page = await browsers.NewPageAsync(site);
        await page.SignUpAsync("Proxy bidder");

        await page.GotoAsync($"/auctions/{id}");

        await Assertions.Expect(page.GetByLabel("Your maximum ($)")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("bid-next-step")).ToHaveCountAsync(0);
    }

    // A fixed-step auction opening at $10.00 with $1.00 steps, and a signed-in bidder's page.
    private async Task<(IPage Page, Guid Id)> FixedStepAuctionAsync()
    {
        var site = await Browsers.SiteAsync(db);
        var seller = await (await db.ApiAsync()).SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(
            title: $"Fixed steps {Guid.NewGuid():N}"[..19], bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));
        var page = await browsers.NewPageAsync(site);
        await page.SignUpAsync("Step bidder");
        return (page, id);
    }
}
