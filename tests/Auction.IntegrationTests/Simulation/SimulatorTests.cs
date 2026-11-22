using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Browser;
using Auction.IntegrationTests.Database;
using Auction.Migrator;
using Auction.Simulator;
using Auction.Simulator.Control;
using Auction.Verifier;
using Npgsql;

namespace Auction.IntegrationTests.Simulation;

/// <summary>
/// The simulator over the public API of the whole stack, and the separate verifier over the database
/// (SIM-01..05, SIM-16, INT-03), and its control endpoint for the admin area (ADM-02). Runs alone: the hot
/// burst would distort the timing-bound tests beside it, and runs share the simulator accounts.
/// </summary>
[Collection(Simulation.Name)]
public sealed class SimulatorTests(MigratedDatabase db)
{
    private const string Password = "simulator-pass-for-tests";
    private const string ControlToken = "control-token-for-tests";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_smoke_cli_signs_in_every_virtual_user_bids_writes_its_report_and_exits_zero()
    {
        var site = await SeededSiteAsync();
        var report = Path.Combine(Path.GetTempPath(), $"simulator-{Guid.NewGuid():N}.json");

        var (exit, output) = await RunToolAsync(
            "Auction.Simulator", ["run", "smoke", "--base-url", site.ToString(), "--seed", "7", "--duration", "6", "--no-wait-close", "--report", report]);

        Assert.True(exit == 0, output);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report, Ct));
        var root = json.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("setupError").ValueKind);
        Assert.Equal(3, root.GetProperty("intended").GetProperty("bidders").GetInt32());
        Assert.True(root.GetProperty("actual").GetProperty("accepted").GetInt32() > 0);
        Assert.Equal(0, root.GetProperty("verification").GetProperty("violations").GetArrayLength());
        File.Delete(report);
    }

    [Fact]
    public async Task A_hot_burst_of_50_bidders_for_30_seconds_keeps_every_invariant()
    {
        var site = await SeededSiteAsync();

        using var runner = new SimulationRunner(Profile.HotBurst, Options(site, seed: 11));
        var report = await runner.RunAsync(Ct);

        Assert.Null(report.SetupError);
        Assert.Equal(50, report.Intended.Bidders);
        Assert.True(report.Actual!.Accepted > 10, $"only {report.Actual.Accepted} bids accepted of {report.Actual.Attempts}");
        Assert.True(report.Verification!.Passed, string.Join("; ", report.Verification.Violations.Select(v => $"{v.Rule}: {v.Detail}")));
        Assert.True(report.Verification.BidsChecked >= report.Actual.Accepted);
        Assert.NotNull(report.Actual.BidLatencyP95Ms);
    }

    [Fact]
    public async Task The_realistic_profile_bids_concurrently_across_several_auctions()
    {
        var site = await SeededSiteAsync();

        using var runner = new SimulationRunner(Profile.Realistic, Options(site, seed: 3) with { Duration = TimeSpan.FromSeconds(15) });
        var report = await runner.RunAsync(Ct);

        Assert.True(report.Passed, JsonSerializer.Serialize(report));
        Assert.Equal(5, report.Auctions.Count);
        Assert.True(report.Actual!.AuctionsWithBids >= 3, $"bids landed on {report.Actual.AuctionsWithBids} auctions");
    }

    [Fact]
    public async Task The_verifier_finds_a_price_that_disagrees_with_the_history_and_its_cli_exits_non_zero()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(reserveCents: null, floorType: "minimum_bid"));
        using (var bid = await buyer.Http.BidAsync(id, 1500))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        await using var verifier = NpgsqlDataSource.Create(db.ConnectionStringFor(MigratedDatabase.Verifier));
        var before = await InvariantVerifier.VerifyAsync(verifier, new VerifierScope(SellerId: seller.Id), Ct);
        await db.ExecuteAsync(MigratedDatabase.Migrator, "UPDATE auctions SET current_price_cents = current_price_cents + 100 WHERE id = $1", id);
        var after = await InvariantVerifier.VerifyAsync(verifier, new VerifierScope(SellerId: seller.Id), Ct);
        var (exit, output) = await RunToolAsync("Auction.Verifier", [], db.ConnectionStringFor(MigratedDatabase.Verifier));

        Assert.True(before.Passed);
        Assert.Contains(after.Violations, v => v.AuctionId == id && v.Rule == "standing");
        Assert.Contains(after.Violations, v => v.AuctionId == id && v.Rule == "price_not_last_visible");
        Assert.True(exit == 1, output);
    }

    [Fact]
    public async Task A_wrong_simulator_password_stops_the_run_at_setup_and_fails()
    {
        var site = await SeededSiteAsync();

        using var runner = new SimulationRunner(Profile.Smoke, Options(site, seed: 1) with { Password = "wrong" });
        var report = await runner.RunAsync(Ct);

        Assert.NotNull(report.SetupError);
        Assert.False(report.Passed);
        Assert.Null(report.Actual);
    }

    [Fact]
    public async Task The_control_endpoint_needs_its_token_runs_one_profile_at_a_time_and_stops_on_request()
    {
        var site = await SeededSiteAsync();
        await using var control = await ControlHost.StartAsync(ControlSettingsFor(site));
        using var anonymous = new HttpClient { BaseAddress = control.Address };
        using var wrongToken = ControlHost.Client(control.Address, "not-the-control-token");
        using var http = ControlHost.Client(control.Address, ControlToken);

        using (var noToken = await anonymous.PostAsJsonAsync("runs", new { profile = "smoke" }, Ct))
        using (var wrong = await wrongToken.GetAsync(new Uri("runs/current", UriKind.Relative), Ct))
        {
            Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized), (noToken.StatusCode, wrong.StatusCode));
        }

        using (var first = await http.PostAsJsonAsync("runs", new { profile = "smoke" }, Ct))
        using (var second = await http.PostAsJsonAsync("runs", new { profile = "smoke" }, Ct))
        using (var unknown = await http.PostAsJsonAsync("runs", new { profile = "endless" }, Ct))
        {
            Assert.Equal(
                (HttpStatusCode.Accepted, HttpStatusCode.Conflict, HttpStatusCode.BadRequest),
                (first.StatusCode, second.StatusCode, unknown.StatusCode));
        }

        await ControlHost.WaitForAsync(http, run => run.GetProperty("accepted").GetInt32() > 0);
        using (var stop = await http.DeleteAsync(new Uri("runs/current", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
        }

        var last = (await ControlHost.WaitForAsync(http, run => run.GetProperty("state").GetString() == "idle")).GetProperty("last");
        Assert.True(last.GetProperty("stoppedEarly").GetBoolean());
        Assert.True(last.GetProperty("passed").GetBoolean(), last.ToString());
        using var none = await http.DeleteAsync(new Uri("runs/current", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
    }

    [Fact]
    public async Task An_admin_starts_and_stops_a_profile_through_the_API_which_audits_both_and_hides_the_routes_from_others()
    {
        var site = await SeededSiteAsync();
        await using var control = await ControlHost.StartAsync(ControlSettingsFor(site));
        await using var api = await db.ApiWithAsync(new Dictionary<string, string>
        {
            ["Api:SimulatorAddress"] = control.Address.ToString(),
            ["Api:SimulatorToken"] = ControlToken,
        });
        var user = await api.SignUpAsync();
        var (admin, signIn) = await api.SignInAsync(await db.InsertPasswordAccountAsync("admin", "admin-simulator-password"), "admin-simulator-password");
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        signIn.Dispose();

        using (var notAdmin = await user.Http.PostAsJsonAsync("/api/v1/admin/simulator/runs", new { profile = "smoke" }, Ct))
        using (var hotBurst = await admin.PostAsJsonAsync("/api/v1/admin/simulator/runs", new { profile = "hot-burst" }, Ct))
        {
            await notAdmin.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
            await hotBurst.AssertProblemAsync(HttpStatusCode.Conflict, "profile_not_allowed");
        }

        Guid runId;
        using (var start = await admin.PostAsJsonAsync("/api/v1/admin/simulator/runs", new { profile = "smoke" }, Ct))
        using (var again = await admin.PostAsJsonAsync("/api/v1/admin/simulator/runs", new { profile = "smoke" }, Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
            runId = (await start.JsonAsync()).GetProperty("runId").GetGuid();
            await again.AssertProblemAsync(HttpStatusCode.Conflict, "run_in_progress");
        }

        var view = await admin.GetJsonAsync("/api/v1/admin/simulator");
        Assert.True(view.GetProperty("available").GetBoolean());
        Assert.Equal(["smoke", "realistic"], view.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()));
        await ControlHost.WaitForAsync(admin, run => run.GetProperty("accepted").GetInt32() > 0, "/api/v1/admin/simulator", "run");

        using (var stop = await admin.DeleteAsync(new Uri("/api/v1/admin/simulator/runs/current", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
        }

        await ControlHost.WaitForAsync(admin, run => run.GetProperty("state").GetString() == "idle", "/api/v1/admin/simulator", "run");
        Assert.Equal(2L, await db.ScalarAsync<long>(
            MigratedDatabase.Migrator,
            "SELECT count(*) FROM admin_audit WHERE target_type = 'simulator_run' AND target_id = $1 AND action IN ('simulator_start', 'simulator_stop')",
            runId));
    }

    [Fact]
    public void A_run_with_accepted_bids_fails_when_the_verifier_found_a_violation_or_did_not_run()
    {
        var attempts = new[] { new BidAttempt(Guid.NewGuid(), Guid.NewGuid(), 1000, HttpStatusCode.Created, null, TimeSpan.FromMilliseconds(5), false) };
        var clean = new VerifierReport(1, 1, []);
        var broken = new VerifierReport(1, 1, [new Violation(Guid.NewGuid(), "standing", "planted")]);
        var options = new SimulationOptions(new Uri("http://localhost"), Password, 1);

        SimulationReport Run(VerifierReport? verification) =>
            SimulationReport.Completed(Profile.Smoke, options, (TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), attempts, [], verification);

        Assert.True(Run(clean).Passed);
        Assert.False(Run(broken).Passed);
        Assert.False(Run(null).Passed);
    }

    private SimulationOptions Options(Uri site, int seed) => new(site, Password, seed)
    {
        VerifierConnectionString = db.ConnectionStringFor(MigratedDatabase.Verifier),
        WaitForClose = false,
    };

    private ControlSettings ControlSettingsFor(Uri site) => new(site, Password, ControlToken, db.ConnectionStringFor(MigratedDatabase.Verifier));

    private async Task<Uri> SeededSiteAsync()
    {
        await DemoSeed.RunAsync(
            db.ConnectionStringFor(MigratedDatabase.Migrator), new DemoSeedOptions("admin-pass-for-tests", "demo-pass-for-tests", Password), Ct);
        return await Browsers.SiteAsync(db);
    }

    // Runs a built tool (the simulator or verifier CLI) as its own process from its own build output, as an
    // operator would; that output carries the tool's own dependencies.
    private async Task<(int Exit, string Output)> RunToolAsync(string project, string[] arguments, string? verifierConnection = null)
    {
        var directory = Path.Combine(RepositoryRoot(), "src", project, "bin", "Release", "net10.0");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        start.ArgumentList.Add(Path.Combine(directory, project + ".dll"));
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["SIMULATOR_PASSWORD"] = Password;
        start.Environment["VERIFIER_CONNECTION_STRING"] = verifierConnection ?? db.ConnectionStringFor(MigratedDatabase.Verifier);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await output + await errors);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Auction.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root (Auction.slnx) was not found.");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Simulation
{
    public const string Name = "Simulation";

    private Simulation()
    {
    }
}
