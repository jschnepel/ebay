using System.Net;
using System.Net.Http.Json;
using Auction.IntegrationTests.Browser;
using Auction.IntegrationTests.Database;
using Auction.Migrator;
using Microsoft.Playwright;

namespace Auction.IntegrationTests.Api;

/// <summary>HOST-01: health and readiness, a path base, forwarded headers only from the trusted proxy.</summary>
[Collection(Auction.IntegrationTests.Browser.BrowserJourneys.Name)]
public sealed class HostingTests(MigratedDatabase db, Browsers browsers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_is_liveness_only_and_ready_needs_postgresql_and_the_engine()
    {
        var api = await db.ApiAsync();
        await using var noEngine = await db.ApiWithAsync(new Dictionary<string, string> { ["Api:EngineAddress"] = "https://127.0.0.1:9" });

        Assert.Equal(HttpStatusCode.OK, await api.Anonymous().StatusOfGetAsync("/health"));
        Assert.Equal(HttpStatusCode.OK, await api.Anonymous().StatusOfGetAsync("/ready"));
        Assert.Equal(HttpStatusCode.OK, await noEngine.Anonymous().StatusOfGetAsync("/health"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await noEngine.Anonymous().StatusOfGetAsync("/ready"));
    }

    [Fact]
    public async Task A_session_outlives_the_api_instance_that_issued_it_when_the_key_directory_is_kept()
    {
        var keys = Directory.CreateTempSubdirectory("auction-keys-");
        var kept = new Dictionary<string, string> { ["Api:KeysDirectory"] = keys.FullName };
        string cookie;
        await using (var first = await db.ApiWithAsync(kept))
        {
            using var signUp = await first.Anonymous(handleCookies: false).PostAsJsonAsync(
                "/api/v1/account/sign-up", new { displayName = "Kept", email = $"{Guid.NewGuid():N}@example.test" }, Ct);
            Assert.Equal(HttpStatusCode.Created, signUp.StatusCode);
            cookie = signUp.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("auction_session=", StringComparison.Ordinal)).Split(';')[0];
        }

        await using var replacement = await db.ApiWithAsync(kept);
        await using var freshKeys = await db.ApiWithAsync(new Dictionary<string, string> { ["Api:KeysDirectory"] = Directory.CreateTempSubdirectory("auction-keys-").FullName });

        Assert.NotEmpty(keys.GetFiles("key-*.xml"));
        Assert.Equal(HttpStatusCode.OK, await MeStatusAsync(replacement, cookie));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeStatusAsync(freshKeys, cookie));
    }

    private static async Task<HttpStatusCode> MeStatusAsync(ApiHost host, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/account/me");
        request.Headers.Add("Cookie", cookie);
        using var response = await host.Anonymous(handleCookies: false).SendAsync(request, Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task Under_a_path_base_the_page_the_api_the_hub_and_the_cookie_all_live_under_it()
    {
        await using var hosted = await db.ApiWithAsync(new Dictionary<string, string> { ["Api:PathBase"] = "/projects/ebay" });
        var http = hosted.Anonymous();

        var page = await http.GetStringAsync(new Uri("/projects/ebay/auctions/0195d3a2-7c4e-7000-8000-000000000001", UriKind.Relative), Ct);
        using var list = await http.GetAsync(new Uri("/projects/ebay/api/v1/auctions", UriKind.Relative), Ct);
        using var signUp = await http.PostAsJsonAsync(
            "/projects/ebay/api/v1/account/sign-up", new { displayName = "Hosted", email = $"{Guid.NewGuid():N}@example.test" }, Ct);
        using var negotiate = await http.PostAsync(new Uri("/projects/ebay/hubs/auctions/negotiate?negotiateVersion=1", UriKind.Relative), null, Ct);

        Assert.Contains("<base href=\"/projects/ebay/\" />", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("application/json", list.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Created, signUp.StatusCode);
        Assert.Contains("path=/projects/ebay", Assert.Single(signUp.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
    }

    [Fact]
    public async Task Forwarded_addresses_count_only_when_they_come_from_the_trusted_proxy()
    {
        var limits = new Dictionary<string, string>
        {
            ["Api:RateLimits:SignUpPerAddressPerHour"] = "1",
            ["Api:TrustedProxies"] = "10.77.0.1",
        };
        await using var throughProxy = await db.ApiWithAsync(new Dictionary<string, string>(limits) { [ApiHost.ClientAddressSetting] = "10.77.0.1" });
        await using var direct = await db.ApiWithAsync(new Dictionary<string, string>(limits) { [ApiHost.ClientAddressSetting] = $"10.78.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}" });

        // Through the proxy, two visitors are two addresses; directly, a made-up header changes nothing.
        Assert.Equal(HttpStatusCode.Created, await SignUpFromAsync(throughProxy, "203.0.113.21"));
        Assert.Equal(HttpStatusCode.Created, await SignUpFromAsync(throughProxy, "203.0.113.22"));
        Assert.Equal(HttpStatusCode.Created, await SignUpFromAsync(direct, "203.0.113.21"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignUpFromAsync(direct, "203.0.113.22"));
    }

    [Fact]
    public async Task A_trusted_range_believes_forwarded_addresses_from_any_proxy_inside_it_and_none_outside()
    {
        // A tunnel container's address changes when it restarts, so a deployment trusts its network's range.
        var limits = new Dictionary<string, string>
        {
            ["Api:RateLimits:SignUpPerAddressPerHour"] = "1",
            ["Api:TrustedProxies"] = "10.79.0.0/16",
        };
        await using var inside = await db.ApiWithAsync(new Dictionary<string, string>(limits) { [ApiHost.ClientAddressSetting] = "10.79.4.20" });
        await using var outside = await db.ApiWithAsync(new Dictionary<string, string>(limits) { [ApiHost.ClientAddressSetting] = $"10.80.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}" });

        Assert.Equal(HttpStatusCode.Created, await SignUpFromAsync(inside, "203.0.113.31"));
        Assert.Equal(HttpStatusCode.Created, await SignUpFromAsync(inside, "203.0.113.32"));
        Assert.Equal(HttpStatusCode.Created, await SignUpFromAsync(outside, "203.0.113.31"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await SignUpFromAsync(outside, "203.0.113.32"));
    }

    [Fact]
    public async Task In_the_browser_the_site_works_under_its_path_base()
    {
        var (host, address) = await db.SiteWithAsync(new Dictionary<string, string> { ["Api:PathBase"] = "/projects/ebay" });
        await using var _ = host;
        var page = await browsers.NewPageAsync(new Uri(address, "/projects/ebay/"));

        await page.GotoAsync("sign-in");
        await page.GetByLabel("Display name").FillAsync("Under a path");
        await page.GetByLabel("Email for the new account").FillAsync($"{Guid.NewGuid():N}@example.test");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await Assertions.Expect(page.GetByTestId("signed-in-as")).ToHaveTextAsync("Under a path");
        await page.GetByRole(AriaRole.Link, new() { Name = "Sell" }).ClickAsync();

        await page.WaitForURLAsync("**/projects/ebay/sell");
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "New listing" })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("unread-count")).ToHaveTextAsync("0");
    }

    private static async Task<HttpStatusCode> SignUpFromAsync(ApiHost host, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/account/sign-up")
        {
            Content = JsonContent.Create(new { displayName = "Visitor", email = $"{Guid.NewGuid():N}@example.test" }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        using var response = await host.Anonymous().SendAsync(request, Ct);
        return response.StatusCode;
    }
}

/// <summary>A database of its own: reset-demo empties every table, so it must not share one with other tests.</summary>
public sealed class ResetDatabase : IAsyncLifetime
{
    public MigratedDatabase Db { get; } = new();

    public ValueTask InitializeAsync() => Db.InitializeAsync();

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

/// <summary>HOST-01: reset-demo twice gives the same seeded state and never changes a password.</summary>
public sealed class ResetDemoTests(ResetDatabase reset) : IClassFixture<ResetDatabase>
{
    private static readonly DemoSeedOptions Passwords = new("admin-pass", "demo-pass", "simulator-pass");

    [Fact]
    public async Task Reset_demo_twice_gives_the_same_seeded_state_with_fresh_times_and_the_same_passwords()
    {
        var db = reset.Db;
        var connection = db.ConnectionStringFor(MigratedDatabase.Migrator);
        await DemoSeed.RunAsync(connection, Passwords, TestContext.Current.CancellationToken);
        var hashes = await db.ScalarAsync<string>(MigratedDatabase.Migrator, "SELECT string_agg(email || password_hash, ',' ORDER BY email) FROM users");
        await db.ExecuteAsync(
            MigratedDatabase.Migrator,
            "INSERT INTO users (id, email, normalized_email, display_name) VALUES (gen_random_uuid(), 'visitor@example.test', 'VISITOR@EXAMPLE.TEST', 'Visitor')");

        await DemoSeed.ResetAsync(connection, new DemoSeedOptions("other", "other", "other"), TestContext.Current.CancellationToken);
        var first = await StateAsync(db);
        await DemoSeed.ResetAsync(connection, Passwords, TestContext.Current.CancellationToken);
        var second = await StateAsync(db);

        Assert.Equal(first, second);
        Assert.Equal((0L, 0L, 6L), (first.SelfRegistered, first.Bids, first.ActiveAuctions));
        Assert.Equal(hashes, await db.ScalarAsync<string>(MigratedDatabase.Migrator, "SELECT string_agg(email || password_hash, ',' ORDER BY email) FROM users"));
        Assert.True(await db.ScalarAsync<bool>(MigratedDatabase.Migrator, "SELECT min(ends_at) > now() + interval '5 minutes' FROM auctions"));
    }

    private static async Task<(long SelfRegistered, long Bids, long ActiveAuctions, string? Titles)> StateAsync(MigratedDatabase db) => (
        await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM users WHERE kind = 'self_registered'"),
        await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM bids"),
        await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM auctions WHERE status = 'active'"),
        await db.ScalarAsync<string>(MigratedDatabase.Migrator, "SELECT string_agg(title, ',' ORDER BY title) FROM auctions"));
}
