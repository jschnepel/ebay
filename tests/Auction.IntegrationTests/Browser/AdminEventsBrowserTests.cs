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
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The target account is made through the in-process API.");

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
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "Needs the in-process API, which an external stack doesn't share.");

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
    public async Task Live_mode_adds_a_new_event_without_a_reload_and_polls_once_however_often_it_is_toggled()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The new account is made through the in-process API.");

        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Live watcher", role: "Admin");
        await admin.GotoAsync("/admin/events?category=account");
        await Assertions.Expect(admin.GetByTestId("event-row").First).ToBeVisibleAsync();
        var polls = 0;
        admin.Request += (_, request) =>
        {
            // A tick's first request has no cursor; a busy tick follows it with more pages.
            if (request.Url.Contains("/admin/events?", StringComparison.Ordinal) && !request.Url.Contains("cursor=", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref polls);
            }
        };

        // Switched on three times inside one 5-second tick: one loop must be left running, not three.
        var live = admin.GetByRole(AriaRole.Button, new() { Name = "Live" });
        for (var click = 0; click < 5; click++)
        {
            await live.ClickAsync();
        }

        await Assertions.Expect(live).ToHaveAttributeAsync("aria-pressed", "true");
        var name = $"Arrives {Guid.NewGuid():N}"[..16];
        await (await db.ApiAsync()).SignUpAsync(name);

        await Assertions.Expect(admin.GetByTestId("event-row").Filter(new() { HasText = $"{name} created an account" }))
            .ToHaveCountAsync(1, new() { Timeout = 12_000 });
        await admin.WaitForTimeoutAsync(6_000);

        // Two or three ticks have passed, each starting with one request; three loops would have made three times that.
        Assert.InRange(Volatile.Read(ref polls), 1, 3);
    }

    [Fact]
    public async Task Live_mode_keeps_going_after_the_open_row_leaves_the_view()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The new accounts are made through the in-process API.");

        var token = $"Stream{Guid.NewGuid():N}"[..14];
        var api = await db.ApiAsync();
        await api.SignUpAsync($"{token} one");
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Live reader", role: "Admin");
        await admin.GotoAsync("/admin/events?category=account");
        await admin.GetByRole(AriaRole.Button, new() { Name = "Live" }).ClickAsync();

        // Open a row, then search so that row leaves the view while others stay.
        var reading = admin.GetByTestId("event-row").Filter(new() { HasText = "Live reader created an account" });
        await reading.GetByRole(AriaRole.Button, new() { Name = "Details" }).ClickAsync();
        await admin.GetByLabel("Search people, items, ids or actions").FillAsync(token);
        await admin.GetByRole(AriaRole.Button, new() { Name = "Search" }).ClickAsync();
        await Assertions.Expect(admin.GetByTestId("event-row").Filter(new() { HasText = $"{token} one created an account" })).ToHaveCountAsync(1);
        await Assertions.Expect(reading).ToHaveCountAsync(0);

        await api.SignUpAsync($"{token} two");

        await Assertions.Expect(admin.GetByTestId("event-row").Filter(new() { HasText = $"{token} two created an account" }))
            .ToHaveCountAsync(1, new() { Timeout = 12_000 });
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

        // The legend names each category with its count; this admin's own sign-up is at least one account event.
        var legend = admin.GetByRole(AriaRole.List, new() { Name = "Categories in the chart" }).GetByRole(AriaRole.Listitem);
        await Assertions.Expect(legend).ToHaveTextAsync([new Regex("^Account [1-9]"), new Regex("^Admin [0-9]"), new Regex("^Listing [0-9]"), new Regex("^Bid [0-9]")]);

        // Clicking a bar narrows the log to that bar's stretch of time.
        await admin.GetByTestId("chart-bar").Last.ClickAsync(new() { Force = true });
        await admin.WaitForURLAsync(new Regex("range=custom.*from=.*to="));
        // The range switch now shows that stretch, and the chart is redrawn for it in finer bars than 30 seconds.
        await Assertions.Expect(admin.GetByRole(AriaRole.Group, new() { Name = "Time range" }).GetByRole(AriaRole.Button, new() { Disabled = true })).ToContainTextAsync(" to ");
        await Assertions.Expect(admin.GetByRole(AriaRole.Img, new() { NameRegex = new Regex("^Events over .* to ") })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_old_audit_address_opens_the_event_log()
    {
        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Redirected admin", role: "Admin");

        await admin.GotoAsync("/admin/audit");

        await admin.WaitForURLAsync(new Regex("/admin/events\\?category=admin"));
        await Assertions.Expect(admin.GetByRole(AriaRole.Heading, new() { Name = "Event log", Level = 1 })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_users_page_shows_their_activity()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "Needs the in-process API, which an external stack doesn't share.");

        var api = await db.ApiAsync();
        var name = $"Active {Guid.NewGuid():N}"[..14];
        var user = await api.SignUpAsync(name);
        using (var signOut = await user.Http.PostAsJsonAsync("/api/v1/account/sign-out", new { }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(System.Net.HttpStatusCode.NoContent, signOut.StatusCode);
        }

        var admin = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        await admin.SignUpAsync("Watching admin", role: "Admin");
        await admin.GotoAsync($"/admin/users/{user.Id}");

        var activity = admin.GetByRole(AriaRole.Region, new() { Name = "Activity" });
        await Assertions.Expect(activity.GetByTestId("event-row").Filter(new() { HasText = $"{name} signed out" })).ToHaveCountAsync(1);
        await Assertions.Expect(activity.GetByTestId("event-row").Filter(new() { HasText = $"{name} created an account" })).ToHaveCountAsync(1);
    }
}
