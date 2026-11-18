using System.Diagnostics;
using Auction.Domain;
using Auction.Engine;
using Auction.IntegrationTests.Database;
using Npgsql;
using static Auction.IntegrationTests.Engine.EngineScenario;

namespace Auction.IntegrationTests.Engine;

/// <summary>Deadlines are judged after the lock, a lock held too long fails fast, and a failed command leaves no trace.</summary>
public sealed class LockingTests(MigratedDatabase db)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_bid_that_waited_for_the_lock_past_the_end_is_refused()
    {
        var engine = db.CreateEngine(ShortAuctions with { LockTimeout = TimeSpan.FromSeconds(30) });
        var now = await db.NowAsync();
        var id = await ActiveAsync(engine, UserId.New(), Terms(now, duration: TimeSpan.FromSeconds(3)));

        await using (var holder = await HoldLockAsync(id))
        {
            var bid = BidAsync(engine, UserId.New(), id, 1500);
            await db.WaitUntilAsync(now.AddSeconds(4));
            Assert.False(bid.IsCompleted, "the bid should still be waiting for the lock");
            await holder.CommitAsync(_ct);

            Assert.Equal(CommandError.AuctionEnded, (await bid).Error);
        }

        Assert.Equal(0, await CountAsync(db, "bids", id));
    }

    [Fact]
    public async Task A_bid_that_waited_for_the_lock_before_the_end_is_accepted()
    {
        var engine = db.CreateEngine(ShortAuctions with { LockTimeout = TimeSpan.FromSeconds(30) });
        var id = await ActiveAsync(db, engine);

        await using (var holder = await HoldLockAsync(id))
        {
            var bid = BidAsync(engine, UserId.New(), id, 1500);
            await Task.Delay(500, _ct);
            await holder.CommitAsync(_ct);

            Assert.Equal(CommandError.None, (await bid).Error);
        }
    }

    [Fact]
    public async Task A_lock_held_too_long_fails_fast_with_a_retryable_error_and_records_nothing()
    {
        var engine = db.CreateEngine(new EngineOptions { LockTimeout = TimeSpan.FromMilliseconds(200), MaxAttempts = 2 });
        var id = await ActiveAsync(db, engine);
        var context = As(UserId.New());

        CommandOutcome busy;
        var clock = Stopwatch.StartNew();
        await using (await HoldLockAsync(id))
        {
            busy = await engine.PlaceBidAsync(context, id, new Money(1500), _ct);
        }

        clock.Stop();
        var retried = await engine.PlaceBidAsync(context, id, new Money(1500), _ct);

        Assert.Equal(CommandError.Busy, busy.Error);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Equal((CommandError.None, false), (retried.Error, retried.Replayed));
    }

    [Fact]
    public async Task A_failure_after_the_bid_is_written_rolls_back_the_whole_command()
    {
        var engine = db.CreateEngine();
        var id = await ActiveAsync(db, engine);
        var context = As(UserId.New());
        await using var fault = await FailOutboxWritesAsync(id);

        await Assert.ThrowsAsync<PostgresException>(() => engine.PlaceBidAsync(context, id, new Money(1500), _ct));

        Assert.Equal(0, await CountAsync(db, "bids", id));
        Assert.Equal(0, await CountAsync(db, "proxy_maxima", id));
        Assert.Equal(1, await CountAsync(db, "outbox_events", id));
        Assert.Equal((1L, 0L), (
            await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT version FROM auctions WHERE id = $1", id.Value),
            await db.ScalarAsync<long>(MigratedDatabase.Verifier, "SELECT accepted_bid_count FROM auctions WHERE id = $1", id.Value)));
        Assert.Equal(0, await db.ScalarAsync<long>(
            MigratedDatabase.Verifier,
            "SELECT count(*) FROM idempotency_records WHERE actor_id = $1 AND key = $2",
            context.Actor.Value,
            context.IdempotencyKey));
    }

    /// <summary>Locks the auction row from another transaction until the returned holder commits or is disposed.</summary>
    private async Task<LockHolder> HoldLockAsync(AuctionId id)
    {
        var connection = await db.OpenAsync(MigratedDatabase.Engine);
        var transaction = await connection.BeginTransactionAsync(_ct);
        await using var command = new NpgsqlCommand("SELECT 1 FROM auctions WHERE id = $1 FOR UPDATE", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = id.Value });
        await command.ExecuteNonQueryAsync(_ct);
        return new LockHolder(connection, transaction);
    }

    /// <summary>A trigger, scoped to one auction, that makes its outbox insert fail after the bid rows are written.</summary>
    private async Task<FaultTrigger> FailOutboxWritesAsync(AuctionId id)
    {
        var name = $"fault_{id.Value:N}";
        await db.ExecuteAsync(
            MigratedDatabase.Migrator,
            $"""
            CREATE FUNCTION auction.{name}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.auction_id = '{id.Value}' THEN RAISE EXCEPTION 'injected fault'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {name} BEFORE INSERT ON auction.outbox_events
                FOR EACH ROW EXECUTE FUNCTION auction.{name}();
            """);
        return new FaultTrigger(db, name);
    }

    private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FaultTrigger(MigratedDatabase db, string name) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() =>
            await db.ExecuteAsync(
                MigratedDatabase.Migrator,
                $"DROP TRIGGER {name} ON auction.outbox_events; DROP FUNCTION auction.{name}();");
    }
}
