using System.Globalization;
using System.Text.Json;
using Auction.Simulator;
using Auction.Simulator.Control;

// simulator run <smoke|realistic|hot-burst> --base-url URL [--seed N] [--report FILE] [--duration SECONDS] [--no-wait-close]
//
// Environment: SIMULATOR_PASSWORD (the seeded simulator accounts), BASE_URL when --base-url is absent, and, for the invariant check,
// VERIFIER_CONNECTION_STRING (the read-only verifier role). Exit codes: 0 the run made progress and every
// invariant held; 1 an invariant was violated, nothing was accepted, or it couldn't be verified; 2 bad input
// or another run in progress.
//
// simulator serve: the control endpoint for the admin area (ADM-02), with SIMULATOR_CONTROL_TOKEN as its bearer token.
// simulator health: probes a running control endpoint (for the Compose healthcheck).
if (args is ["health"] or ["serve", ..])
{
    return await ControlServer.RunCommandAsync(args);
}

if (args is not ["run", var name, .. var rest] || Profile.Named(name) is not { } profile)
{
    await Console.Error.WriteLineAsync("Usage: simulator run <smoke|realistic|hot-burst> [--base-url URL] [--seed N] [--report FILE] [--duration SECONDS] [--no-wait-close]");
    await Console.Error.WriteLineAsync("       simulator serve | simulator health");
    return 2;
}

var password = Environment.GetEnvironmentVariable("SIMULATOR_PASSWORD");
if (string.IsNullOrEmpty(password))
{
    await Console.Error.WriteLineAsync("SIMULATOR_PASSWORD is not set.");
    return 2;
}

string? Option(string flag)
{
    var at = Array.IndexOf(rest, flag);
    return at >= 0 && at + 1 < rest.Length ? rest[at + 1] : null;
}

if ((Option("--base-url") ?? Environment.GetEnvironmentVariable("BASE_URL")) is not { Length: > 0 } target
    || !Uri.TryCreate(target, UriKind.Absolute, out var baseUrl))
{
    await Console.Error.WriteLineAsync("Give the site with --base-url or BASE_URL (deploy/.env sets it).");
    return 2;
}

var seed = int.TryParse(Option("--seed"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 1;
var options = new SimulationOptions(baseUrl, password, seed)
{
    VerifierConnectionString = Environment.GetEnvironmentVariable("VERIFIER_CONNECTION_STRING"),
    Duration = int.TryParse(Option("--duration"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds) : null,
    WaitForClose = !rest.Contains("--no-wait-close"),
};

// One run at a time (SIM-05): runs share the simulator accounts, and a second sign-in would end the first's sessions.
using var runLock = RunLock.TryTake();
if (runLock is null)
{
    await Console.Error.WriteLineAsync("Another simulator run is in progress.");
    return 2;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

using var runner = new SimulationRunner(profile, options);
var report = await runner.RunAsync(stop.Token);
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
Console.WriteLine(json);
if (Option("--report") is { } path)
{
    await File.WriteAllTextAsync(path, json);
}

return report.Passed ? 0 : 1;
