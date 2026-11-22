using System.Net;
using System.Net.Http.Headers;
using Auction.Api.Accounts;
using Auction.Api.Hosting;
using Npgsql;

namespace Auction.Api.Admin;

public sealed record SimulatorStartRequest(string? Profile);

/// <summary>How the last finished run ended, as the simulator reports it.</summary>
public sealed record SimulatorOutcome(
    Guid RunId, string Profile, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool StoppedEarly, bool Passed, int Accepted, string? Error);

/// <summary>The live marketplace: running or paused (until when, if an admin paused it), its size and its last check.</summary>
public sealed record SimulatorLive(
    string State, DateTimeOffset? Since, DateTimeOffset? ResumesAt, int ListingsLive, int BidsAccepted, DateTimeOffset? LastVerifiedAt, bool? LastVerifyPassed);

/// <summary>
/// The simulator's state: idle, running or stopping, the current run's progress, the last outcome, and the
/// live marketplace (null where it isn't enabled).
/// </summary>
public sealed record SimulatorRun(string State, Guid? RunId, string? Profile, DateTimeOffset? StartedAt, int Attempts, int Accepted, SimulatorOutcome? Last)
{
    public SimulatorLive? Live { get; init; }
}

/// <summary>What the admin area shows: whether the simulator can be reached, the profiles an admin may start, and its state.</summary>
public sealed record SimulatorView(bool Available, IReadOnlyList<string> Profiles, SimulatorRun? Run);

/// <summary>
/// Starts and stops simulator runs for the admin area (ADM-02) through the simulator's control endpoint on
/// the internal network, with the shared token. Under DEMO_MODE only the smoke and realistic profiles may
/// start. Each start and stop is audited like any other admin action, as is pausing or resuming the live
/// marketplace (a pause lasts at most 30 minutes; the simulator then resumes it on its own).
/// </summary>
internal sealed class SimulatorControl(HttpClient http, ApiOptions options, NpgsqlDataSource database)
{
    private static readonly string[] DemoProfiles = ["smoke", "realistic"];
    private static readonly string[] AllProfiles = ["smoke", "realistic", "hot-burst"];

    public IReadOnlyList<string> Profiles => options.DemoMode ? DemoProfiles : AllProfiles;

    public static void Configure(IServiceProvider services, HttpClient http)
    {
        var options = services.GetRequiredService<ApiOptions>();
        http.BaseAddress = options.SimulatorAddress;
        http.Timeout = TimeSpan.FromSeconds(5);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.SimulatorToken);
    }

    public async Task<SimulatorView> ViewAsync(CancellationToken cancellationToken)
    {
        var (status, run) = await SendAsync(HttpMethod.Get, "runs/current", null, cancellationToken);
        return new SimulatorView(status == HttpStatusCode.OK, Profiles, run);
    }

    public async Task<IResult> StartAsync(SimulatorStartRequest body, HttpContext http)
    {
        if (body.Profile is not { } profile || !AllProfiles.Contains(profile))
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["profile"] = [$"one of {string.Join(", ", AllProfiles)}"] });
        }

        if (!Profiles.Contains(profile))
        {
            return Problems.Of(StatusCodes.Status409Conflict, "profile_not_allowed", "The demo runs only the smoke and realistic profiles.");
        }

        var (status, run) = await SendAsync(HttpMethod.Post, "runs", new { profile }, http.RequestAborted);
        return await AnswerAsync(status, run, http, "simulator_start", $"Started the {profile} profile");
    }

    public async Task<IResult> StopAsync(HttpContext http)
    {
        var (status, run) = await SendAsync(HttpMethod.Delete, "runs/current", null, http.RequestAborted);
        return await AnswerAsync(status, run, http, "simulator_stop", $"Stopped the {run?.Profile} profile");
    }

    public async Task<IResult> PauseLiveAsync(HttpContext http)
    {
        var (status, run) = await SendAsync(HttpMethod.Delete, "live", null, http.RequestAborted);
        return await LiveAnswerAsync(status, run, http, "live_marketplace_paused", "Paused the live marketplace for up to 30 minutes");
    }

    public async Task<IResult> ResumeLiveAsync(HttpContext http)
    {
        var (status, run) = await SendAsync(HttpMethod.Post, "live", null, http.RequestAborted);
        return await LiveAnswerAsync(status, run, http, "live_marketplace_resumed", "Resumed the live marketplace");
    }

    private async Task<IResult> LiveAnswerAsync(HttpStatusCode status, SimulatorRun? run, HttpContext http, string action, string reason)
    {
        switch (status)
        {
            case HttpStatusCode.Accepted:
                await AuditAsync(http.User.UserIdOf()!.Value.Value, action, "live_marketplace", Guid.Empty, reason, http.RequestAborted);
                return TypedResults.Accepted((string?)null, run);
            case HttpStatusCode.Conflict:
                return Problems.Of(StatusCodes.Status409Conflict, "live_not_running", "The live marketplace isn't running.");
            default:
                return Problems.Of(StatusCodes.Status503ServiceUnavailable, "simulator_unavailable", "The simulator can't be reached.");
        }
    }

    private async Task<IResult> AnswerAsync(HttpStatusCode status, SimulatorRun? run, HttpContext http, string action, string reason)
    {
        switch (status)
        {
            case HttpStatusCode.Accepted when run?.RunId is { } runId:
                await AuditAsync(http.User.UserIdOf()!.Value.Value, action, "simulator_run", runId, reason, http.RequestAborted);
                return TypedResults.Accepted((string?)null, run);
            case HttpStatusCode.Conflict when action == "simulator_start":
                return Problems.Of(StatusCodes.Status409Conflict, "run_in_progress", "A simulator run is already in progress.");
            case HttpStatusCode.Conflict:
                return Problems.Of(StatusCodes.Status409Conflict, "not_running", "No simulator run is in progress.");
            default:
                return Problems.Of(StatusCodes.Status503ServiceUnavailable, "simulator_unavailable", "The simulator can't be reached.");
        }
    }

    // The status and body, or 503 when the simulator isn't configured or can't be reached.
    private async Task<(HttpStatusCode Status, SimulatorRun? Run)> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        if (options.SimulatorAddress is null)
        {
            return (HttpStatusCode.ServiceUnavailable, null);
        }

        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative))
        {
            Content = body is null ? null : JsonContent.Create(body),
        };
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var run = response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted
                ? await response.Content.ReadFromJsonAsync<SimulatorRun>(cancellationToken)
                : null;
            return (response.StatusCode, run);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (HttpStatusCode.ServiceUnavailable, null);
        }
    }

    private async Task AuditAsync(Guid adminId, string action, string targetType, Guid targetId, string reason, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand(
            "INSERT INTO auction.admin_audit (admin_id, action, target_type, target_id, reason, at) VALUES ($1, $2, $3, $4, $5, now())");
        command.Parameters.Add(new() { Value = adminId });
        command.Parameters.Add(new() { Value = action });
        command.Parameters.Add(new() { Value = targetType });
        command.Parameters.Add(new() { Value = targetId });
        command.Parameters.Add(new() { Value = reason });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
