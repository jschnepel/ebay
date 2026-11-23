using System.Net;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>UI-01 and UI-03 in the browser: accessibility, phone width, page states, drafts across browsers, search.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class UiQualityTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task Every_main_page_has_no_serious_or_critical_accessibility_violations()
    {
        var site = await Browsers.SiteAsync(db);
        var page = await browsers.NewPageAsync(site);
        var problems = new List<string>();

        async Task ScanAsync(string path, string readyTestIdOrText)
        {
            await page.GotoAsync(path);
            await page.GetByText(readyTestIdOrText).First.WaitForAsync();
            var results = await page.RunAxe();
            problems.AddRange(results.Violations
                .Where(v => v.Impact is "serious" or "critical")
                .Select(v => $"{path}: {v.Id} ({v.Impact}) {string.Join(" | ", v.Nodes.Take(2).Select(n => n.Html))}"));
        }

        await ScanAsync("/sign-in", "New here?");
        await page.OpenCreateAccountAsync();
        await page.GetByText("Account type").WaitForAsync();
        problems.AddRange((await page.RunAxe()).Violations
            .Where(v => v.Impact is "serious" or "critical")
            .Select(v => $"create account: {v.Id} ({v.Impact}) {string.Join(" | ", v.Nodes.Take(2).Select(n => n.Html))}"));
        await page.SignUpAsync("Accessible seller");
        var auction = await page.ListAndPublishAsync($"Axe item {Guid.NewGuid():N}"[..17], "5.00", "6.00");
        await ScanAsync("/", "Auctions");
        await ScanAsync(auction, "Bid history");
        await ScanAsync("/sell", "New listing");
        await ScanAsync("/sell/new", "Save draft");
        await ScanAsync("/my-bids", "My bids");
        await ScanAsync("/notifications", "Notifications");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task The_admin_users_user_and_event_log_pages_pass_the_accessibility_check_and_fit_a_phone()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The long-named account is made through the in-process API.");

        // The longest name and a long email, so wrapping is tested with the worst case.
        var api = await db.ApiAsync();
        var longName = $"Maximilian Bartholomew {Guid.NewGuid():N}"[..50];
        var longEmail = $"{new string('m', 60)}.{Guid.NewGuid():N}@{new string('d', 60)}.{new string('e', 60)}.example.test";
        var user = await api.SignUpAsync(longName, email: longEmail);
        var seller = await api.SignUpAsync("Quality seller");
        var listed = await seller.PublishedAsync(Api.ApiCalls.Terms(title: $"Quality lot {Guid.NewGuid():N}"[..30], bidEntry: "fixed_increment"));
        using (var bid = await user.Http.BidAsync(listed, 1000))
        {
            Assert.Equal(System.Net.HttpStatusCode.Created, bid.StatusCode);
        }

        var site = await Browsers.SiteAsync(db);
        var page = await browsers.NewPageAsync(site);
        await page.SignUpAsync("Quality admin", role: "Admin");
        var paths = new[] { $"/admin/users?q={Uri.EscapeDataString(longName)}", $"/admin/users/{user.Id}", "/admin/events?range=15m" };
        var problems = new List<string>();

        foreach (var path in paths)
        {
            await page.SetViewportSizeAsync(1280, 900);
            await page.GotoAsync(path);
            await Assertions.Expect(page.Locator("h1")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("state-loading")).ToHaveCountAsync(0);
            problems.AddRange((await page.RunAxe()).Violations
                .Where(v => v.Impact is "serious" or "critical")
                .Select(v => $"{path}: {v.Id} ({v.Impact}) {string.Join(" | ", v.Nodes.Take(2).Select(n => n.Html))}"));

            await page.SetViewportSizeAsync(360, 740);
            await page.ReloadAsync();
            await Assertions.Expect(page.Locator("h1")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("state-loading")).ToHaveCountAsync(0);
            var wide = await page.EvaluateAsync<string[]>("""
                () => {
                    const width = document.documentElement.clientWidth;
                    // Inside a box that scrolls sideways and itself fits the screen, such as the admin sections strip.
                    const inScroller = e => {
                        for (let a = e.parentElement; a && a !== document.body; a = a.parentElement) {
                            if (['auto', 'scroll'].includes(getComputedStyle(a).overflowX) && a.getBoundingClientRect().right <= width + 1) return true;
                        }
                        return false;
                    };
                    return [...document.querySelectorAll('body *')]
                    .filter(e => !e.closest('.table-scroll') && !inScroller(e) && e.getClientRects().length > 0)
                    .filter(e => e.getBoundingClientRect().right > document.documentElement.clientWidth + 1)
                    .slice(0, 3)
                    .map(e => e.tagName + '.' + e.className);
                }
                """);
            problems.AddRange(wide.Select(w => $"{path} at 360 px: {w} runs past the edge"));
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public async Task The_journey_works_at_360_px_without_sideways_scrolling()
    {
        var site = await Browsers.SiteAsync(db);
        var seller = await browsers.NewPageAsync(site);
        var buyer = await browsers.NewPageAsync(site);
        await seller.SetViewportSizeAsync(360, 740);
        await buyer.SetViewportSizeAsync(360, 740);
        await seller.SignUpAsync("Phone seller");
        var auction = await seller.ListAndPublishAsync($"Phone item {Guid.NewGuid():N}"[..19], "5.00", "6.00");
        await buyer.SignUpAsync("Phone buyer");

        foreach (var (page, path) in new[] { (buyer, "/"), (buyer, auction), (seller, "/sell"), (seller, "/sell/new"), (buyer, "/my-bids") })
        {
            await page.GotoAsync(path);
            await Assertions.Expect(page.Locator("h1")).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByTestId("state-loading")).ToHaveCountAsync(0);
            if (path == "/")
            {
                await Assertions.Expect(page.GetByTestId("auction-row").First).ToBeVisibleAsync();
            }
            // Nothing may run past the screen's edge, except inside a box that scrolls on its own.
            var wide = await page.EvaluateAsync<string[]>("""
                () => [...document.querySelectorAll('body *')]
                    .filter(e => !e.closest('.table-scroll') && e.getClientRects().length > 0)
                    .filter(e => e.getBoundingClientRect().right > document.documentElement.clientWidth + 1)
                    .slice(0, 3)
                    .map(e => e.tagName + '.' + e.className)
                """);
            Assert.True(wide.Length == 0, $"{path} at 360 px: {string.Join(", ", wide)} run past the edge");
        }

        await buyer.GotoAsync(auction);
        await buyer.BidAsync("8.00");
        await Assertions.Expect(buyer.GetByTestId("bid-result")).ToContainTextAsync("highest bidder");
    }

    [Fact]
    public async Task Views_show_their_loading_error_empty_and_signed_out_states()
    {
        var site = await Browsers.SiteAsync(db);
        var page = await browsers.NewPageAsync(site);

        // Loading: the directory's answer is held back.
        using var release = new SemaphoreSlim(0);
        await page.RouteAsync("**/api/v1/auctions*", async route =>
        {
            await release.WaitAsync(TestContext.Current.CancellationToken);
            await route.ContinueAsync();
        });
        await page.GotoAsync("/");
        await Assertions.Expect(page.GetByTestId("state-loading")).ToBeVisibleAsync();
        release.Release(int.MaxValue / 2);
        await page.UnrouteAllAsync(new() { Behavior = UnrouteBehavior.IgnoreErrors });

        // Error, then a retry that succeeds.
        await page.RouteAsync("**/api/v1/auctions*", route => route.FulfillAsync(new()
        {
            Status = 500,
            ContentType = "application/problem+json",
            Body = """{"status":500,"title":"The server had a problem."}""",
        }));
        await page.GotoAsync("/");
        await Assertions.Expect(page.GetByTestId("state-error")).ToContainTextAsync("The server had a problem.");
        await page.UnrouteAllAsync(new() { Behavior = UnrouteBehavior.IgnoreErrors });
        await page.GetByRole(AriaRole.Button, new() { Name = "Try again" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("state-error")).ToHaveCountAsync(0);

        // Empty: a search nothing matches.
        await page.GetByLabel("Search titles").FillAsync($"nothing-{Guid.NewGuid():N}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("state-empty")).ToContainTextAsync("No auctions match");

        // Signed out: the personal views offer to sign in.
        foreach (var path in new[] { "/my-bids", "/sell", "/notifications" })
        {
            await page.GotoAsync(path);
            await Assertions.Expect(page.GetByTestId("state-forbidden").GetByRole(AriaRole.Link, new() { Name = "Sign in" })).ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task A_saved_draft_follows_its_seller_to_another_browser_until_deleted_and_nobody_else_can_open_it()
    {
        var site = await Browsers.SiteAsync(db);
        var first = await browsers.NewPageAsync(site);
        var email = await first.SignUpAsync("Draft keeper");
        var title = $"Kept draft {Guid.NewGuid():N}"[..19];
        await first.GotoAsync("/sell/new");
        await first.GetByLabel("Title").FillAsync(title);
        await first.GetByLabel("Opening price ($)").FillAsync("5.00");
        await first.GetByLabel("Reserve ($)").FillAsync("6.00");
        await first.GetByRole(AriaRole.Button, new() { Name = "Save draft" }).ClickAsync();
        await first.WaitForURLAsync("**/sell");
        var draftLink = await first.GetByTestId("my-auction-row").Filter(new() { HasText = title }).GetByRole(AriaRole.Link, new() { Name = title }).GetAttributeAsync("href");

        var second = await browsers.NewPageAsync(site);
        await second.GotoAsync("/sign-in");
        await second.GetByLabel("Email", new() { Exact = true }).FillAsync(email);
        await second.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Assertions.Expect(second.GetByTestId("signed-in-as")).ToHaveTextAsync("Draft keeper");
        await second.GotoAsync("/sell");
        var row = second.GetByTestId("my-auction-row").Filter(new() { HasText = title });
        await Assertions.Expect(row).ToContainTextAsync("Draft");

        var stranger = await browsers.NewPageAsync(site);
        await stranger.SignUpAsync("Stranger");
        await stranger.GotoAsync(draftLink!);
        await Assertions.Expect(stranger.GetByTestId("state-error")).ToContainTextAsync("doesn't exist"); // control

        await row.GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
        await Assertions.Expect(second.GetByTestId("sell-result")).ToContainTextAsync("Deleted");
        await Assertions.Expect(row).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task A_buyer_searches_a_keyword_and_filters_by_status()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The auctions are created through the in-process API.");

        var site = await Browsers.SiteAsync(db);
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var word = $"kw{Guid.NewGuid():N}"[..10];
        await seller.PublishedAsync(ApiCalls.Terms(title: $"Lamp {word}"));
        await seller.PublishedAsync(ApiCalls.Terms(title: $"Chair {word}"));
        await seller.PublishedAsync(ApiCalls.Terms(title: "Something else entirely"));
        var page = await browsers.NewPageAsync(site);

        await page.GotoAsync("/");
        await page.GetByLabel("Search titles").FillAsync(word);
        await page.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();

        await Assertions.Expect(page.GetByTestId("auction-row")).ToHaveCountAsync(2);
        await Assertions.Expect(page.GetByTestId("auction-row").Filter(new() { HasText = "Something else" })).ToHaveCountAsync(0);
        await page.GetByLabel("Show").ClickAsync();
        await page.GetByRole(AriaRole.Option, new() { Name = "Closed" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("state-empty")).ToBeVisibleAsync();
        Assert.Equal(HttpStatusCode.OK, await api.Anonymous().StatusOfGetAsync($"/api/v1/auctions?q={word}&status=active")); // control
    }
}
