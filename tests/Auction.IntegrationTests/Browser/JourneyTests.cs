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
        await Assertions.Expect(buyer.GetByTestId("bid-result")).ToContainTextAsync("You are the highest bidder");

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
        await retry.OpenCreateAccountAsync();
        await retry.GetByLabel("Display name").FillAsync("Returning user");
        await retry.GetByLabel("Email for the new account").FillAsync(email);
        await retry.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await Assertions.Expect(retry.GetByTestId("sign-in-error")).ToContainTextAsync("already exists");
        await retry.GetByRole(AriaRole.Button, new() { Name = $"Sign in as {email}" }).ClickAsync();
        await Assertions.Expect(retry.GetByTestId("signed-in-as")).ToHaveTextAsync("Returning user");
    }

    [Fact]
    public async Task The_sign_in_page_has_an_email_field_and_no_password_field()
    {
        var visitor = await browsers.NewPageAsync(await Browsers.SiteAsync(db));

        await visitor.GotoAsync("/sign-in");

        await Assertions.Expect(visitor.GetByLabel("Email", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(visitor.Locator("input[type=password]")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task An_email_no_account_uses_is_told_so_and_reminded_of_the_daily_reset()
    {
        var visitor = await browsers.NewPageAsync(await Browsers.SiteAsync(db));

        await visitor.GotoAsync("/sign-in");
        await visitor.GetByLabel("Email", new() { Exact = true }).FillAsync($"{Guid.NewGuid():N}@example.test");
        await visitor.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();

        await Assertions.Expect(visitor.GetByTestId("sign-in-error")).ToContainTextAsync("No account uses that email");
        await Assertions.Expect(visitor.GetByTestId("sign-in-error")).ToContainTextAsync("daily reset");
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
