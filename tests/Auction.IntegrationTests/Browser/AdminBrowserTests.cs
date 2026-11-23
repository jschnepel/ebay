using System.Net;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Auction.IntegrationTests.Simulation;
using Auction.Migrator;
using Auction.Simulator.Control;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>The Admin area in a browser (UI-04, ADM-01, ADM-02, ADM-03, INT-02's fourth interface).</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class AdminBrowserTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task A_visitor_creating_an_admin_account_lands_in_the_admin_area_without_buy_or_sell()
    {
        var site = await Browsers.SiteAsync(db);
        var page = await browsers.NewPageAsync(site);

        await page.SignUpAsync("Visiting admin", role: "Admin");

        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/admin$"));
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Overview" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Sell" })).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "My bids" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task An_admin_asks_for_a_demo_reset_only_after_confirming()
    {
        await db.ExecuteAsync(MigratedDatabase.Migrator, "DELETE FROM demo_resets");
        var site = await Browsers.SiteAsync(db);
        var page = await browsers.NewPageAsync(site);
        await page.SignUpAsync("Resetting admin", role: "Admin");
        await page.WaitForURLAsync(new System.Text.RegularExpressions.Regex("/admin$"));
        var panel = page.GetByTestId("demo-reset-panel");
        await Assertions.Expect(panel).ToContainTextAsync("every day");

        await panel.GetByRole(AriaRole.Button, new() { Name = "Reset demo now" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        Assert.Equal(0L, await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM demo_resets"));

        await panel.GetByRole(AriaRole.Button, new() { Name = "Reset demo now" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Reset the demo now?")).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Reset demo", Exact = true }).ClickAsync();

        await Assertions.Expect(page.GetByTestId("demo-reset-state")).ToContainTextAsync("Reset requested");
        Assert.Equal("Resetting admin", await db.ScalarAsync<string>(MigratedDatabase.Migrator, "SELECT requested_by FROM demo_resets WHERE completed_at IS NULL"));
        await db.ExecuteAsync(MigratedDatabase.Migrator, "DELETE FROM demo_resets");
    }

    [Fact]
    public async Task A_visitor_creating_a_user_account_sees_no_admin_link()
    {
        var site = await Browsers.SiteAsync(db);
        var page = await browsers.NewPageAsync(site);

        await page.SignUpAsync("Visiting user");

        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Sell" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Overview" })).ToHaveCountAsync(0);
        await page.GotoAsync("/admin");
        await Assertions.Expect(page.GetByTestId("state-forbidden")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task An_admin_sees_new_bids_without_a_reload_and_a_suspension_reaches_an_open_auction_page()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The admin account is created in the test database, which an external stack doesn't share.");

        var site = await Browsers.SiteAsync(db);
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var title = $"Admin watch {Guid.NewGuid():N}"[..20];
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: title));

        var admin = await browsers.NewPageAsync(site);
        await admin.SignUpAsync("Watching admin", role: "Admin");
        await Assertions.Expect(admin.GetByRole(AriaRole.Link, new() { Name = "Sell" })).ToHaveCountAsync(0);
        await admin.GotoAsync("/admin");
        await Assertions.Expect(admin.GetByTestId("kpi-row")).ToHaveCountAsync(4);

        using (var bid = await buyer.Http.BidAsync(id, 1500))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        await Assertions.Expect(admin.GetByTestId("recent-bid").Filter(new() { HasText = title }).First).ToBeVisibleAsync(new() { Timeout = 2_000 });

        var watcher = await browsers.NewPageAsync(site);
        await watcher.GotoAsync($"/auctions/{id}");
        await Assertions.Expect(watcher.GetByTestId("status")).ToHaveTextAsync("Active");

        await admin.GotoAsync("/admin/auctions");
        await admin.GetByLabel("Title, seller name or id").FillAsync(title);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await admin.GetByLabel("Reason (sent to the seller and bidders, kept in the audit log)").FillAsync("Checking the photos");
        await admin.GetByTestId("moderated-auction").Filter(new() { HasText = title }).GetByRole(AriaRole.Button, new() { Name = "Suspend" }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("moderation-result")).ToContainTextAsync("Done");

        await Assertions.Expect(watcher.GetByTestId("status")).ToHaveTextAsync("Suspended", new() { Timeout = 2_000 });

        await admin.GetByRole(AriaRole.Link, new() { Name = "Event log" }).ClickAsync();
        await admin.GetByRole(AriaRole.Group, new() { Name = "Categories" }).GetByRole(AriaRole.Button, new() { Name = "Admin" }).ClickAsync();
        await admin.GetByLabel("Search people, items, ids or actions").FillAsync(id.ToString());
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("event-row")).ToHaveCountAsync(1);
        await Assertions.Expect(admin.GetByTestId("event-row")).ToContainTextAsync("suspended");
        await Assertions.Expect(admin.GetByTestId("event-row")).ToContainTextAsync("Checking the photos");
    }

    [Fact]
    public async Task An_admin_starts_the_smoke_profile_watches_simulated_bids_arrive_and_stops_it()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "Needs the in-process API, which an external stack doesn't share.");

        // The same simulator password as the simulator tests: seeding never changes an existing account's password.
        const string simulatorPassword = "simulator-pass-for-tests";
        const string token = "control-token-for-browser-tests";
        await DemoSeed.RunAsync(
            db.ConnectionStringFor(MigratedDatabase.Migrator),
            new DemoSeedOptions("admin-pass-for-tests", "demo-pass-for-tests", simulatorPassword),
            TestContext.Current.CancellationToken);
        await using var control = await ControlHost.StartAsync(
            new ControlSettings(await Browsers.SiteAsync(db), simulatorPassword, token, db.ConnectionStringFor(MigratedDatabase.Verifier)));
        var (host, site) = await db.SiteWithAsync(new Dictionary<string, string>
        {
            ["Api:SimulatorAddress"] = control.Address.ToString(),
            ["Api:SimulatorToken"] = token,
        });
        await using var _ = host;

        var admin = await browsers.NewPageAsync(site);
        await admin.SignUpAsync("Watching admin", role: "Admin");
        await Assertions.Expect(admin.GetByRole(AriaRole.Link, new() { Name = "Sell" })).ToHaveCountAsync(0);
        await admin.GotoAsync("/admin");
        await Assertions.Expect(admin.GetByTestId("simulator-state")).ToHaveTextAsync("Idle.");

        await admin.GetByRole(AriaRole.Button, new() { Name = "Start run" }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("simulator-state")).ToContainTextAsync("Running smoke");
        await Assertions.Expect(admin.GetByTestId("recent-bid").Filter(new() { HasText = "Simulated item" }).First)
            .ToBeVisibleAsync(new() { Timeout = 20_000 });

        await admin.GetByRole(AriaRole.Button, new() { Name = "Stop run" }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("simulator-last")).ToContainTextAsync("(stopped early)", new() { Timeout = 30_000 });
        await Assertions.Expect(admin.GetByTestId("simulator-state")).ToHaveTextAsync("Idle.");
    }
}
