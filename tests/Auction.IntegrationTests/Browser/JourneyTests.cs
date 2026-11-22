using System.Globalization;
using System.Text.Json;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>The demo journey in a real browser against the whole stack: client, API, engine and database.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class JourneyTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task A_seller_lists_a_buyer_bids_and_both_see_the_new_price_after_a_reload()
    {
        var site = await Browsers.SiteAsync(db);
        var seller = await browsers.NewPageAsync(site);
        var buyer = await browsers.NewPageAsync(site);
        var title = $"Film camera {Guid.NewGuid():N}"[..20];

        await seller.SignUpAsync("Seller");
        var auction = await seller.ListAndPublishAsync(title, openingDollars: "10.00", reserveDollars: "15.00");

        await buyer.SignUpAsync("Buyer");
        await buyer.GotoAsync(auction);
        await Assertions.Expect(buyer.GetByTestId("current-price")).ToHaveTextAsync("$10.00 (opening)");
        await buyer.BidAsync("25.00");
        await Assertions.Expect(buyer.GetByTestId("bid-result")).ToContainTextAsync($"Accepted · $25.00 · {title}");

        // The maximum covers the reserve, so the price rises to it and no further.
        foreach (var page in new[] { buyer, seller })
        {
            await page.ReloadAsync();
            await Assertions.Expect(page.GetByTestId("current-price")).ToHaveTextAsync("$15.00");
        }

        await buyer.GotoAsync("/my-bids");
        await Assertions.Expect(buyer.GetByTestId("my-bid-row").Filter(new() { HasText = title })).ToContainTextAsync("Leading");
        await buyer.GotoAsync("/");
        await Assertions.Expect(await buyer.DirectoryRowAsync(title)).ToContainTextAsync("$15.00");
    }

    [Theory]
    [InlineData(null, "Check the connection")]
    [InlineData(502, "We couldn't confirm your bid.")]
    [InlineData(504, "We couldn't confirm your bid.")]
    public async Task A_reformatted_bid_recovers_its_acceptance_after_the_reply_is_lost(int? gatewayStatus, string expectedMessage)
    {
        var site = await Browsers.SiteAsync(db);
        var seller = await browsers.NewPageAsync(site);
        var buyer = await browsers.NewPageAsync(site);
        await seller.SignUpAsync("Seller");
        var title = $"Retry {Guid.NewGuid():N}";
        var auction = await seller.ListAndPublishAsync(title, openingDollars: "10", reserveDollars: "15");
        await buyer.SignUpAsync("Buyer");
        await buyer.GotoAsync(auction);

        // Let the server commit the real bid before simulating what the browser receives.
        DateTimeOffset? acceptedAt = null;
        var releaseReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationToken = TestContext.Current.CancellationToken;
        await buyer.RouteAsync("**/api/v1/auctions/*/bids", async route =>
        {
            if (route.Request.Method != "POST")
            {
                await route.ContinueAsync();
                return;
            }

            var response = await route.FetchAsync();
            if (response.Status == 201)
            {
                using var body = JsonDocument.Parse(await response.TextAsync());
                acceptedAt = body.RootElement.GetProperty("decidedAt").GetDateTimeOffset();
            }
            await response.DisposeAsync();
            await releaseReply.Task.WaitAsync(cancellationToken);
            if (gatewayStatus is int status)
            {
                await route.FulfillAsync(new() { Status = status, ContentType = "text/plain", Body = "Upstream unavailable" });
            }
            else
            {
                await route.AbortAsync();
            }
        });
        var button = buyer.GetByTestId("bid-submit");
        await SubmitPendingBidAsync(buyer, button, releaseReply);
        var receipt = buyer.GetByTestId("bid-result");
        await Assertions.Expect(receipt).ToContainTextAsync(expectedMessage);
        await buyer.Mouse.MoveAsync(0, 0);
        await Assertions.Expect(button).ToHaveTextAsync("Unconfirmed · retry $50.00");
        await Assertions.Expect(button).ToBeEnabledAsync();
        await Assertions.Expect(button).ToHaveCSSAsync("background-color", "rgb(255, 218, 68)");
        var originalTime = Assert.IsType<DateTimeOffset>(acceptedAt);
        await buyer.UnrouteAllAsync(new() { Behavior = UnrouteBehavior.Wait });

        await buyer.BidAsync("50.00");
        await Assertions.Expect(button).ToHaveTextAsync("Accepted");
        await Assertions.Expect(button).ToHaveCSSAsync("background-color", "rgb(46, 125, 50)");
        var time = originalTime.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        await Assertions.Expect(receipt).ToHaveTextAsync($"Accepted · $50.00 · {title} · {time}");
        await Assertions.Expect(buyer.GetByTestId("bid-count")).ToHaveTextAsync("1");

        // Once the bidder has received the receipt, another submission is a fresh bid.
        await buyer.BidAsync("50");
        await buyer.Mouse.MoveAsync(0, 0);
        await Assertions.Expect(button).ToHaveTextAsync("Rejected · place bid again");
        await Assertions.Expect(button).ToHaveCSSAsync("background-color", "rgb(179, 38, 30)");
        await Assertions.Expect(receipt).ToContainTextAsync($"Rejected · $50.00 · {title}");
        await Assertions.Expect(receipt.Locator("time")).ToContainTextAsync("UTC");
        await Assertions.Expect(receipt.GetByTestId("bid-reason")).ToHaveTextAsync("Your new maximum must be higher than your current maximum.");
        await seller.ReloadAsync();
        await Assertions.Expect(seller.GetByTestId("bid-result")).ToHaveCountAsync(0);
        await Assertions.Expect(seller.GetByTestId("bid-count")).ToHaveTextAsync("1");
        await Assertions.Expect(seller.GetByTestId("current-price")).ToHaveTextAsync("$15.00");
    }

    private static async Task SubmitPendingBidAsync(IPage buyer, ILocator button, TaskCompletionSource releaseReply)
    {
        try
        {
            await buyer.BidAsync("50");
            await Assertions.Expect(button).ToBeDisabledAsync();
            await Assertions.Expect(button).ToHaveTextAsync("Submitting $50.00…");
            await Assertions.Expect(button).ToHaveCSSAsync("background-color", "rgb(255, 218, 68)");
        }
        finally
        {
            releaseReply.TrySetResult();
        }
    }

    [Fact]
    public async Task A_demo_account_signs_out_and_back_in_with_its_email_and_a_repeated_sign_up_is_offered_that_account()
    {
        var site = await Browsers.SiteAsync(db);
        var page = await browsers.NewPageAsync(site);
        var email = await page.SignUpAsync("Returning user");

        await page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("signed-in-as")).ToHaveCountAsync(0);
        await page.GotoAsync("/sign-in");
        await page.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("signed-in-as")).ToHaveTextAsync("Returning user");

        // As if the first sign-up's answer was lost and the visitor tries again from a fresh browser.
        var retry = await browsers.NewPageAsync(site);
        await retry.GotoAsync("/sign-in");
        await retry.GetByLabel("Display name").FillAsync("Returning user");
        await retry.GetByLabel("Email for the new account").FillAsync(email);
        await retry.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await Assertions.Expect(retry.GetByTestId("sign-in-error")).ToContainTextAsync("already exists");
        await retry.GetByRole(AriaRole.Button, new() { Name = $"Sign in as {email}" }).ClickAsync();
        await Assertions.Expect(retry.GetByTestId("signed-in-as")).ToHaveTextAsync("Returning user");
    }

    [Fact]
    public async Task A_visitor_who_is_not_signed_in_is_asked_to_sign_in_to_bid_or_sell()
    {
        var site = await Browsers.SiteAsync(db);
        var visitor = await browsers.NewPageAsync(site);

        await visitor.GotoAsync("/sell");

        await Assertions.Expect(visitor.GetByRole(AriaRole.Link, new() { Name = "Sign in", Exact = true }).Last).ToBeVisibleAsync();
        await Assertions.Expect(visitor.GetByRole(AriaRole.Link, new() { Name = "New listing" })).ToHaveCountAsync(0);
    }
}
