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
    private const string ForeignKeyViolation = "23503";
    private const string SweptImage = "fk_auction_images_image_blobs_blob_id";
    private const int KeySweepBatchSize = 1_000;

    public const int ReasonMaxLength = 500;

    /// <summary>The result of applying the rules to a locked auction: an outcome, and a change if accepted.</summary>
    private sealed record Decision(CommandOutcome Outcome, AuctionChange? Change);

    /// <summary>An admin moderation command: its audit name, the reason, the lifecycle rule and its event.</summary>
    private sealed record Moderation(string Action, string Reason, Func<AuctionState, DateTimeOffset, LifecycleResult> Rule, string EventType);

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
        RunLoadedAsync(context, auctionId, RequestHash.Of("publish", auctionId), async (db, auction, now, ct) =>
        {
            var result = Lifecycle.Publish(auction.State, context.Actor, now, options.Limits);
            if (!result.Succeeded)
            {
                return Refuse(auctionId, ToError(result.Error), result.FieldErrors);
            }

            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var publishedToday = await SellerLimits.PublishedOnAsync(db, auction.State.SellerId, today, ct);
            return publishedToday >= options.MaxPublishesPerDay
                ? Refuse(auctionId, CommandError.PublishLimit)
                : Accept(auction, new AuctionChange(result.State, [], "auction.published") { Extra = [SellerLimits.CountPublish(auction.State.SellerId, today)] });
        }, cancellationToken);

    /// <summary>Links an image the seller uploaded to their draft, under the auction lock (UI-05).</summary>
    public Task<CommandOutcome> AttachImageAsync(CommandContext context, AuctionId auctionId, Guid blobId, CancellationToken cancellationToken) =>
        ImageCommandAsync(context, auctionId, RequestHash.Of("attach", new { auctionId, blobId }), async (db, images, ct) =>
        {
            if (images.Exists(i => i.BlobId == blobId))
            {
                return (CommandError.InvalidState, null);
            }

            if (await AuctionImages.OwnerOfAsync(db, blobId, ct) != context.Actor.Value)
            {
                return (CommandError.NotFound, null);
            }

            return images.Count >= options.MaxImages
                ? (CommandError.ImageLimit, null)
                : (CommandError.None, AuctionImages.Attach(auctionId, blobId, images.Count, isCover: images.Count == 0));
        }, cancellationToken);

    public Task<CommandOutcome> RemoveImageAsync(CommandContext context, AuctionId auctionId, Guid blobId, CancellationToken cancellationToken) =>
        ImageCommandAsync(context, auctionId, RequestHash.Of("detach", new { auctionId, blobId }), (_, images, _) =>
            Task.FromResult(images.Exists(i => i.BlobId == blobId)
                ? (CommandError.None, AuctionImages.Remove(auctionId, blobId))
                : (CommandError.NotFound, (IReadOnlyList<NpgsqlBatchCommand>?)null)), cancellationToken);

    /// <summary>Puts the draft's images in the given order; the list must name exactly its images.</summary>
    public Task<CommandOutcome> ReorderImagesAsync(CommandContext context, AuctionId auctionId, IReadOnlyList<Guid> order, CancellationToken cancellationToken) =>
        ImageCommandAsync(context, auctionId, RequestHash.Of("reorder", new { auctionId, order }), (_, images, _) =>
            Task.FromResult(order.Count == images.Count && order.Distinct().Count() == order.Count && order.All(id => images.Exists(i => i.BlobId == id))
                ? (CommandError.None, AuctionImages.Reorder(auctionId, order))
                : (CommandError.ValidationFailed, (IReadOnlyList<NpgsqlBatchCommand>?)null)), cancellationToken);

    public Task<CommandOutcome> SetCoverAsync(CommandContext context, AuctionId auctionId, Guid blobId, CancellationToken cancellationToken) =>
        ImageCommandAsync(context, auctionId, RequestHash.Of("cover", new { auctionId, blobId }), (_, images, _) =>
            Task.FromResult(images.Exists(i => i.BlobId == blobId)
                ? (CommandError.None, AuctionImages.SetCover(auctionId, blobId))
                : (CommandError.NotFound, (IReadOnlyList<NpgsqlBatchCommand>?)null)), cancellationToken);

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
            if (!decision.Accepted)
            {
                return Refuse(auctionId, Enum.Parse<CommandError>(decision.Rejection.ToString()));
            }

            // Only the bidder who just lost the lead is told; a bid outbid at once changes no leader.
            var previous = auction.State.LeaderId;
            Notice[] notices = previous is { } loser && loser != decision.State.LeaderId ? [Notice.To(loser, "outbid")] : [];
            return Accept(auction, new AuctionChange(decision.State, decision.Steps, "auction.bid_accepted") { Notices = notices });
        }, cancellationToken);

    /// <summary>The owner removes their draft. Anyone else gets "not found", as for any draft.</summary>
    public Task<CommandOutcome> DeleteDraftAsync(CommandContext context, AuctionId auctionId, CancellationToken cancellationToken) =>
        RunAsync(context, auctionId, RequestHash.Of("delete", auctionId), (auction, _) =>
        {
            var result = Lifecycle.DeleteDraft(auction.State, context.Actor);
            return result.Succeeded
                ? Accept(auction, new AuctionChange(result.State, [], EventType: null) { DeletesDraft = true })
                : Refuse(auctionId, ToError(result.Error));
        }, cancellationToken);

    /// <summary>Admin: Active to Suspended, with a reason the seller and bidders are told and the audit log keeps.</summary>
    public Task<CommandOutcome> SuspendAsync(CommandContext context, AuctionId auctionId, string reason, CancellationToken cancellationToken) =>
        ModerateAsync(context, auctionId, new Moderation("suspend", reason, (state, _) => Lifecycle.Suspend(state), "auction.suspended"), cancellationToken);

    /// <summary>Admin: Suspended back to Active, before the end.</summary>
    public Task<CommandOutcome> ResumeAsync(CommandContext context, AuctionId auctionId, string reason, CancellationToken cancellationToken) =>
        ModerateAsync(context, auctionId, new Moderation("resume", reason, Lifecycle.Resume, "auction.resumed"), cancellationToken);

    /// <summary>Admin: cancels a Scheduled, Active or Suspended auction, even after bids; the history stays.</summary>
    public Task<CommandOutcome> AdminCancelAsync(CommandContext context, AuctionId auctionId, string reason, CancellationToken cancellationToken) =>
        ModerateAsync(context, auctionId, new Moderation("cancel", reason, (state, _) => Lifecycle.CancelByAdmin(state), "auction.cancelled"), cancellationToken);

    private Task<CommandOutcome> ModerateAsync(CommandContext context, AuctionId auctionId, Moderation moderation, CancellationToken cancellationToken)
    {
        var reason = moderation.Reason.Trim();
        return RunAsync(context, auctionId, RequestHash.Of(moderation.Action, new { auctionId, reason }), (auction, now) =>
        {
            if (reason.Length is 0 or > ReasonMaxLength)
            {
                return Refuse(auctionId, CommandError.ValidationFailed, [new FieldError("reason", reason.Length == 0 ? "required" : "too_long")]);
            }

            var result = moderation.Rule(auction.State, now);
            if (!result.Succeeded)
            {
                return Refuse(auctionId, ToError(result.Error));
            }

            var kind = moderation.Action switch { "suspend" => "suspended", "resume" => "resumed", _ => "cancelled" };
            return Accept(auction, new AuctionChange(result.State, [], moderation.EventType)
            {
                Notices = [new Notice(Audience.Seller, kind, reason), new Notice(Audience.Bidders, kind, reason)],
                Audit = new AdminAction(context.Actor, moderation.Action, reason),
            });
        }, cancellationToken);
    }

    // Image changes: the owner's draft only (anyone else gets "not found", like any owner-only action on
    // someone else's auction), with the current links read under the lock, so a cap or a publish can't race.
    private Task<CommandOutcome> ImageCommandAsync(
        CommandContext context, AuctionId auctionId, byte[] requestHash,
        Func<NpgsqlBatchFactory, List<AuctionImage>, CancellationToken, Task<(CommandError Error, IReadOnlyList<NpgsqlBatchCommand>? Writes)>> decide,
        CancellationToken cancellationToken) =>
        RunLoadedAsync(context, auctionId, requestHash, async (db, auction, _, ct) =>
        {
            if (auction.State.SellerId != context.Actor)
            {
                return Refuse(auctionId, CommandError.NotFound);
            }

            if (auction.State.Status != AuctionStatus.Draft)
            {
                return Refuse(auctionId, CommandError.InvalidState);
            }

            var (error, writes) = await decide(db, await AuctionImages.ListAsync(db, auctionId, ct), ct);
            if (writes is not null)
            {
                return Accept(auction, new AuctionChange(auction.State, [], EventType: null) { Extra = writes });
            }

            IReadOnlyList<FieldError> fields = error == CommandError.ValidationFailed ? [new FieldError("images", "must_list_each_image_once")] : [];
            return Refuse(auctionId, error, fields);
        }, cancellationToken);

    /// <summary>
    /// Activates scheduled auctions whose start has passed and closes auctions whose end has passed.
    /// Returns how many changed.
    /// </summary>
    public async Task<int> AdvanceDueAuctionsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<AuctionId> due;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            due = await AuctionStore.FindDueAsync(connection, options.SweepBatchSize, cancellationToken);
        }

        var advanced = 0;
        foreach (var id in due)
        {
            advanced += await AdvanceAsync(id, cancellationToken) ? 1 : 0;
        }

        return advanced;
    }

    /// <summary>
    /// Deletes idempotency records older than <see cref="EngineOptions.KeyRetention"/>, in batches.
    /// A key older than that is treated as new. Returns how many were deleted.
    /// </summary>
    public async Task<int> SweepExpiredKeysAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var total = 0;
        int deleted;
        do
        {
            deleted = await IdempotencyStore.SweepAsync(connection, options.KeyRetention, KeySweepBatchSize, cancellationToken);
            total += deleted;
        }
        while (deleted == KeySweepBatchSize);

        return total;
    }

    private Task<CommandOutcome> RunAsync(
        CommandContext context, AuctionId auctionId, byte[] requestHash, Func<LockedAuction, DateTimeOffset, Decision> decide, CancellationToken cancellationToken) =>
        RunLoadedAsync(context, auctionId, requestHash, (_, auction, now, _) => Task.FromResult(decide(auction, now)), cancellationToken);

    /// <summary>A command that reads more under the auction lock (images, the seller's limits) before it decides.</summary>
    private Task<CommandOutcome> RunLoadedAsync(
        CommandContext context, AuctionId auctionId, byte[] requestHash, Func<NpgsqlBatchFactory, LockedAuction, DateTimeOffset, CancellationToken, Task<Decision>> decide,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return WithRetriesAsync(context, () => RunOnceAsync(context, auctionId, requestHash, decide, cancellationToken), cancellationToken);
    }

    // A lock timeout or deadlock leaves nothing behind, so the whole command is retried after a short,
    // jittered pause; if every attempt fails, the caller gets Busy and may retry with the same key.
    // A unique violation on the key means a concurrent command with the same key, for another auction,
    // committed first; nothing of this attempt was kept, and the retry finds the record and refuses the
    // reuse.
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
            catch (PostgresException e) when (e.SqlState == ForeignKeyViolation && e.ConstraintName == SweptImage)
            {
                // The image was swept between the owner check and the link: it no longer exists.
                return new CommandOutcome { Error = CommandError.NotFound };
            }
        }
    }

    private static bool IsRetryable(PostgresException e) =>
        e.SqlState is LockNotAvailable or DeadlockDetected
        || (e.SqlState == UniqueViolation && e.ConstraintName == "pk_idempotency_records");

    private async Task<CommandOutcome> RunOnceAsync(
        CommandContext context, AuctionId auctionId, byte[] requestHash, Func<NpgsqlBatchFactory, LockedAuction, DateTimeOffset, CancellationToken, Task<Decision>> decide,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var locked = await AuctionStore.LockAsync(connection, transaction, auctionId, context, options.LockTimeout, cancellationToken);

        // Replay comes before any rule is checked, so a retried acceptance stays accepted after the deadline.
        if (locked.Recorded is { } recorded)
        {
            return recorded.ReplayFor(requestHash);
        }

        var decision = locked.Auction is { } auction && IsVisibleTo(auction, context.Actor)
            ? await decide(new NpgsqlBatchFactory(connection, transaction), auction, locked.Now, cancellationToken)
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
        var full = errors.Count == 0
            && await SellerLimits.OpenDraftsAsync(new NpgsqlBatchFactory(connection, transaction), context.Actor, cancellationToken) >= options.MaxOpenDrafts;
        var outcome = (errors.Count > 0, full) switch
        {
            (true, _) => new CommandOutcome { Error = CommandError.ValidationFailed, FieldErrors = errors },
            (_, true) => new CommandOutcome { Error = CommandError.DraftLimit },
            _ => new CommandOutcome { AuctionId = id, Version = 0 },
        };

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

    // Status and time are re-checked under the lock, so an auction another worker already advanced, or
    // one that is not due after all, is left unchanged.
    private async Task<bool> AdvanceAsync(AuctionId id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var claimed = await AuctionStore.ClaimAsync(connection, transaction, id, cancellationToken);
        if (claimed.Auction is not { } auction)
        {
            return false;
        }

        var result = auction.State.Status == AuctionStatus.Scheduled
            ? Lifecycle.Activate(auction.State, claimed.Now)
            : Lifecycle.Close(auction.State, claimed.Now);
        if (!result.Succeeded)
        {
            return false;
        }

        var closed = result.State.Status == AuctionStatus.Closed;
        var change = new AuctionChange(result.State, [], closed ? "auction.closed" : "auction.activated")
        {
            Notices = closed ? ClosingNotices(result.State) : [],
        };
        await using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var command in AuctionStore.ChangeCommands(auction, change, claimed.Now, Guid.CreateVersion7()))
        {
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    // NOT-01 at close: the winner, the other bidders and the seller each hear their own outcome.
    private static Notice[] ClosingNotices(AuctionState closed) => closed.Outcome switch
    {
        CloseOutcome.Sold =>
        [
            Notice.To(closed.LeaderId!.Value, "won"),
            new Notice(Audience.Bidders, "lost", Except: closed.LeaderId),
            new Notice(Audience.Seller, "ended_sold"),
        ],
        CloseOutcome.ReserveNotMet => [new Notice(Audience.Seller, "reserve_not_met"), new Notice(Audience.Bidders, "reserve_not_met")],
        _ => [new Notice(Audience.Seller, "ended_no_bids")],
    };

    // A draft is private to its seller: to anyone else it is refused exactly like an id that doesn't exist.
    private static bool IsVisibleTo(LockedAuction auction, UserId actor) =>
        auction.State.Status != AuctionStatus.Draft || auction.State.SellerId == actor;

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
