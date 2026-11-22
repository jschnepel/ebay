using Auction.IntegrationTests.Browser;
using Microsoft.Playwright;

namespace Auction.StackTests;

/// <summary>
/// People using the site in a real browser against the containers: the client, the API, the engine over
/// gRPC, PostgreSQL, the outbox relay and the Redis backplane, and the simulator's control service.
/// </summary>
[Collection(StackRuns.Name)]
public sealed class BrowserTests(StackUnderTest stack)
{
    // The requirement's bound for an open browser to show a new price (A5).
    private static readonly LocatorAssertionsToHaveTextOptions WithinBound = new() { Timeout = 2_000 };

    [Fact]
    public async Task The_live_marketplace_has_photographed_listings_within_two_minutes_and_their_photos_load()
    {
        using var anonymous = stack.Client();
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var (active, photographed) = (0, 0);
        while (DateTimeOffset.UtcNow < deadline)
        {
            (active, photographed) = await ActiveListingsAsync(anonymous);
            if (photographed >= 12)
            {
                break;
            }

            await Task.Delay(5_000, TestContext.Current.CancellationToken);
        }

        Assert.True(photographed >= 12, $"{photographed} photographed of {active} active listings after two minutes; the live marketplace says {await LiveStatusAsync()}");
        var visitor = await stack.NewPageAsync();
        await visitor.GotoAsync("/");
        await Assertions.Expect(visitor.GetByTestId("state-loading")).ToHaveCountAsync(0);
        // Covers load after the list renders, so wait for one to finish rather than reading the page at once.
        await visitor.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('img[data-testid=cover]')].some(i => i.complete && i.naturalWidth > 0)",
            null,
            new() { Timeout = 30_000 });
    }

    // Every active auction through the public directory, and how many have a cover photo.
    private static async Task<(int Active, int Photographed)> ActiveListingsAsync(HttpClient http)
    {
        var (active, photographed) = (0, 0);
        string? cursor = null;
        do
        {
            var url = "/api/v1/auctions?status=active&pageSize=50" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var page = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken));
            foreach (var item in page.RootElement.GetProperty("items").EnumerateArray())
            {
                active++;
                photographed += item.TryGetProperty("coverImageId", out var cover) && cover.ValueKind == System.Text.Json.JsonValueKind.String ? 1 : 0;
            }

            cursor = page.RootElement.TryGetProperty("nextCursor", out var next) && next.ValueKind == System.Text.Json.JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);

        return (active, photographed);
    }

    // The admin area's view of the live marketplace, for a failure message.
    private async Task<string> LiveStatusAsync()
    {
        using var admin = stack.Client();
        admin.DefaultRequestHeaders.Add("Origin", stack.Site.GetLeftPart(UriPartial.Authority));
        using var signIn = await admin.PostAsync(
            new Uri("/api/v1/account/sign-in", UriKind.Relative),
            System.Net.Http.Json.JsonContent.Create(new { email = StackUnderTest.AdminEmail, password = StackUnderTest.AdminPassword }),
            TestContext.Current.CancellationToken);
        return await admin.GetStringAsync(new Uri("/api/v1/admin/simulator", UriKind.Relative), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_seller_lists_two_buyers_bid_and_every_open_browser_follows_the_price_without_a_reload()
    {
        var seller = await stack.NewPageAsync();
        var first = await stack.NewPageAsync();
        var second = await stack.NewPageAsync();
        var title = $"Stack lamp {Guid.NewGuid():N}"[..19];

        await seller.SignUpAsync("Seller");
        var auction = await seller.ListAndPublishAsync(title, openingDollars: "10.00", reserveDollars: "12.00");
        await seller.GotoAsync(auction);
        await first.SignUpAsync("First buyer");
        await first.GotoAsync(auction);
        await second.SignUpAsync("Second buyer");
        await second.GotoAsync(auction);
        await Assertions.Expect(first.GetByTestId("current-price")).ToHaveTextAsync("$10.00 (opening)");

        await first.BidAsync("20.00");
        await Assertions.Expect(first.GetByTestId("bid-result")).ToContainTextAsync("You are the highest bidder");
        foreach (var page in new[] { seller, second })
        {
            await Assertions.Expect(page.GetByTestId("current-price")).ToHaveTextAsync("$12.00", WithinBound);
        }

        await second.BidAsync("30.00");
        foreach (var page in new[] { seller, first })
        {
            await Assertions.Expect(page.GetByTestId("current-price")).ToHaveTextAsync("$21.00", WithinBound);
        }

        await second.GotoAsync("/my-bids");
        await Assertions.Expect(second.GetByTestId("my-bid-row").Filter(new() { HasText = title })).ToContainTextAsync("Leading");
        await first.GotoAsync("/");
        await Assertions.Expect(await first.DirectoryRowAsync(title)).ToContainTextAsync("$21.00");
    }

    [Fact]
    public async Task The_seeded_admin_suspends_an_auction_with_a_reason_and_the_buyer_watching_it_sees_it_stop()
    {
        var seller = await stack.SignUpAsync("Seller");
        var title = $"Moderated {Guid.NewGuid():N}"[..19];
        var auction = await seller.PublishAsync(title);
        var watcher = await stack.NewPageAsync();
        await watcher.GotoAsync($"/auctions/{auction}");
        await Assertions.Expect(watcher.GetByTestId("status")).ToHaveTextAsync("Active");
        var admin = await SignedInAdminAsync();
        var reason = $"Checking the photos {Guid.NewGuid():N}"[..28];

        await admin.GotoAsync("/admin/auctions");
        await admin.GetByLabel("Title, seller name or id").FillAsync(title);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await admin.GetByLabel("Reason (sent to the seller and bidders, kept in the audit log)").FillAsync(reason);
        await admin.GetByTestId("moderated-auction").Filter(new() { HasText = title }).GetByRole(AriaRole.Button, new() { Name = "Suspend" }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("moderation-result")).ToContainTextAsync("Done");

        await Assertions.Expect(watcher.GetByTestId("status")).ToHaveTextAsync("Suspended", WithinBound);

        await admin.GetByRole(AriaRole.Link, new() { Name = "Audit log" }).ClickAsync();
        await admin.GetByLabel("Admin name, admin or target id, or action").FillAsync(auction.ToString());
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("audit-row")).ToHaveCountAsync(1);
        await Assertions.Expect(admin.GetByTestId("audit-row")).ToContainTextAsync("suspend");
        await Assertions.Expect(admin.GetByTestId("audit-row")).ToContainTextAsync(reason);
    }

    [Fact]
    public async Task The_seeded_admin_runs_the_smoke_profile_and_the_verifier_finds_every_invariant_held()
    {
        var admin = await SignedInAdminAsync();
        await admin.GotoAsync("/admin");
        await Assertions.Expect(admin.GetByTestId("simulator-state")).ToHaveTextAsync("Idle.");

        await admin.GetByRole(AriaRole.Button, new() { Name = "Start run" }).ClickAsync();

        await Assertions.Expect(admin.GetByTestId("simulator-state")).ToContainTextAsync("Running smoke");
        await Assertions.Expect(admin.GetByTestId("recent-bid").Filter(new() { HasText = "Simulated item" }).First).ToBeVisibleAsync();
        // The run bids for its set time, waits for its auctions to close, then the verifier checks every invariant.
        await Assertions.Expect(admin.GetByTestId("simulator-last")).ToContainTextAsync("every invariant held", new() { Timeout = 240_000 });
        await Assertions.Expect(admin.GetByTestId("simulator-state")).ToHaveTextAsync("Idle.");
    }

    private async Task<IPage> SignedInAdminAsync()
    {
        var admin = await stack.NewPageAsync();
        await admin.GotoAsync("/sign-in");
        await admin.GetByLabel("Email", new() { Exact = true }).FillAsync(StackUnderTest.AdminEmail);
        await admin.GetByLabel("Password (built-in demo accounts only)").FillAsync(StackUnderTest.AdminPassword);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("signed-in-as")).ToHaveTextAsync("Admin");
        await Assertions.Expect(admin.GetByRole(AriaRole.Link, new() { Name = "Sell" })).ToHaveCountAsync(0);
        return admin;
    }
}
