using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auction.Simulator.Control;

/// <summary>
/// <c>simulator serve</c>: the control endpoint the API calls to start and stop runs for the admin area
/// (ADM-02), and to pause and resume the live marketplace. It listens on the internal network only, and every
/// route but <c>/health</c> needs the shared token as a bearer token; without it the answer is 401. With
/// LIVE_SIMULATION=true the live marketplace starts with the service.
/// </summary>
public static class ControlServer
{
    public const string TokenVariable = "SIMULATOR_CONTROL_TOKEN";

    /// <summary>How long an admin's pause of the live marketplace lasts before it resumes on its own.</summary>
    public static readonly TimeSpan LivePause = TimeSpan.FromMinutes(30);

    /// <summary>The shortest token accepted, so a placeholder can't protect the endpoint by accident.</summary>
    public const int MinTokenLength = 16;

    private static readonly char[] PortSeparators = [';', ','];

    /// <summary><c>simulator health</c> or <c>simulator serve [ASP.NET Core arguments]</c>.</summary>
    public static Task<int> RunCommandAsync(string[] args) => args[0] == "health" ? ProbeAsync() : ServeAsync(args[1..]);

    /// <summary>
    /// Serves the control endpoint until shut down, with BASE_URL, SIMULATOR_PASSWORD, the token and the
    /// verifier's connection from the environment; exit code 2 when the site or token is missing.
    /// </summary>
    public static async Task<int> ServeAsync(string[] args)
    {
        var token = Environment.GetEnvironmentVariable(TokenVariable) ?? "";
        if (token.Length < MinTokenLength || !Uri.TryCreate(Environment.GetEnvironmentVariable("BASE_URL"), UriKind.Absolute, out var site))
        {
            await Console.Error.WriteLineAsync($"Set BASE_URL and {TokenVariable} (at least {MinTokenLength} characters).");
            return 2;
        }

        var settings = new ControlSettings(
            site, Environment.GetEnvironmentVariable("SIMULATOR_PASSWORD") ?? "", token, Environment.GetEnvironmentVariable("VERIFIER_CONNECTION_STRING"))
        {
            StartLive = string.Equals(Environment.GetEnvironmentVariable("LIVE_SIMULATION"), "true", StringComparison.OrdinalIgnoreCase),
        };
        await using var app = Build(settings, args);
        await app.RunAsync();
        return 0;
    }

    public static WebApplication Build(ControlSettings settings, string[] args)
    {
        if (settings.Token.Length < MinTokenLength)
        {
            throw new ArgumentException($"The control token must have at least {MinTokenLength} characters.", nameof(settings));
        }

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders().AddJsonConsole();
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RunHost>();
        builder.Services.AddProblemDetails();

        var app = builder.Build();
        app.MapGet("/health", () => TypedResults.Ok());
        var runs = app.MapGroup("/runs").AddEndpointFilter(RequireTokenAsync);
        runs.MapGet("/current", (RunHost host) => TypedResults.Ok(host.Status()));
        runs.MapPost("", Start);
        runs.MapDelete("/current", (RunHost host) => host.Stop()
            ? (IResult)TypedResults.Accepted("/runs/current", host.Status())
            : Problem(StatusCodes.Status409Conflict, "not_running", "No simulator run is in progress."));
        var live = app.MapGroup("/live").AddEndpointFilter(RequireTokenAsync);
        live.MapPost("", (RunHost host) => host.StartLive()
            ? (IResult)TypedResults.Accepted("/runs/current", host.Status())
            : Problem(StatusCodes.Status409Conflict, "live_unavailable", "The live marketplace can't start."));
        live.MapDelete("", (RunHost host) => host.PauseLive(LivePause)
            ? (IResult)TypedResults.Accepted("/runs/current", host.Status())
            : Problem(StatusCodes.Status409Conflict, "live_not_running", "The live marketplace isn't running."));
        if (settings.StartLive)
        {
            app.Services.GetRequiredService<RunHost>().StartLive();
        }

        return app;
    }

    /// <summary><c>simulator health</c>: asks the running control service whether it is serving (the chiseled image has no curl).</summary>
    public static async Task<int> ProbeAsync()
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(PortSeparators)[0] ?? "8080";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/health"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    private static IResult Start(StartRequest body, RunHost host)
    {
        if (body.Profile is not { } name || Profile.Named(name) is not { } profile)
        {
            return Problem(StatusCodes.Status400BadRequest, "unknown_profile", "Choose smoke, realistic or hot-burst.");
        }

        return host.TryStart(profile) is { } status
            ? (IResult)TypedResults.Accepted("/runs/current", status)
            : Problem(StatusCodes.Status409Conflict, "run_in_progress", "Another simulator run is in progress.");
    }

    private static async ValueTask<object?> RequireTokenAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<ControlSettings>().Token;
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        var given = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected))
            ? await next(context)
            : (object)Problem(StatusCodes.Status401Unauthorized, "token_required", "Send the control token.");
    }

    private static ProblemHttpResult Problem(int status, string code, string detail) =>
        TypedResults.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
