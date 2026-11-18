using System.Net;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>The Admin area in a browser (UI-04, ADM-01, INT-02's fourth interface).</summary>
public sealed class AdminBrowserTests(MigratedDatabase db, Browsers browsers)
{
    private const string Password = "admin-browser-password";

    [Fact]
    public async Task An_admin_sees_new_bids_without_a_reload_and_a_suspension_reaches_an_open_auction_page()
    {
        if (Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 })
        {
            // The admin account is created in the test database, which an external stack doesn't share.
            return;
        }

        var site = await Browsers.SiteAsync(db);
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var title = $"Admin watch {Guid.NewGuid():N}"[..20];
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: title));

        var admin = await browsers.NewPageAsync(site);
        await admin.GotoAsync("/sign-in");
        await admin.GetByLabel("Email", new() { Exact = true }).FillAsync(await db.InsertPasswordAccountAsync("admin", Password));
        await admin.GetByLabel("Password (seeded accounts only)").FillAsync(Password);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
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
    }
}
