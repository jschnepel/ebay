using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>The admin event log in a browser: sentences that name people, rows that open, filters kept in the address, and the old audit address.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed partial class AdminEventsBrowserTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task The_event_log_shows_a_disable_with_both_names_and_its_reason()
    {
        if (Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 })
        {
            return; // The target account is made through the in-process API.
        }

        var name = $"Logged {Guid.NewGuid():N}"[..14];
        var target = await (await db.ApiAsync()).SignUpAsync(name);
        var adminName = $"Logging {Guid.NewGuid():N}"[..15];
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync(adminName, role: "Admin");
        await admin.GotoAsync("/admin/users");
        await admin.GetByLabel("Name, email or id").FillAsync(name);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await admin.GetByTestId("user-row").Filter(new() { HasText = name }).GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();
        await admin.GetByRole(AriaRole.Dialog).GetByLabel("Reason").FillAsync("Spam listings");
        await admin.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("user-row").Filter(new() { HasText = name })).ToContainTextAsync("Disabled");

        await admin.GetByRole(AriaRole.Link, new() { Name = "Event log" }).ClickAsync();
        var row = admin.GetByTestId("event-row").Filter(new() { HasText = $"{adminName} disabled {name}: \"Spam listings\"" });
        await Assertions.Expect(row).ToHaveCountAsync(1);
        await Assertions.Expect(row).ToContainTextAsync("Warning");

        await row.GetByRole(AriaRole.Button, new() { Name = "Details" }).ClickAsync();
        await Assertions.Expect(row).ToContainTextAsync(target.Email);
        await Assertions.Expect(row.GetByRole(AriaRole.Link, new() { Name = name })).ToHaveAttributeAsync("href", $"admin/users/{target.Id}");
    }

    [Fact]
    public async Task The_event_log_filters_by_category_and_keeps_its_view_in_the_address()
    {
        if (Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 })
        {
            return;
        }

        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync("Event seller");
        var buyer = await api.SignUpAsync("Event buyer");
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: "Event lot", bidEntry: "fixed_increment"));
        using (var bid = await buyer.Http.BidAsync(id, 1000))
        {
            Assert.Equal(System.Net.HttpStatusCode.Created, bid.StatusCode);
        }

        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Filtering admin", role: "Admin");
        await admin.GotoAsync("/admin/events");
        await Assertions.Expect(admin.GetByTestId("event-row").First).ToBeVisibleAsync();

        await admin.GetByRole(AriaRole.Group, new() { Name = "Categories" }).GetByRole(AriaRole.Button, new() { Name = "Bids" }).ClickAsync();

        await Assertions.Expect(admin.GetByTestId("event-row").Filter(new() { HasText = "Event buyer bid $10.00 on Event lot" })).ToHaveCountAsync(1);
        await Assertions.Expect(admin.GetByTestId("event-category").Filter(new() { HasNotText = "Bid" })).ToHaveCountAsync(0);
        await admin.WaitForURLAsync(new Regex("category=bid"));
        Assert.Contains("range=1h", admin.Url, StringComparison.Ordinal);

        await admin.ReloadAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Group, new() { Name = "Categories" }).GetByRole(AriaRole.Button, new() { Name = "Bids" }))
            .ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(admin.GetByTestId("event-category").Filter(new() { HasNotText = "Bid" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_event_log_draws_a_chart_of_events_over_time()
    {
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Charting admin", role: "Admin");

        await admin.GotoAsync("/admin/events?range=15m");

        var chart = admin.GetByRole(AriaRole.Img, new() { NameRegex = new Regex("^Events over the last 15 minutes") });
        await Assertions.Expect(chart).ToBeVisibleAsync();
        await Assertions.Expect(admin.GetByTestId("chart-bar")).ToHaveCountAsync(30);
    }

    [Fact]
    public async Task The_old_audit_address_opens_the_event_log()
    {
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Redirected admin", role: "Admin");

        await admin.GotoAsync("/admin/audit");

        await admin.WaitForURLAsync(new Regex("/admin/events"));
        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Event log", Level = 1 })).ToBeVisibleAsync();
    }
}
