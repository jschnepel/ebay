using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Auction.Simulator.Control;

/// <summary>What the control service needs to start a run: the same settings the CLI reads from the environment.</summary>
public sealed record ControlSettings(Uri BaseAddress, string Password, string Token, string? VerifierConnectionString);

public sealed record StartRequest(string? Profile);

/// <summary>How the last finished run ended.</summary>
public sealed record RunOutcome(
    Guid RunId, string Profile, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool StoppedEarly, bool Passed, int Accepted, string? Error);

/// <summary>The control service's state: idle, running or stopping, the current run's progress, and the last outcome.</summary>
public sealed record RunStatus(string State, Guid? RunId, string? Profile, DateTimeOffset? StartedAt, int Attempts, int Accepted, RunOutcome? Last);

/// <summary>
/// Runs at most one profile at a time for the admin area (ADM-02). It also takes the CLI's run lock, so a
/// run started here and one started from the command line never share the simulator accounts (SIM-05).
/// </summary>
public sealed partial class RunHost(ControlSettings settings, ILogger<RunHost> logger) : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Current? _current;
    private RunOutcome? _last;

    public RunStatus Status()
    {
        lock (_gate)
        {
            if (_current is not { } run)
            {
                return new RunStatus("idle", null, null, null, 0, 0, _last);
            }

            var state = run.Runner.Stopped ? "stopping" : "running";
            return new RunStatus(state, run.Id, run.Runner.Profile.Name, run.StartedAt, run.Runner.Attempts, run.Runner.Accepted, _last);
        }
    }

    /// <summary>Starts the profile, or returns null when a run (here or from the CLI) is already in progress.</summary>
    public RunStatus? TryStart(Profile profile)
    {
        lock (_gate)
        {
            if (_current is not null || RunLock.TryTake() is not { } runLock)
            {
                return null;
            }

            var options = new SimulationOptions(settings.BaseAddress, settings.Password, RandomNumberGenerator.GetInt32(1, 1_000_000))
            {
                VerifierConnectionString = settings.VerifierConnectionString,
            };
            var run = new Current(Guid.NewGuid(), new SimulationRunner(profile, options), runLock, DateTimeOffset.UtcNow);
            _current = run;
            LogStarted(logger, run.Id, profile.Name, options.Seed);
            // Never cancelled before it starts: the run must always release its lock and record its outcome.
            run.Task = Task.Run(() => RunAsync(run), CancellationToken.None);
        }

        return Status();
    }

    /// <summary>Ends the current run's load early; false when nothing is running.</summary>
    public bool Stop()
    {
        lock (_gate)
        {
            if (_current is not { } run)
            {
                return false;
            }

            run.Runner.Stop();
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        Task? running;
        lock (_gate)
        {
            running = _current?.Task;
        }

        if (running is not null)
        {
            await running;
        }

        _shutdown.Dispose();
    }

    private async Task RunAsync(Current run)
    {
        RunOutcome outcome;
        try
        {
            var report = await run.Runner.RunAsync(_shutdown.Token);
            outcome = Outcome(run, report.Passed, report.SetupError ?? FailureOf(report));
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or NpgsqlException or TimeoutException or System.Text.Json.JsonException)
        {
            LogFailed(logger, run.Id, e);
            outcome = Outcome(run, passed: false, e.Message);
        }

        LogFinished(logger, run.Id, outcome.Passed, outcome.Accepted);
        lock (_gate)
        {
            _current = null;
            _last = outcome;
        }

        run.Lock.Dispose();
        run.Runner.Dispose();
    }

    private static RunOutcome Outcome(Current run, bool passed, string? error) =>
        new(run.Id, run.Runner.Profile.Name, run.StartedAt, DateTimeOffset.UtcNow, run.Runner.Stopped, passed, run.Runner.Accepted, error);

    private static string? FailureOf(SimulationReport report) => report switch
    {
        { Passed: true } => null,
        { Actual.Accepted: 0 } => "No bid was accepted.",
        { Verification: null } => "The run was not verified.",
        { Verification.Violations.Count: var count } => $"The verifier found {count} violation(s).",
    };

    [LoggerMessage(1, LogLevel.Information, "Simulator run {RunId} started: {Profile}, seed {Seed}")]
    private static partial void LogStarted(ILogger logger, Guid runId, string profile, int seed);

    [LoggerMessage(2, LogLevel.Information, "Simulator run {RunId} finished: passed {Passed}, {Accepted} bids accepted")]
    private static partial void LogFinished(ILogger logger, Guid runId, bool passed, int accepted);

    [LoggerMessage(3, LogLevel.Warning, "Simulator run {RunId} failed")]
    private static partial void LogFailed(ILogger logger, Guid runId, Exception exception);

    private sealed class Current(Guid id, SimulationRunner runner, RunLock runLock, DateTimeOffset startedAt)
    {
        public Guid Id { get; } = id;

        public SimulationRunner Runner { get; } = runner;

        public RunLock Lock { get; } = runLock;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public Task? Task { get; set; }
    }
}
