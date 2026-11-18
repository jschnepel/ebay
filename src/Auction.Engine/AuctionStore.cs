using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Auction.Domain;
using Npgsql;

namespace Auction.Engine;

/// <summary>An auction row locked for this transaction, with the database time read after the lock.</summary>
internal sealed record LockedAuction(AuctionState State, long Version, long LastHistorySeq);

/// <summary>What a command found once it held the lock.</summary>
internal sealed record LockResult(LockedAuction? Auction, DateTimeOffset Now, RecordedOutcome? Recorded);

/// <summary>An accepted change to write: the state after the command, its history steps and its public event.</summary>
internal sealed record AuctionChange(AuctionState After, IReadOnlyList<BidStep> Steps, string? EventType);

/// <summary>The SQL for one auction command. Every method runs inside the caller's transaction.</summary>
internal static class AuctionStore
{
    private const string Columns =
        "id, seller_id, status, title, description, opening_price_cents, increment_cents, floor_type, reserve_cents, " +
        "bid_entry, starts_at, ends_at, current_price_cents, leader_id, accepted_bid_count, outcome, version";

    /// <summary>
    /// Locks the auction row, waiting up to <paramref name="lockTimeout"/>, then reads the clock, the
    /// private maxima, the last history step and any recorded outcome for the key. The clock is a separate
    /// statement after the lock, so time spent waiting for the lock counts against the deadline.
    /// </summary>
    public static Task<LockResult> LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AuctionId id, CommandContext context, TimeSpan lockTimeout, CancellationToken cancellationToken)
    {
        var timeout = ((long)lockTimeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";
        return LoadAsync(
            new NpgsqlBatch(connection, transaction) { BatchCommands = { Command("SELECT set_config('lock_timeout', $1, true)", timeout) } },
            id,
            "FOR UPDATE",
            context,
            cancellationToken);
    }

    /// <summary>
    /// Claims the auction row for the close worker, skipping it if a bid or another worker holds it; the
    /// next sweep picks it up. No auction is returned when it was skipped.
    /// </summary>
    public static Task<LockResult> ClaimAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AuctionId id, CancellationToken cancellationToken) =>
        LoadAsync(new NpgsqlBatch(connection, transaction), id, "FOR UPDATE SKIP LOCKED", context: null, cancellationToken);

    /// <summary>Auctions due to start or to end by the database clock, soonest end first.</summary>
    public static async Task<IReadOnlyList<AuctionId>> FindDueAsync(
        NpgsqlConnection connection, int limit, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection)
        {
            BatchCommands =
            {
                Command(
                    """
                    SELECT id FROM auctions
                    WHERE (status = 'scheduled' AND starts_at <= clock_timestamp())
                       OR (status IN ('scheduled', 'active', 'suspended') AND ends_at <= clock_timestamp())
                    ORDER BY ends_at
                    LIMIT $1
                    """,
                    limit),
            },
        };
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var due = new List<AuctionId>();
        while (await reader.ReadAsync(cancellationToken))
        {
            due.Add(new AuctionId(reader.GetGuid(0)));
        }

        return due;
    }

    private static async Task<LockResult> LoadAsync(
        NpgsqlBatch batch, AuctionId id, string lockClause, CommandContext? context, CancellationToken cancellationToken)
    {
        await using var owned = batch;
        var leading = batch.BatchCommands.Count;
        batch.BatchCommands.Add(Command($"SELECT {Columns} FROM auctions WHERE id = $1 {lockClause}", id.Value));
        batch.BatchCommands.Add(Command("SELECT clock_timestamp()"));
        batch.BatchCommands.Add(Command("SELECT bidder_id, maximum_cents, sequence FROM proxy_maxima WHERE auction_id = $1", id.Value));
        batch.BatchCommands.Add(Command("SELECT coalesce(max(seq), 0) FROM bids WHERE auction_id = $1", id.Value));
        if (context is not null)
        {
            batch.BatchCommands.Add(IdempotencyStore.SelectCommand(context));
        }

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        for (var i = 0; i < leading; i++)
        {
            await reader.NextResultAsync(cancellationToken);
        }

        var row = await reader.ReadAsync(cancellationToken) ? ReadAuction(reader) : null;
        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var now = await reader.GetFieldValueAsync<DateTimeOffset>(0, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var maxima = ImmutableList.CreateBuilder<ProxyMaximum>();
        while (await reader.ReadAsync(cancellationToken))
        {
            maxima.Add(new ProxyMaximum(new UserId(reader.GetGuid(0)), new Money(reader.GetInt64(1)), reader.GetInt64(2)));
        }

        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var lastSeq = reader.GetInt64(0);
        RecordedOutcome? recorded = null;
        if (context is not null)
        {
            await reader.NextResultAsync(cancellationToken);
            recorded = await IdempotencyStore.ReadAsync(reader, cancellationToken);
        }

        var auction = row is null
            ? null
            : new LockedAuction(row.Value.State with { Maxima = maxima.ToImmutable() }, row.Value.Version, lastSeq);
        return new LockResult(auction, now, recorded);
    }

    public static NpgsqlBatchCommand InsertDraftCommand(AuctionId id, UserId seller, AuctionTerms terms) =>
        Command(
            """
            INSERT INTO auctions (id, seller_id, title, description, opening_price_cents, increment_cents,
                                  floor_type, reserve_cents, bid_entry, starts_at, ends_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """,
            id.Value,
            seller.Value,
            terms.Title,
            terms.Description,
            terms.OpeningPrice.Cents,
            terms.Increment.Cents,
            DbNames.Of(terms.FloorType),
            terms.Reserve?.Cents,
            DbNames.Of(terms.BidEntry),
            terms.StartsAt.ToUniversalTime(),
            terms.EndsAt.ToUniversalTime());

    /// <summary>The writes for an accepted change: the row, new history steps, changed maxima and the event.</summary>
    public static IEnumerable<NpgsqlBatchCommand> ChangeCommands(
        LockedAuction before, AuctionChange change, DateTimeOffset now, Guid requestId)
    {
        var after = change.After;
        var version = before.Version + 1;
        yield return UpdateCommand(after, version, now);

        var seq = before.LastHistorySeq;
        foreach (var step in change.Steps)
        {
            yield return Command(
                "INSERT INTO bids (auction_id, seq, bidder_id, amount_cents, source, accepted_at, request_id) " +
                "VALUES ($1, $2, $3, $4, $5, $6, $7)",
                after.Id.Value,
                ++seq,
                step.BidderId.Value,
                step.Amount?.Cents,
                DbNames.Of(step.Source),
                now,
                requestId);
        }

        foreach (var maximum in after.Maxima.Except(before.State.Maxima))
        {
            yield return Command(
                "INSERT INTO proxy_maxima (auction_id, bidder_id, maximum_cents, sequence, updated_at) VALUES ($1, $2, $3, $4, $5) " +
                "ON CONFLICT (auction_id, bidder_id) DO UPDATE SET maximum_cents = excluded.maximum_cents, " +
                "sequence = excluded.sequence, updated_at = excluded.updated_at",
                after.Id.Value,
                maximum.BidderId.Value,
                maximum.Maximum.Cents,
                maximum.Sequence,
                now);
        }

        if (change.EventType is { } type)
        {
            yield return Command(
                "INSERT INTO outbox_events (auction_id, auction_version, type, payload) VALUES ($1, $2, $3, $4::jsonb)",
                after.Id.Value,
                version,
                type,
                PublicPayload(after, version));
        }
    }

    private static NpgsqlBatchCommand UpdateCommand(AuctionState after, long version, DateTimeOffset now) =>
        Command(
            """
            UPDATE auctions SET
                status = $2, title = $3, description = $4, opening_price_cents = $5, increment_cents = $6,
                floor_type = $7, reserve_cents = $8, bid_entry = $9, starts_at = $10, ends_at = $11,
                current_price_cents = $12, leader_id = $13, accepted_bid_count = $14, outcome = $15, version = $16,
                published_at = CASE WHEN status = 'draft' AND $2 <> 'draft' THEN $17 ELSE published_at END,
                closed_at = CASE WHEN status <> 'closed' AND $2 = 'closed' THEN $17 ELSE closed_at END,
                updated_at = $17
            WHERE id = $1
            """,
            after.Id.Value,
            DbNames.Of(after.Status),
            after.Terms.Title,
            after.Terms.Description,
            after.Terms.OpeningPrice.Cents,
            after.Terms.Increment.Cents,
            DbNames.Of(after.Terms.FloorType),
            after.Terms.Reserve?.Cents,
            DbNames.Of(after.Terms.BidEntry),
            after.Terms.StartsAt.ToUniversalTime(),
            after.Terms.EndsAt.ToUniversalTime(),
            after.CurrentPrice?.Cents,
            after.LeaderId?.Value,
            after.AcceptedBidCount,
            after.Outcome is { } outcome ? DbNames.Of(outcome) : null,
            version,
            now);

    // Live clients treat an event as a hint and re-fetch, but the payload still carries only public
    // facts: never a private maximum or the hidden reserve.
    private static string PublicPayload(AuctionState state, long version) =>
        JsonSerializer.Serialize(new
        {
            version,
            status = DbNames.Of(state.Status),
            currentPriceCents = state.CurrentPrice?.Cents,
            leaderId = state.LeaderId?.Value,
            acceptedBidCount = state.AcceptedBidCount,
            reserveMet = state.ReserveMet,
            endsAt = state.Terms.EndsAt.ToUniversalTime(),
        });

    private static (AuctionState State, long Version)? ReadAuction(NpgsqlDataReader reader)
    {
        var terms = new AuctionTerms
        {
            Title = reader.GetString(3),
            Description = reader.GetString(4),
            OpeningPrice = new Money(reader.GetInt64(5)),
            Increment = new Money(reader.GetInt64(6)),
            FloorType = DbNames.Parse<FloorType>(reader.GetString(7)),
            Reserve = reader.IsDBNull(8) ? null : new Money(reader.GetInt64(8)),
            BidEntry = DbNames.Parse<BidEntry>(reader.GetString(9)),
            StartsAt = reader.GetFieldValue<DateTimeOffset>(10),
            EndsAt = reader.GetFieldValue<DateTimeOffset>(11),
        };
        var state = new AuctionState
        {
            Id = new AuctionId(reader.GetGuid(0)),
            SellerId = new UserId(reader.GetGuid(1)),
            Status = DbNames.Parse<AuctionStatus>(reader.GetString(2)),
            Terms = terms,
            CurrentPrice = reader.IsDBNull(12) ? null : new Money(reader.GetInt64(12)),
            LeaderId = reader.IsDBNull(13) ? null : new UserId(reader.GetGuid(13)),
            AcceptedBidCount = reader.GetInt64(14),
            Outcome = reader.IsDBNull(15) ? null : DbNames.Parse<CloseOutcome>(reader.GetString(15)),
        };
        return (state, reader.GetInt64(16));
    }

    private static NpgsqlBatchCommand Command(string sql, params object?[] args)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }

        return command;
    }
}
