using System.Net;
using System.Net.Http.Json;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Browser;
using Auction.IntegrationTests.Database;
using Auction.Migrator;
using Auction.Simulator;
using Auction.Simulator.Control;
using Auction.Simulator.Live;
using Microsoft.Extensions.Logging.Abstractions;

namespace Auction.IntegrationTests.Simulation;

/// <summary>
/// The always-on live marketplace against the whole stack: it lists photographed auctions from several simulated
/// sellers, bids on them and on visitors' listings, and heals itself after cancellations, disabled accounts,
/// publish limits and a demo reset. Each test starts from a reset, so the class has a database of its own.
/// </summary>
[Collection(Simulation.Name)]
public sealed class LiveMarketplaceTests(ResetDatabase own) : IClassFixture<ResetDatabase>
{
    private const string Password = "simulator-pass-for-tests";
    private const string AdminPassword = "admin-pass-for-tests";

    private static readonly DemoSeedOptions Seed = new(AdminPassword, "demo-pass-for-tests", Password);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MigratedDatabase Db => own.Db;

    private static LiveOptions Small(int target = 6, int sellers = 3, int bidders = 6) =>
        new(target, sellers, bidders) { Cycle = TimeSpan.FromSeconds(1), ThinkScale = 0.01 };

    [Fact]
    public async Task Live_mode_fills_the_marketplace_with_photographed_listings_from_several_sellers_and_bids_on_them()
    {
        var site = await FreshSiteAsync();
        await using var live = Start(site, Small());

        await WaitUntilAsync(async () => await ActiveSimulatedAsync() >= 6, "six active simulated listings");
        await WaitUntilAsync(async () => await ScalarAsync(
            "SELECT count(DISTINCT auction_id) FROM bids b JOIN auctions a ON a.id = b.auction_id WHERE a.status = 'active'") >= 3, "bids on three listings");

        Assert.Equal(0, await ScalarAsync(
            $"SELECT count(*) FROM auctions a WHERE {SimulatedSeller} AND a.status = 'active' AND NOT EXISTS (SELECT 1 FROM auction_images i WHERE i.auction_id = a.id AND i.is_cover)"));
        Assert.True(await ScalarAsync($"SELECT count(DISTINCT a.seller_id) FROM auctions a WHERE {SimulatedSeller}") >= 2);
        Assert.Equal("running", live.Marketplace.Status().State);
    }

    [Fact]
    public async Task Live_mode_spaces_its_sign_ins_so_the_sites_per_address_limit_keeps_room_for_others()
    {
        var site = await FreshSiteAsync();
        await using var live = Start(site, Small(target: 2, sellers: 3, bidders: 6) with { SignInsPerMinute = 5 });

        await Task.Delay(TimeSpan.FromSeconds(20), Ct);

        Assert.Equal(5, await ScalarAsync(
            "SELECT count(*) FROM sessions s JOIN users u ON u.id = s.user_id WHERE u.kind = 'simulator' AND s.created_at > now() - interval '1 minute'"));
        Assert.Equal(3, await ScalarAsync(
            "SELECT count(*) FROM sessions s JOIN users u ON u.id = s.user_id WHERE u.email LIKE 'sim-seller-__@auction.test'"));
    }

    [Fact]
    public async Task A_seller_stuck_at_its_draft_limit_clears_its_old_drafts_and_lists_again()
    {
        var site = await FreshSiteAsync();
        var api = await Db.ApiAsync();
        var (seller, signIn) = await api.SignInAsync("sim-seller-01@auction.test", Password);
        signIn.Dispose();
        for (var i = 0; i < 10; i++)
        {
            using var draft = await seller.PostCommandAsync("/api/v1/auctions", ApiCalls.Terms(title: $"Abandoned draft {i}"));
            Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        }

        await using var live = Start(site, Small(target: 2, sellers: 1, bidders: 1));

        await WaitUntilAsync(async () => await ActiveSimulatedAsync() >= 2, "two listings from the seller that was stuck at its draft limit");
        Assert.Equal(0, await ScalarAsync(
            "SELECT count(*) FROM auctions a JOIN users u ON u.id = a.seller_id WHERE u.email = 'sim-seller-01@auction.test' AND a.status = 'draft' AND a.title LIKE 'Abandoned draft%'"));
    }

    [Fact]
    public async Task A_live_marketplace_that_cannot_start_never_takes_the_control_service_down()
    {
        var site = await FreshSiteAsync();
        var broken = Small(target: 2, bidders: 2) with { CatalogueDirectory = Path.Combine(Path.GetTempPath(), $"no-catalogue-{Guid.NewGuid():N}") };
        await using var host = new RunHost(Settings(site, broken), NullLogger<RunHost>.Instance, TimeProvider.System);

        host.StartLive();
        await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        var started = host.TryStart(Profile.Smoke);

        Assert.NotNull(started);
        await WaitUntilAsync(() => Task.FromResult(host.Status() is { State: "idle", Last: not null }), "the smoke run to finish", TimeSpan.FromMinutes(3));
        Assert.True(host.Status().Last!.Passed, host.Status().Last!.Error);
    }

    [Fact]
    public async Task A_cancelled_listing_is_replaced()
    {
        var site = await FreshSiteAsync();
        await using var live = Start(site, Small(target: 4, bidders: 2));
        await WaitUntilAsync(async () => await ActiveSimulatedAsync() >= 4, "four active simulated listings");
        var victim = await Db.ScalarAsync<Guid>(MigratedDatabase.Migrator, $"SELECT a.id FROM auctions a WHERE {SimulatedSeller} AND a.status = 'active' LIMIT 1");

        var admin = await AdminAsync();
        using (var cancel = await admin.PostCommandAsync($"/api/v1/admin/auctions/{victim}/cancel", new { reason = "Testing the refill" }))
        {
            Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        }

        await WaitUntilAsync(async () => await ActiveSimulatedAsync() >= 4, "the cancelled listing replaced");
        Assert.Equal("cancelled", await Db.ScalarAsync<string>(MigratedDatabase.Migrator, "SELECT status FROM auctions WHERE id = $1", victim));
    }

    [Fact]
    public async Task Live_mode_bids_on_a_visitors_listing_within_three_times_its_opening_price()
    {
        var site = await FreshSiteAsync();
        var visitor = await (await Db.ApiAsync()).SignUpAsync("Visiting seller");
        var listing = await visitor.PublishedAsync(ApiCalls.Terms(title: "Visitor's lamp", openingPriceCents: 2_000, floorType: "absolute", reserveCents: null));
        await using var live = Start(site, Small(target: 2, sellers: 1, bidders: 6));

        await WaitUntilAsync(async () => await ScalarAsync("SELECT count(*) FROM bids WHERE auction_id = $1", listing) >= 1, "a simulated bid on the visitor's listing");
        Assert.True(await ScalarAsync("SELECT coalesce(max(current_price_cents), 0) FROM auctions WHERE id = $1", listing) <= 6_000);
    }

    [Fact]
    public async Task Live_mode_recovers_from_a_reset_and_refills()
    {
        var site = await FreshSiteAsync();
        await using var live = Start(site, Small(target: 4, bidders: 3));
        await WaitUntilAsync(async () => await ActiveSimulatedAsync() >= 4, "four active simulated listings");

        await DemoSeed.ResetAsync(Db.ConnectionStringFor(MigratedDatabase.Migrator), Seed, Ct);
        Assert.Equal(0, await ActiveSimulatedAsync());

        await WaitUntilAsync(async () => await ActiveSimulatedAsync() >= 4, "the marketplace refilled after the reset");
        Assert.Equal("running", live.Marketplace.Status().State);
        Assert.False(live.Run.IsFaulted);
    }

    [Fact]
    public async Task Disabled_simulated_accounts_are_skipped_and_the_rest_carry_on()
    {
        var site = await FreshSiteAsync();
        await Db.ExecuteAsync(
            MigratedDatabase.Migrator,
            "UPDATE users SET disabled_at = now() WHERE email IN ('sim-seller-01@auction.test', 'sim-seller-02@auction.test', 'sim-001@auction.test', 'sim-002@auction.test')");
        await using var live = Start(site, Small(target: 3, sellers: 3, bidders: 4));

        await WaitUntilAsync(async () => await ActiveSimulatedAsync() >= 3, "three listings from the one enabled seller");
        await WaitUntilAsync(async () => await ScalarAsync("SELECT count(*) FROM bids") >= 1, "bids from the enabled bidders");
        Assert.Equal(1, await ScalarAsync($"SELECT count(DISTINCT a.seller_id) FROM auctions a WHERE {SimulatedSeller}"));
        Assert.Equal(0, await ScalarAsync(
            "SELECT count(*) FROM bids b JOIN users u ON u.id = b.bidder_id WHERE u.email IN ('sim-001@auction.test', 'sim-002@auction.test')"));

        await Db.ExecuteAsync(MigratedDatabase.Migrator, "UPDATE users SET disabled_at = now() WHERE email LIKE 'sim-seller-__@auction.test'");
        await Task.Delay(TimeSpan.FromSeconds(4), Ct);
        Assert.Equal("running", live.Marketplace.Status().State);
        Assert.False(live.Run.IsCompleted);
    }

    [Fact]
    public async Task A_seller_at_its_publish_limit_is_skipped_for_the_day()
    {
        var site = await FreshSiteAsync();
        await Db.ExecuteAsync(
            MigratedDatabase.Migrator,
            """
            INSERT INTO user_limits (user_id, published_on, published_count)
            SELECT id, (now() AT TIME ZONE 'UTC')::date, 20 FROM users WHERE email = 'sim-seller-01@auction.test'
            ON CONFLICT (user_id) DO UPDATE SET published_on = excluded.published_on, published_count = 20
            """);
        await using var live = Start(site, Small(target: 4, sellers: 2, bidders: 1));

        await WaitUntilAsync(async () => await ActiveSimulatedAsync() >= 4, "four listings from the seller with room left");
        Assert.Equal(0, await ScalarAsync(
            "SELECT count(*) FROM auctions a JOIN users u ON u.id = a.seller_id WHERE u.email = 'sim-seller-01@auction.test' AND a.status <> 'draft'"));
    }

    [Fact]
    public async Task Pausing_live_mode_resumes_it_on_its_own_after_the_pause()
    {
        var site = await FreshSiteAsync();
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var host = new RunHost(Settings(site, Small(target: 2, bidders: 2)), NullLogger<RunHost>.Instance, clock);

        Assert.True(host.StartLive());
        await WaitUntilAsync(() => Task.FromResult(host.Status().Live?.State == "running"), "live mode running");
        Assert.True(host.PauseLive(TimeSpan.FromMinutes(30)));
        var paused = host.Status().Live!;
        Assert.Equal(("paused", clock.GetUtcNow() + TimeSpan.FromMinutes(30)), (paused.State, paused.ResumesAt));

        clock.Advance(TimeSpan.FromMinutes(30));
        await WaitUntilAsync(() => Task.FromResult(host.Status().Live?.State == "running"), "live mode resumed after the pause");
    }

    [Fact]
    public async Task A_short_run_pauses_live_mode_and_live_mode_resumes_when_it_ends()
    {
        var site = await FreshSiteAsync();
        await using var host = new RunHost(Settings(site, Small(target: 2, bidders: 2)), NullLogger<RunHost>.Instance, TimeProvider.System);
        Assert.True(host.StartLive());
        await WaitUntilAsync(() => Task.FromResult(host.Status().Live?.State == "running"), "live mode running");

        var started = host.TryStart(Profile.Smoke);

        Assert.NotNull(started);
        Assert.Equal("smoke", started.Profile);
        Assert.Equal("paused", started.Live!.State);
        await WaitUntilAsync(() => Task.FromResult(host.Status() is { State: "idle", Live.State: "running" }), "live mode back after the smoke run", TimeSpan.FromMinutes(3));
        Assert.True(host.Status().Last!.Passed, host.Status().Last!.Error);
    }

    [Fact]
    public async Task An_admin_pauses_and_resumes_the_live_marketplace_through_the_API_and_both_are_audited()
    {
        var site = await FreshSiteAsync();
        await using var control = await ControlHost.StartAsync(Settings(site, Small(target: 2, bidders: 2)) with { StartLive = true });
        await using var api = await Db.ApiWithAsync(new Dictionary<string, string>
        {
            ["Api:SimulatorAddress"] = control.Address.ToString(),
            ["Api:SimulatorToken"] = "control-token-for-tests",
        });
        var admin = await api.SignUpAsync("Visiting admin", role: "admin");
        var user = await api.SignUpAsync();
        await WaitUntilAsync(async () => (await admin.Http.GetJsonAsync("/api/v1/admin/simulator")).GetProperty("run").GetProperty("live").GetProperty("state").GetString() == "running", "live mode running");

        using var notAdmin = await user.Http.DeleteAsync(new Uri("/api/v1/admin/simulator/live", UriKind.Relative), Ct);
        using var pause = await admin.Http.DeleteAsync(new Uri("/api/v1/admin/simulator/live", UriKind.Relative), Ct);
        var paused = (await admin.Http.GetJsonAsync("/api/v1/admin/simulator")).GetProperty("run").GetProperty("live");
        using var resume = await admin.Http.PostAsJsonAsync("/api/v1/admin/simulator/live", new { }, Ct);

        Assert.Equal((HttpStatusCode.NotFound, HttpStatusCode.Accepted, HttpStatusCode.Accepted), (notAdmin.StatusCode, pause.StatusCode, resume.StatusCode));
        Assert.Equal("paused", paused.GetProperty("state").GetString());
        Assert.InRange(paused.GetProperty("resumesAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMinutes(29), DateTimeOffset.UtcNow.AddMinutes(31));
        await WaitUntilAsync(async () => (await admin.Http.GetJsonAsync("/api/v1/admin/simulator")).GetProperty("run").GetProperty("live").GetProperty("state").GetString() == "running", "live mode resumed");
        Assert.Equal(
            "live_marketplace_paused,live_marketplace_resumed",
            await Db.ScalarAsync<string>(MigratedDatabase.Migrator, "SELECT string_agg(action, ',' ORDER BY id) FROM admin_audit WHERE target_type = 'live_marketplace'"));
    }

    private const string SimulatedSeller = "a.seller_id IN (SELECT id FROM users WHERE email LIKE 'sim-seller-__@auction.test')";

    private async Task<Uri> FreshSiteAsync()
    {
        await DemoSeed.ResetAsync(Db.ConnectionStringFor(MigratedDatabase.Migrator), Seed, Ct);
        return await Browsers.SiteAsync(Db);
    }

    private ControlSettings Settings(Uri site, LiveOptions options) =>
        new(site, Password, "control-token-for-tests", Db.ConnectionStringFor(MigratedDatabase.Verifier)) { LiveOptions = options };

    private Running Start(Uri site, LiveOptions options)
    {
        var marketplace = new LiveMarketplace(
            options, new SimulationOptions(site, Password, Seed: 21) { VerifierConnectionString = Db.ConnectionStringFor(MigratedDatabase.Verifier) },
            TimeProvider.System, NullLogger.Instance);
        var stop = new CancellationTokenSource();
        return new Running(marketplace, stop, marketplace.RunAsync(stop.Token));
    }

    private async Task<HttpClient> AdminAsync()
    {
        var api = await Db.ApiAsync();
        var (http, response) = await api.SignInAsync(DemoSeedOptions.AdminEmail, AdminPassword);
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        return http;
    }

    private Task<long> ActiveSimulatedAsync() =>
        ScalarAsync($"SELECT count(*) FROM auctions a WHERE {SimulatedSeller} AND a.status = 'active'");

    private Task<long> ScalarAsync(string sql, params object[] args) => Db.ScalarAsync<long>(MigratedDatabase.Migrator, sql, args);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? TimeSpan.FromSeconds(90));
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(500, Ct);
        }
    }

    private sealed class Running(LiveMarketplace marketplace, CancellationTokenSource stop, Task run) : IAsyncDisposable
    {
        public LiveMarketplace Marketplace { get; } = marketplace;

        public Task Run { get; } = run;

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
#pragma warning disable VSTHRD003 // the run this test started
            await Run;
#pragma warning restore VSTHRD003
            stop.Dispose();
            Marketplace.Dispose();
        }
    }

    /// <summary>A clock the test moves by hand, for the pause that ends on its own.</summary>
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
