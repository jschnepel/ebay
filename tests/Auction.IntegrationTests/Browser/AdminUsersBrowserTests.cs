using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Browser;

/// <summary>The admin Users screen in a browser: the reason popover beside each user, filters, paging, and the way to a user's page.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed partial class AdminUsersBrowserTests(MigratedDatabase db, Browsers browsers)
{
    [Fact]
    public async Task Disabling_a_user_asks_for_the_reason_beside_their_row()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The target account is made through the in-process API.");

        var name = $"Target {Guid.NewGuid():N}"[..14];
        await (await db.ApiAsync()).SignUpAsync(name);
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Popover admin", role: "Admin");

        await admin.GotoAsync("/admin/users");
        await admin.GetByLabel("Name, email or id").FillAsync(name);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        var row = admin.GetByTestId("user-row").Filter(new() { HasText = name });
        await Assertions.Expect(row).ToHaveCountAsync(1);
        await Assertions.Expect(admin.GetByLabel("Reason")).ToHaveCountAsync(0);

        await row.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();
        var popover = admin.GetByRole(AriaRole.Dialog, new() { Name = $"Disable {name}?" });
        await Assertions.Expect(popover).ToBeVisibleAsync();
        await Assertions.Expect(popover.GetByLabel("Reason")).ToBeFocusedAsync();
        await Assertions.Expect(popover.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true })).ToBeDisabledAsync();
        await popover.GetByLabel("Reason").FillAsync("Spam listings");
        await popover.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();

        await Assertions.Expect(popover).ToHaveCountAsync(0);
        await Assertions.Expect(row).ToContainTextAsync("Disabled");
        await Assertions.Expect(row.GetByRole(AriaRole.Button, new() { Name = "Enable", Exact = true })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_popover_closes_on_escape_without_changing_anything()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "Needs the in-process API, which an external stack doesn't share.");

        var name = $"Kept {Guid.NewGuid():N}"[..12];
        await (await db.ApiAsync()).SignUpAsync(name);
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Escaping admin", role: "Admin");
        await admin.GotoAsync("/admin/users");
        await admin.GetByLabel("Name, email or id").FillAsync(name);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        var row = admin.GetByTestId("user-row").Filter(new() { HasText = name });

        await row.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();
        await admin.Keyboard.PressAsync("Escape");

        await Assertions.Expect(admin.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        await Assertions.Expect(row).ToContainTextAsync("Enabled");
    }

    [Fact]
    public async Task Enter_on_cancel_cancels_and_enter_in_the_field_confirms_once()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "Needs the in-process API, which an external stack doesn't share.");

        var name = $"Keyed {Guid.NewGuid():N}"[..13];
        var target = await (await db.ApiAsync()).SignUpAsync(name);
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Keyboard admin", role: "Admin");
        await admin.GotoAsync("/admin/users");
        await admin.GetByLabel("Name, email or id").FillAsync(name);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        var row = admin.GetByTestId("user-row").Filter(new() { HasText = name });

        await row.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();
        await admin.GetByRole(AriaRole.Dialog).GetByLabel("Reason").FillAsync("Changed my mind");
        await admin.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancel" }).FocusAsync();
        await admin.Keyboard.PressAsync("Enter");

        await Assertions.Expect(admin.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0);
        await Assertions.Expect(row).ToContainTextAsync("Enabled");

        await row.GetByRole(AriaRole.Button, new() { Name = "Disable", Exact = true }).ClickAsync();
        await admin.GetByRole(AriaRole.Dialog).GetByLabel("Reason").FillAsync("Spam");
        await admin.Keyboard.PressAsync("Enter");

        await Assertions.Expect(row).ToContainTextAsync("Disabled");
        var disables = await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM admin_audit WHERE action = 'disable' AND target_id = $1", target.Id);
        Assert.Equal(1, disables);

        // Enter on the action's own button is a key press and a click at once; it still acts once.
        await row.GetByRole(AriaRole.Button, new() { Name = "Enable", Exact = true }).ClickAsync();
        await admin.GetByRole(AriaRole.Dialog).GetByLabel("Reason").FillAsync("Sorted out");
        await admin.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Enable", Exact = true }).FocusAsync();
        await admin.Keyboard.PressAsync("Enter");

        await Assertions.Expect(row).ToContainTextAsync("Enabled");
        Assert.Equal(1, await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM admin_audit WHERE action = 'enable' AND target_id = $1", target.Id));
    }

    [Fact]
    public async Task The_users_screen_pages_and_filters_by_state()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "Needs the in-process API, which an external stack doesn't share.");

        var api = await db.ApiAsync();
        for (var i = 0; i < 26; i++)
        {
            await api.SignUpAsync($"Paged user {i}");
        }

        var disabledName = $"Off {Guid.NewGuid():N}"[..11];
        var disabled = await api.SignUpAsync(disabledName);
        var apiAdmin = await api.SignUpAsync("Api admin", role: "admin");
        using (var disable = await apiAdmin.Http.PostAsJsonAsync($"/api/v1/admin/users/{disabled.Id}/disable", new { reason = "test" }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.NoContent, disable.StatusCode);
        }

        // The production page limit, so a page of 25 isn't cut to the test host's small default.
        var (host, site) = await db.SiteWithAsync(new Dictionary<string, string> { ["Api:MaxPageSize"] = "50" });
        await using var _ = host;
        var admin = await browsers.NewPageAsync(site);
        await admin.SignUpAsync("Paging admin", role: "Admin");
        await admin.GotoAsync("/admin/users");
        await Assertions.Expect(admin.GetByTestId("user-row")).ToHaveCountAsync(25);

        await admin.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("users-page")).ToHaveTextAsync("Page 2");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("users-page")).ToHaveTextAsync("Page 1");

        await admin.GetByRole(AriaRole.Group, new() { Name = "State" }).GetByRole(AriaRole.Button, new() { Name = "Disabled", Exact = true }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("user-row").Filter(new() { HasText = disabledName })).ToHaveCountAsync(1);
        await Assertions.Expect(admin.GetByTestId("user-row").Filter(new() { HasText = "Enabled" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Clicking_a_user_opens_their_page()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "Needs the in-process API, which an external stack doesn't share.");

        var name = $"Opened {Guid.NewGuid():N}"[..14];
        var user = await (await db.ApiAsync()).SignUpAsync(name);
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Opening admin", role: "Admin");
        await admin.GotoAsync("/admin/users");
        await admin.GetByLabel("Name, email or id").FillAsync(user.Email);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();

        await admin.GetByTestId("user-row").Filter(new() { HasText = name }).GetByRole(AriaRole.Link, new() { Name = name }).ClickAsync();

        await admin.WaitForURLAsync(UserPage());
        Assert.EndsWith($"/admin/users/{user.Id}", admin.Url, StringComparison.Ordinal);
        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = name, Level = 1 })).ToBeVisibleAsync();

        // Anywhere else on the row opens it too.
        await admin.GotoAsync("/admin/users");
        await admin.GetByLabel("Name, email or id").FillAsync(user.Email);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await admin.GetByTestId("user-row").Filter(new() { HasText = name }).GetByText("Enabled").ClickAsync();
        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = name, Level = 1 })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_users_page_shows_who_they_are_and_their_selling_and_bidding()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "Needs the in-process API, which an external stack doesn't share.");

        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync("Paged seller");
        var buyerName = $"Bidder {Guid.NewGuid():N}"[..14];
        var buyer = await api.SignUpAsync(buyerName);
        var title = $"Watched lot {Guid.NewGuid():N}"[..18];
        var id = await seller.PublishedAsync(ApiCalls.Terms(title: title, bidEntry: "fixed_increment"));
        using (var bid = await buyer.Http.BidAsync(id, 1000))
        {
            Assert.Equal(System.Net.HttpStatusCode.Created, bid.StatusCode);
        }

        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Looking admin", role: "Admin");

        await admin.GotoAsync($"/admin/users/{buyer.Id}");
        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = buyerName, Level = 1 })).ToBeVisibleAsync();
        await Assertions.Expect(admin.GetByTestId("user-email")).ToHaveTextAsync(buyer.Email);
        await Assertions.Expect(admin.GetByTestId("user-stat-bids")).ToContainTextAsync("1");
        var bidding = admin.GetByTestId("bidding-row").Filter(new() { HasText = title });
        await Assertions.Expect(bidding).ToContainTextAsync("Leading");
        await Assertions.Expect(bidding).ToContainTextAsync("$10.00");

        await admin.GotoAsync($"/admin/users/{seller.Id}");
        var selling = admin.GetByTestId("selling-row").Filter(new() { HasText = title });
        await Assertions.Expect(selling).ToContainTextAsync("Active");
        // The log's longest range, so the link finds bids older than the default hour.
        await Assertions.Expect(selling.GetByRole(AriaRole.Link, new() { Name = "1 bid" }))
            .ToHaveAttributeAsync("href", $"admin/events?range=7d&category=bid&q={id}");
    }

    [Fact]
    public async Task A_users_page_says_when_their_listings_couldnt_be_loaded()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The user is made through the in-process API.");

        var user = await (await db.ApiAsync()).SignUpAsync("Half loaded");
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Half seeing", role: "Admin");
        await admin.RouteAsync("**/admin/users/*/listings*", route => route.FulfillAsync(new() { Status = 500 }));

        await admin.GotoAsync($"/admin/users/{user.Id}");

        // A failed request is an error on the page, not an account with nothing listed.
        await Assertions.Expect(admin.GetByTestId("user-result")).ToContainTextAsync("The request failed (500).");
    }

    [Fact]
    public async Task An_unknown_users_page_says_so()
    {
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Lost admin", role: "Admin");

        await admin.GotoAsync($"/admin/users/{Guid.NewGuid()}");

        await Assertions.Expect(admin.GetByTestId("state-error")).ToContainTextAsync("No such user");
    }

    [GeneratedRegex("/admin/users/[0-9a-f-]{36}$")]
    private static partial Regex UserPage();
}
