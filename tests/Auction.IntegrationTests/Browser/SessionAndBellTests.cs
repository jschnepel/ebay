using System.Net;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task A_page_whose_session_a_reset_removed_shows_it_is_signed_out_instead_of_unauthorized()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "Ends the session through the shared database and Redis, which an external stack doesn't share.");

        var site = await Browsers.SiteAsync(db);
        var page = await browsers.NewPageAsync(site);
        var email = await page.SignUpAsync("Reset sleeper");
        await page.GotoAsync("/");
        await Assertions.Expect(page.GetByTestId("signed-in-as")).ToHaveTextAsync("Reset sleeper");

        // The daily reset ends sessions in the database and clears Redis, and tells no open page.
        var redis = (await db.ApiAsync()).Services.GetRequiredService<Auction.Api.Caching.RedisJobs>();
        var sessions = await db.ScalarAsync<Guid[]>(
            MigratedDatabase.Migrator,
            """
            WITH ended AS (
                UPDATE sessions SET revoked_at = now() WHERE user_id = (SELECT id FROM users WHERE email = $1) AND revoked_at IS NULL RETURNING id)
            SELECT coalesce(array_agg(id), '{}') FROM ended
            """,
            email);
        foreach (var session in sessions!)
        {
            await redis.TryAsync(r => r.KeyDeleteAsync($"session:{session:N}"));
        }

        await page.GetByRole(AriaRole.Link, new() { Name = "My bids" }).ClickAsync();

        await Assertions.Expect(page.GetByText("Sign in to see your bids.")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("signed-out-notice")).ToContainTextAsync("You've been signed out");
        await Assertions.Expect(page.GetByTestId("signed-in-as")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByText("Unauthorized")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task An_outbid_bidder_sees_the_bell_count_within_two_seconds_and_opening_it_clears_the_count()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The other users are created through the in-process API, which an external stack doesn't share.");

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
    public async Task The_unread_badge_shows_its_number_on_the_bells_corner_inside_the_bar()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable(Browsers.BaseUrlVariable) is { Length: > 0 }, "The notices are written straight to the test database.");

        var page = await browsers.NewPageAsync(await Browsers.SiteAsync(db));
        var email = await page.SignUpAsync("Badge reader");
        var user = await db.ScalarAsync<Guid>(MigratedDatabase.Migrator, "SELECT id FROM users WHERE normalized_email = upper($1)", email);
        foreach (var _ in new[] { 1, 2 })
        {
            await db.ExecuteAsync(MigratedDatabase.Migrator, "INSERT INTO notifications (user_id, kind) VALUES ($1, 'outbid')", user);
        }

        await page.ReloadAsync();
        await Assertions.Expect(page.GetByTestId("unread-count")).ToHaveTextAsync("2");

        await Assertions.Expect(page.GetByTestId("unread-badge")).ToHaveTextAsync("2");
        var box = await page.EvaluateAsync<double[]>("""
            () => {
                const badge = document.querySelector('[data-testid=unread-badge]').getBoundingClientRect();
                const icon = document.querySelector('[data-testid=bell] svg').getBoundingClientRect();
                const bar = document.querySelector('.appbar').getBoundingClientRect();
                return [badge.top - bar.top, bar.bottom - badge.bottom, badge.left - icon.left, icon.right - badge.left, icon.bottom - badge.top];
            }
            """);
        Assert.True(box[0] >= 0 && box[1] >= 0, $"The badge runs outside the bar: {string.Join(", ", box)}");
        // It sits on the icon's top-right corner: it starts inside the icon and overlaps it.
        Assert.True(box[2] > 0 && box[3] > 0 && box[4] > 0, $"The badge doesn't overlap the bell's corner: {string.Join(", ", box)}");
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
