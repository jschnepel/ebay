using System.Net;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>AUTH-02 and NOT-01 in the browser: the signed-out notice and the notification bell, both live.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class SessionAndBellTests(MigratedDatabase db, Browsers browsers)
{
    private static readonly LocatorAssertionsToHaveTextOptions WithinBound = new() { Timeout = 2_000 };

    [Fact]
    public async Task An_open_page_is_told_within_two_seconds_when_its_account_signs_in_elsewhere()
    {
        var site = await Browsers.SiteAsync(db);
        var first = await browsers.NewPageAsync(site);
        var second = await browsers.NewPageAsync(site);
        var email = await first.SignUpAsync("Two browsers");
        await first.GotoAsync("/my-bids");
        await Assertions.Expect(first.GetByTestId("signed-in-as")).ToHaveTextAsync("Two browsers");

        await second.GotoAsync("/sign-in");
        await second.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await second.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Assertions.Expect(second.GetByTestId("signed-in-as")).ToHaveTextAsync("Two browsers");

        await Assertions.Expect(first.GetByTestId("signed-out-notice")).ToContainTextAsync("signed in elsewhere", new() { Timeout = 2_000 });
        await second.GotoAsync("/my-bids");
        await Assertions.Expect(second.GetByTestId("signed-in-as")).ToHaveTextAsync("Two browsers"); // control
    }

    [Fact]
    public async Task An_outbid_bidder_sees_the_bell_count_within_two_seconds_and_opening_it_clears_the_count()
    {
        if (Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 })
        {
            return; // The other users are created through the in-process API, which an external stack doesn't share.
        }

        var site = await Browsers.SiteAsync(db);
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var rival = await api.SignUpAsync();
        var title = $"Bell test {Guid.NewGuid():N}"[..18];
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: title, bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));
        var page = await browsers.NewPageAsync(site);
        await page.SignUpAsync("Outbid bidder");
        await page.GotoAsync($"/auctions/{id}");
        await page.BidNextStepAsync();
        await Assertions.Expect(page.GetByTestId("bid-result")).ToContainTextAsync("highest bidder");
        await page.GotoAsync("/");
        await Assertions.Expect(page.GetByTestId("unread-count")).ToHaveTextAsync("0");

        using (var outbid = await rival.Http.BidAsync(id, 1100))
        {
            Assert.Equal(HttpStatusCode.Created, outbid.StatusCode);
        }

        await Assertions.Expect(page.GetByTestId("unread-count")).ToHaveTextAsync("1", WithinBound);
        // The bell opens a dropdown on the page you're on; a notice opens its auction and is marked read.
        await page.GetByTestId("bell").ClickAsync();
        var notice = page.GetByTestId("notice-panel").GetByTestId("notification").Filter(new() { HasText = title });
        await Assertions.Expect(notice).ToContainTextAsync("You were outbid");
        Assert.EndsWith("/", new Uri(page.Url).AbsolutePath);
        await notice.ClickAsync();
        await page.WaitForURLAsync($"**/auctions/{id}");
        await Assertions.Expect(page.GetByTestId("notice-panel")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("unread-count")).ToHaveTextAsync("0");
    }

    [Fact]
    public async Task The_bell_dropdown_marks_everything_read_links_to_the_full_list_and_closes_with_escape()
    {
        var site = await Browsers.SiteAsync(db);
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var rival = await api.SignUpAsync();
        var page = await browsers.NewPageAsync(site);
        await page.SignUpAsync("Twice outbid");
        foreach (var n in new[] { 1, 2 })
        {
            var title = $"Panel {n} {Guid.NewGuid():N}"[..16];
            var id = await seller.PublishedAsync(ApiCalls.Terms(title: title, bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));
            await page.GotoAsync($"/auctions/{id}");
            await page.BidNextStepAsync();
            await Assertions.Expect(page.GetByTestId("bid-result")).ToContainTextAsync("highest bidder");
            using var outbid = await rival.Http.BidAsync(id, 1100);
            Assert.Equal(HttpStatusCode.Created, outbid.StatusCode);
        }

        await Assertions.Expect(page.GetByTestId("unread-count")).ToHaveTextAsync("2", WithinBound);
        await page.GetByTestId("bell").ClickAsync();
        await Assertions.Expect(page.GetByTestId("notice-panel").GetByTestId("notification")).ToHaveCountAsync(2);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByTestId("notice-panel")).ToHaveCountAsync(0);

        await page.GetByTestId("bell").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Mark all read" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("unread-count")).ToHaveTextAsync("0");
        await page.GetByRole(AriaRole.Link, new() { Name = "See all notifications" }).ClickAsync();
        await page.WaitForURLAsync("**/notifications");
        await Assertions.Expect(page.GetByTestId("notification")).ToHaveCountAsync(2);
    }
}
