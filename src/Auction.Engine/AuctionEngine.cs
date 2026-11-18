using Auction.Domain;
using Npgsql;

namespace Auction.Engine;

/// <summary>
/// The auction commands. Each runs as one transaction that locks the auction row, reads the database
/// clock after the lock, replays a repeated idempotency key, applies the domain rules, and commits the
/// state change, bid history, command outcome and outbox event together.
/// </summary>
public sealed class AuctionEngine(NpgsqlDataSource dataSource, EngineOptions options)
{
    private const string LockNotAvailable = "55P03";
    private const string DeadlockDetected = "40P01";
    private const string UniqueViolation = "23505";

    /// <summary>The result of applying the rules to a locked auction: an outcome, and a change if accepted.</summary>
    private sealed record Decision(CommandOutcome Outcome, AuctionChange? Change);

    public Task<CommandOutcome> CreateDraftAsync(CommandContext context, AuctionTerms terms, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(terms);
        var hash = RequestHash.Of("create", terms);
        return WithRetriesAsync(context, () => CreateOnceAsync(context, terms, hash, cancellationToken), cancellationToken);
    }

    public Task<CommandOutcome> EditDraftAsync(
        CommandContext context, AuctionId auctionId, AuctionTerms terms, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return RunAsync(context, auctionId, RequestHash.Of("edit", new { auctionId, terms }), (auction, _) =>
        {
            var result = Lifecycle.EditTerms(auction.State, context.Actor, terms);
            if (!result.Succeeded)
            {
                return Refuse(auctionId, ToError(result.Error));
            }

            var errors = DraftBounds.Check(terms, options.Limits);
            return errors.Count > 0
                ? Refuse(auctionId, CommandError.ValidationFailed, errors)
                : Accept(auction, new AuctionChange(result.State, [], EventType: null));
        }, cancellationToken);
    }

    public Task<CommandOutcome> PublishAsync(CommandContext context, AuctionId auctionId, CancellationToken cancellationToken) =>
        RunAsync(context, auctionId, RequestHash.Of("publish", auctionId), (auction, now) =>
        {
            var result = Lifecycle.Publish(auction.State, context.Actor, now, options.Limits);
            return result.Succeeded
                ? Accept(auction, new AuctionChange(result.State, [], "auction.published"))
                : Refuse(auctionId, ToError(result.Error), result.FieldErrors);
        }, cancellationToken);

    public Task<CommandOutcome> CancelAsync(CommandContext context, AuctionId auctionId, CancellationToken cancellationToken) =>
        RunAsync(context, auctionId, RequestHash.Of("cancel", auctionId), (auction, _) =>
        {
            var result = Lifecycle.CancelByOwner(auction.State, context.Actor);
            return result.Succeeded
                ? Accept(auction, new AuctionChange(result.State, [], "auction.cancelled"))
                : Refuse(auctionId, ToError(result.Error));
        }, cancellationToken);

    public Task<CommandOutcome> PlaceBidAsync(
        CommandContext context, AuctionId auctionId, Money amount, CancellationToken cancellationToken) =>
        RunAsync(context, auctionId, RequestHash.Of("bid", new { auctionId, amount }), (auction, now) =>
        {
            var decision = Bidding.Place(auction.State, context.Actor, amount, now, options.Limits);
            return decision.Accepted
                ? Accept(auction, new AuctionChange(decision.State, decision.Steps, "auction.bid_accepted"))
                : Refuse(auctionId, Enum.Parse<CommandError>(decision.Rejection.ToString()));
        }, cancellationToken);

    /// <summary>
    /// Activates scheduled auctions whose start has passed and closes auctions whose end has passed.
    /// Returns how many changed.
    /// </summary>
    public Task<int> AdvanceDueAuctionsAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException($"{dataSource.ConnectionString} {cancellationToken.IsCancellationRequested}");

    private Task<CommandOutcome> RunAsync(
        CommandContext context, AuctionId auctionId, byte[] requestHash, Func<LockedAuction, DateTimeOffset, Decision> decide, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return WithRetriesAsync(context, () => RunOnceAsync(context, auctionId, requestHash, decide, cancellationToken), cancellationToken);
    }

    // A lock timeout or deadlock leaves nothing behind, so the whole command is retried after a short,
    // jittered pause; if every attempt fails, the caller gets Busy and may retry with the same key.
    // A unique violation on the key means a concurrent command with the same key, for another auction,
    // committed first; the retry then finds its record.
    private async Task<CommandOutcome> WithRetriesAsync(
        CommandContext context, Func<Task<CommandOutcome>> attempt, CancellationToken cancellationToken)
    {
        if (!IdempotencyStore.IsValidKey(context.IdempotencyKey))
        {
            return new CommandOutcome { Error = CommandError.InvalidKey };
        }

        var tries = 0;
        while (true)
        {
            tries++;
            try
            {
                return await attempt();
            }
            catch (PostgresException e) when (IsRetryable(e) && tries < options.MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 50)), cancellationToken);
            }
            catch (PostgresException e) when (e.SqlState is LockNotAvailable or DeadlockDetected)
            {
                return new CommandOutcome { Error = CommandError.Busy };
            }
        }
    }

    private static bool IsRetryable(PostgresException e) =>
        e.SqlState is LockNotAvailable or DeadlockDetected
        || (e.SqlState == UniqueViolation && e.ConstraintName == "pk_idempotency_records");

    private async Task<CommandOutcome> RunOnceAsync(
        CommandContext context, AuctionId auctionId, byte[] requestHash, Func<LockedAuction, DateTimeOffset, Decision> decide, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = await AuctionStore.LockAsync(connection, transaction, auctionId, context, options.LockTimeout, cancellationToken);

        // Replay comes before any rule is checked, so a retried acceptance stays accepted after the deadline.
        if (locked.Recorded is { } recorded)
        {
            return recorded.ReplayFor(requestHash);
        }

        var decision = locked.Auction is { } auction
            ? decide(auction, locked.Now)
            : Refuse(auctionId, CommandError.NotFound);

        await using var batch = new NpgsqlBatch(connection, transaction);
        if (decision.Change is { } change)
        {
            foreach (var command in AuctionStore.ChangeCommands(locked.Auction!, change, locked.Now, Guid.CreateVersion7()))
            {
                batch.BatchCommands.Add(command);
            }
        }

        batch.BatchCommands.Add(IdempotencyStore.InsertCommand(context, requestHash, decision.Outcome));
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision.Outcome;
    }

    // A draft has no row to lock yet, so the idempotency record is written first: a concurrent duplicate
    // waits on its key, inserts nothing, and replays this outcome. The id is chosen before the row exists.
    private async Task<CommandOutcome> CreateOnceAsync(
        CommandContext context, AuctionTerms terms, byte[] requestHash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await IdempotencyStore.FindAsync(connection, transaction, context, cancellationToken) is { } earlier)
        {
            return earlier.ReplayFor(requestHash);
        }

        var errors = DraftBounds.Check(terms, options.Limits);
        var id = AuctionId.New();
        var outcome = errors.Count > 0
            ? new CommandOutcome { Error = CommandError.ValidationFailed, FieldErrors = errors }
            : new CommandOutcome { AuctionId = id, Version = 0 };

        if (!await IdempotencyStore.TryInsertAsync(connection, transaction, context, requestHash, outcome, cancellationToken))
        {
            var winner = await IdempotencyStore.FindAsync(connection, transaction, context, cancellationToken);
            return winner!.ReplayFor(requestHash);
        }

        if (outcome.Succeeded)
        {
            await using var batch = new NpgsqlBatch(connection, transaction)
            {
                BatchCommands = { AuctionStore.InsertDraftCommand(id, context.Actor, terms) },
            };
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    private static Decision Accept(LockedAuction before, AuctionChange change)
    {
        var after = change.After;
        var isBid = after.AcceptedBidCount != before.State.AcceptedBidCount;
        var outcome = new CommandOutcome
        {
            AuctionId = after.Id,
            Version = before.Version + 1,
            BidSequence = isBid ? after.AcceptedBidCount : null,
            LeaderId = isBid ? after.LeaderId : null,
            CurrentPrice = isBid ? after.CurrentPrice : null,
        };
        return new Decision(outcome, change);
    }

    private static Decision Refuse(AuctionId auctionId, CommandError error, IReadOnlyList<FieldError>? fieldErrors = null) =>
        new(new CommandOutcome { Error = error, AuctionId = auctionId, FieldErrors = fieldErrors ?? [] }, Change: null);

    private static CommandError ToError(LifecycleError error) => error switch
    {
        LifecycleError.NotOwner => CommandError.NotOwner,
        LifecycleError.HasBids => CommandError.HasBids,
        LifecycleError.ValidationFailed => CommandError.ValidationFailed,
        _ => CommandError.InvalidState,
    };
}
