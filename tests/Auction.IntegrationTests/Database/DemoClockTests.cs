using System.Net;
using Auction.IntegrationTests.Api;
using Auction.Migrator;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Auction.IntegrationTests.Database;

/// <summary>
/// The demo's daily and on-demand reset (ADM-04): <c>migrator demo-clock</c> runs reset-demo at a fixed UTC time
/// and whenever an admin's request is waiting, then clears Redis so nobody stays signed in on a cached session.
/// </summary>
public sealed class DemoClockTests(ResetDatabase own) : IClassFixture<ResetDatabase>
{
    private static readonly DemoSeedOptions Seed = new("admin-pass-for-tests", "demo-pass-for-tests", "simulator-pass-for-tests");
    private static readonly TimeOnly NineUtc = new(9, 0);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MigratedDatabase Db => own.Db;

    [Fact]
    public void The_next_reset_is_the_next_0900_utc_and_a_missed_one_runs_once()
    {
        var today = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        var nineToday = today.AddHours(9);

        Assert.Equal(nineToday, DemoClock.NextScheduled(today.AddHours(8).AddMinutes(59), NineUtc, lastCompleted: null));
        Assert.Equal(nineToday.AddDays(1), DemoClock.NextScheduled(today.AddHours(9).AddMinutes(1), NineUtc, lastCompleted: nineToday));
        Assert.Equal(nineToday, DemoClock.NextScheduled(today.AddHours(9).AddMinutes(1), NineUtc, lastCompleted: nineToday.AddDays(-1)));
        // A fresh install has never reset: it waits for the next 09:00 instead of resetting at once.
        Assert.Equal(nineToday.AddDays(1), DemoClock.NextScheduled(today.AddHours(10), NineUtc, lastCompleted: null));
    }

    [Fact]
    public async Task A_waiting_request_resets_the_demo_and_is_marked_complete()
    {
        await FreshAsync();
        var visitor = await (await Db.ApiAsync()).SignUpAsync("Leaves at the reset");
        var request = await RequestAsync("Visiting admin");

        Assert.True(await TickAsync());

        Assert.False(await Db.ScalarAsync<bool>(MigratedDatabase.Migrator, "SELECT completed_at IS NULL FROM demo_resets WHERE id = $1", request));
        // The requesting visitor's name goes with the reset, like everything else visitors leave behind.
        Assert.True(await Db.ScalarAsync<bool>(MigratedDatabase.Migrator, "SELECT requested_by IS NULL FROM demo_resets WHERE id = $1", request));
        Assert.Equal(0L, await Db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM users WHERE id = $1", visitor.Id));
        Assert.False(await TickAsync());
    }

    [Fact]
    public async Task A_clock_that_has_never_reset_runs_the_first_scheduled_reset_once_0900_utc_passes()
    {
        await FreshAsync();
        var day = DateTimeOffset.UtcNow.UtcDateTime.Date;
        var started = new DateTimeOffset(day.AddHours(8), TimeSpan.Zero);
        var clock = new ManualClock(started.AddMinutes(59));

        Assert.False(await TickAsync(clock, started));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await TickAsync(clock, started));
        Assert.False(await TickAsync(clock, started));
    }

    [Fact]
    public async Task After_a_reset_an_old_session_cookie_is_refused_at_once()
    {
        await FreshAsync();
        var visitor = await (await Db.ApiAsync()).SignUpAsync("Cached visitor");
        Assert.Equal(HttpStatusCode.OK, await visitor.Http.StatusOfGetAsync("/api/v1/account/me"));
        await RequestAsync("Visiting admin");

        Assert.True(await TickAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, await visitor.Http.StatusOfGetAsync("/api/v1/account/me"));
    }

    [Fact]
    public async Task Only_one_request_can_wait_at_a_time()
    {
        await FreshAsync();
        await RequestAsync("First admin");

        var second = await Assert.ThrowsAsync<PostgresException>(() => RequestAsync("Second admin"));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, second.SqlState);
    }

    private async Task FreshAsync()
    {
        await DemoSeed.ResetAsync(Db.ConnectionStringFor(MigratedDatabase.Migrator), Seed, Ct);
        await Db.ExecuteAsync(MigratedDatabase.Migrator, "DELETE FROM demo_resets");
    }

    // As the API records it: the requesting admin's display name, no completion yet.
    private async Task<Guid> RequestAsync(string by)
    {
        var id = Guid.NewGuid();
        await Db.ExecuteAsync(MigratedDatabase.Api, "INSERT INTO demo_resets (id, requested_at, requested_by) VALUES ($1, now(), $2)", id, by);
        return id;
    }

    private Task<bool> TickAsync() => TickAsync(TimeProvider.System, DateTimeOffset.UtcNow);

    // The clock started at <paramref name="started"/>; it is now whatever <paramref name="time"/> says.
    private Task<bool> TickAsync(TimeProvider time, DateTimeOffset started) => DemoClock.TickAsync(
        new DemoClockOptions(Db.ConnectionStringFor(MigratedDatabase.Migrator), Db.RedisConnectionString, Seed, NineUtc),
        started, time, NullLogger.Instance, Ct);

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
