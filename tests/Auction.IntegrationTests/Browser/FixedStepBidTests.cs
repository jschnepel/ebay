using System.Net;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>
/// The bid card follows the auction's bidding mode: a fixed-step auction offers one button for the exact next
/// step, since any other amount is refused; a proxy auction asks for a maximum. It also says where the bidder
/// stands: leading (once), outbid, or how an ended auction came out for them.
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
        await Assertions.Expect(page.GetByText("highest bidder")).ToHaveCountAsync(1);
        // Leading, another step only raises the bidder's own price, and the button says so.
        await Assertions.Expect(page.GetByTestId("bid-next-step")).ToHaveTextAsync("Raise your own bid to $11.00");
    }

    [Fact]
    public async Task Being_outbid_replaces_the_bid_result_with_an_outbid_notice()
    {
        var (page, id) = await FixedStepAuctionAsync();
        var rival = await (await db.ApiAsync()).SignUpAsync();
        await page.GotoAsync($"/auctions/{id}");
        await page.BidNextStepAsync();
        await Assertions.Expect(page.GetByTestId("bid-result")).ToContainTextAsync("highest bidder");

        using (var outbid = await rival.Http.BidAsync(id, 1100))
        {
            Assert.Equal(HttpStatusCode.Created, outbid.StatusCode);
        }

        await Assertions.Expect(page.GetByTestId("standing")).ToHaveTextAsync("You've been outbid.");
        await Assertions.Expect(page.GetByTestId("bid-result")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("highest bidder")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("bid-next-step")).ToHaveTextAsync("Bid $12.00");
    }

    [Fact]
    public async Task An_ended_auction_says_it_has_ended_and_whether_the_bidder_won()
    {
        var (page, id) = await FixedStepAuctionAsync();
        await page.GotoAsync($"/auctions/{id}");
        await page.BidNextStepAsync();
        await Assertions.Expect(page.GetByTestId("bid-result")).ToContainTextAsync("highest bidder");

        // Closed as the engine closes it at the end time (the terms, end time included, can't be moved): the
        // row and its outbox event in one transaction, which the relay announces to the cache and the page.
        await db.ExecuteAsync(MigratedDatabase.Migrator, """
            WITH closed AS (
                UPDATE auctions SET status = 'closed', outcome = 'sold', closed_at = clock_timestamp(), version = version + 1
                WHERE id = $1 RETURNING id, version)
            INSERT INTO outbox_events (auction_id, auction_version, type, payload)
            SELECT id, version, 'auction.closed', jsonb_build_object('auctionId', id, 'version', version) FROM closed
            """, id);

        await Assertions.Expect(page.GetByTestId("standing")).ToHaveTextAsync("This auction has ended. You won it.");
        await Assertions.Expect(page.GetByTestId("bid-next-step")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("bid-result")).ToHaveCountAsync(0);
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
        await Assertions.Expect(page.GetByTestId("your-maximum")).ToHaveCountAsync(0);

        // The price moves only as far as it takes to lead, so the card says what the bidder's maximum is,
        // when the bid lands and after a reload.
        await page.BidAsync("70.00");

        await Assertions.Expect(page.GetByTestId("bid-result")).ToContainTextAsync("You are the highest bidder, up to your maximum of $70.00");
        await Assertions.Expect(page.GetByTestId("current-price")).ToHaveTextAsync("$20.00"); // the reserve, far below the maximum
        await Assertions.Expect(page.GetByTestId("your-maximum")).ToHaveTextAsync("$70.00");
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByTestId("your-maximum")).ToHaveTextAsync("$70.00");
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
