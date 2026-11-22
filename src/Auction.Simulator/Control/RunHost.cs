using System.Security.Cryptography;
using Auction.Simulator.Live;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Auction.Simulator.Control;

/// <summary>What the control service needs to start a run: the same settings the CLI reads from the environment.</summary>
public sealed record ControlSettings(Uri BaseAddress, string Password, string Token, string? VerifierConnectionString)
{
    /// <summary>The live marketplace's size and pace.</summary>
    public LiveOptions LiveOptions { get; init; } = new();

    /// <summary>Starts the live marketplace with the service (LIVE_SIMULATION=true, the demo's setting).</summary>
    public bool StartLive { get; init; }
}

public sealed record StartRequest(string? Profile);

/// <summary>How the last finished run ended.</summary>
public sealed record RunOutcome(
    Guid RunId, string Profile, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool StoppedEarly, bool Passed, int Accepted, string? Error);

/// <summary>
/// The control service's state: idle, running or stopping, the current run's progress, the last outcome,
/// and the live marketplace (null until it has been started).
/// </summary>
public sealed record RunStatus(string State, Guid? RunId, string? Profile, DateTimeOffset? StartedAt, int Attempts, int Accepted, RunOutcome? Last)
{
    public LiveStatus? Live { get; init; }
}

/// <summary>
/// Runs at most one simulator workload at a time (SIM-05): a bounded profile for the admin area (ADM-02), or
/// the always-on live marketplace. Both use the simulator accounts, so they never overlap: starting a profile
/// pauses the live marketplace, and it resumes when the profile ends. An admin's pause lasts at most the time
/// given, after which the live marketplace resumes on its own. The CLI's run lock is held throughout, so a
/// command-line run never overlaps either.
/// </summary>
public sealed partial class RunHost : IAsyncDisposable
{
    private static readonly TimeSpan SupervisorTick = TimeSpan.FromSeconds(1);

    private readonly ControlSettings _settings;
    private readonly ILogger<RunHost> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _supervisor;
    private Current? _current;
    private RunOutcome? _last;

    // The live marketplace: wanted at all, paused by an admin until a time, and its current run, if any.
    private LiveMarketplace? _live;
    private bool _liveWanted;
    private DateTimeOffset? _livePausedUntil;
    private LiveRun? _liveRun;
    private Task _liveStopping = Task.CompletedTask;

    public RunHost(ControlSettings settings, ILogger<RunHost> logger, TimeProvider time)
    {
        (_settings, _logger, _time) = (settings, logger, time);
        _supervisor = Task.Run(SuperviseAsync, CancellationToken.None);
    }

    public RunStatus Status()
    {
        lock (_gate)
        {
            var live = LiveStatusLocked();
            if (_current is not { } run)
            {
                return new RunStatus("idle", null, null, null, 0, 0, _last) { Live = live };
            }

            var state = run.Runner.Stopped ? "stopping" : "running";
            return new RunStatus(state, run.Id, run.Runner.Profile.Name, run.StartedAt, run.Runner.Attempts, run.Runner.Accepted, _last) { Live = live };
        }
    }

    /// <summary>
    /// Starts the profile, or returns null when another profile (here or from the CLI) is in progress. A running
    /// live marketplace hands over its lock and stops first; it resumes when the profile ends.
    /// </summary>
    public RunStatus? TryStart(Profile profile)
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                return null;
            }

            var handover = _liveRun;
            var runLock = handover?.Lock ?? RunLock.TryTake();
            if (runLock is null)
            {
                return null;
            }

            _liveRun = null;
            var stopping = handover is null ? Task.CompletedTask : StopAsync(handover, releaseLock: false);
            var options = new SimulationOptions(_settings.BaseAddress, _settings.Password, RandomNumberGenerator.GetInt32(1, 1_000_000))
            {
                VerifierConnectionString = _settings.VerifierConnectionString,
            };
            var run = new Current(Guid.NewGuid(), new SimulationRunner(profile, options), runLock, _time.GetUtcNow());
            _current = run;
            LogStarted(_logger, run.Id, profile.Name, options.Seed);
            // Never cancelled before it starts: the run must always release its lock and record its outcome.
#pragma warning disable VSTHRD003 // the stopping task is this host's own
            run.Task = Task.Run(() => RunAsync(run, stopping), CancellationToken.None);
#pragma warning restore VSTHRD003
        }

        return Status();
    }

    /// <summary>Ends the current profile's load early; false when no profile is running.</summary>
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

    /// <summary>Starts or resumes the live marketplace; it waits for a running profile to end.</summary>
    public bool StartLive()
    {
        lock (_gate)
        {
            _liveWanted = true;
            _livePausedUntil = null;
            TryRunLiveLocked();
            return true;
        }
    }

    /// <summary>Pauses the live marketplace for at most <paramref name="upTo"/>; false when it isn't wanted.</summary>
    public bool PauseLive(TimeSpan upTo)
    {
        lock (_gate)
        {
            if (!_liveWanted)
            {
                return false;
            }

            _livePausedUntil = _time.GetUtcNow() + upTo;
            _liveStopping = StopLiveLockedAsync();
        }

        LogLivePaused(_logger, upTo);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        Task? running;
        Task live;
        lock (_gate)
        {
            running = _current?.Task;
            live = StopLiveLockedAsync();
        }

#pragma warning disable VSTHRD003 // the supervisor is this host's own task, started in its constructor
        await _supervisor;
#pragma warning restore VSTHRD003
        if (running is not null)
        {
            await running;
        }

        await live;
#pragma warning disable VSTHRD003 // this host's own stop
        await _liveStopping;
#pragma warning restore VSTHRD003

        _live?.Dispose();
        _shutdown.Dispose();
    }

    private LiveStatus? LiveStatusLocked()
    {
        if (!_liveWanted || _live is null)
        {
            return _liveWanted ? new LiveStatus("paused", null, _livePausedUntil, 0, 0, null, null) : null;
        }

        var status = _live.Status();
        return _liveRun is not null
            ? status with { State = "running" }
            : status with { State = "paused", ResumesAt = _livePausedUntil };
    }

    // Runs the live marketplace when it is wanted, not paused, no profile is running and the lock is free.
    private void TryRunLiveLocked()
    {
        if (!_liveWanted || _livePausedUntil is not null || _current is not null || _liveRun is not null || _shutdown.IsCancellationRequested)
        {
            return;
        }

        if (RunLock.TryTake() is not { } runLock)
        {
            return;
        }

        try
        {
            _live ??= new LiveMarketplace(
                _settings.LiveOptions,
                new SimulationOptions(_settings.BaseAddress, _settings.Password, RandomNumberGenerator.GetInt32(1, 1_000_000))
                {
                    VerifierConnectionString = _settings.VerifierConnectionString,
                },
                _time,
                _logger);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A marketplace that can't start (its catalogue missing, say) must not hold the lock or stop the service.
            runLock.Dispose();
            LogSuperviseFailed(_logger, e);
            return;
        }

        var stop = new CancellationTokenSource();
        var live = _live;
        var run = new LiveRun(runLock, stop);
        run.Task = Task.Run(() => live.RunAsync(stop.Token), CancellationToken.None);
        _liveRun = run;
        LogLiveStarted(_logger);
    }

    // Stops the live run and releases its lock once it has stopped; returns the stopping task.
    private Task StopLiveLockedAsync()
    {
        if (_liveRun is not { } run)
        {
            return Task.CompletedTask;
        }

        _liveRun = null;
        return StopAsync(run, releaseLock: true);
    }

    // Cancels a live run, waits for it to end, and releases its lock unless a profile is taking it over.
    private static async Task StopAsync(LiveRun run, bool releaseLock)
    {
        try
        {
            await run.Stop.CancelAsync();
#pragma warning disable VSTHRD003 // the live run this host started
            await run.Task!;
#pragma warning restore VSTHRD003
        }
        catch (Exception)
        {
            // It stopped, or had already failed: either way it is over, and its lock is released below.
        }
        finally
        {
            run.Stop.Dispose();
            if (releaseLock)
            {
                run.Lock.Dispose();
            }
        }
    }

    // Resumes the live marketplace when an admin's pause ends, or when its lock comes free.
    private async Task SuperviseAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SupervisorTick, _shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    if (_livePausedUntil is { } until && _time.GetUtcNow() >= until)
                    {
                        _livePausedUntil = null;
                    }

                    // A live run that ended by itself (it never should) is released and started again.
                    if (_liveRun is { Task.IsCompleted: true })
                    {
                        _liveStopping = StopLiveLockedAsync();
                    }

                    TryRunLiveLocked();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The next tick tries again; the supervisor itself must never end.
                LogSuperviseFailed(_logger, e);
            }
        }
    }

    private async Task RunAsync(Current run, Task handover)
    {
        RunOutcome outcome;
        try
        {
            // The live marketplace's run, started by this host, stops before the profile uses the accounts.
#pragma warning disable VSTHRD003
            await handover;
#pragma warning restore VSTHRD003
            var report = await run.Runner.RunAsync(_shutdown.Token);
            outcome = Outcome(run, report.Passed, report.SetupError ?? FailureOf(report));
        }
        catch (Exception e) when (e is not OperationCanceledException || _shutdown.IsCancellationRequested)
        {
            // Whatever went wrong, the run must end: record it, release the lock and let live mode resume.
            LogFailed(_logger, run.Id, e);
            outcome = Outcome(run, passed: false, e.Message);
        }

        LogFinished(_logger, run.Id, outcome.Passed, outcome.Accepted);
        lock (_gate)
        {
            _current = null;
            _last = outcome;
            run.Lock.Dispose();
            TryRunLiveLocked();
        }

        run.Runner.Dispose();
    }

    private RunOutcome Outcome(Current run, bool passed, string? error) =>
        new(run.Id, run.Runner.Profile.Name, run.StartedAt, _time.GetUtcNow(), run.Runner.Stopped, passed, run.Runner.Accepted, error);

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

    [LoggerMessage(4, LogLevel.Information, "Live marketplace running")]
    private static partial void LogLiveStarted(ILogger logger);

    [LoggerMessage(6, LogLevel.Warning, "Could not start the live marketplace; trying again")]
    private static partial void LogSuperviseFailed(ILogger logger, Exception exception);

    [LoggerMessage(5, LogLevel.Information, "Live marketplace paused for up to {Pause}")]
    private static partial void LogLivePaused(ILogger logger, TimeSpan pause);

    private sealed class Current(Guid id, SimulationRunner runner, RunLock runLock, DateTimeOffset startedAt)
    {
        public Guid Id { get; } = id;

        public SimulationRunner Runner { get; } = runner;

        public RunLock Lock { get; } = runLock;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public Task? Task { get; set; }
    }

    private sealed class LiveRun(RunLock runLock, CancellationTokenSource stop)
    {
        public RunLock Lock { get; } = runLock;

        public CancellationTokenSource Stop { get; } = stop;

        public Task? Task { get; set; }
    }
}
