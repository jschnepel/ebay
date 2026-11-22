using System.Text.Json;
using Npgsql;

namespace Auction.Engine;

/// <summary>A command outcome recorded under an actor's idempotency key.</summary>
internal sealed record RecordedOutcome(byte[] RequestHash, CommandOutcome Outcome)
{
    /// <summary>The recorded outcome for the same request, or <see cref="CommandError.KeyReused"/> for a different one.</summary>
    public CommandOutcome ReplayFor(byte[] requestHash) =>
        RequestHash.AsSpan().SequenceEqual(requestHash)
            ? Outcome with { Replayed = true }
            : new CommandOutcome { Error = CommandError.KeyReused };
}

internal static class IdempotencyStore
{
    public const int MaxKeyLength = 255;

    private const string SelectSql =
        "SELECT request_hash, outcome, created_at FROM idempotency_records WHERE actor_id = $1 AND key = $2";

    // A plain insert for commands on an existing auction: the same key committed first by a command on
    // another auction raises a unique violation, which rolls this command back so the retry replays.
    private const string InsertSql =
        "INSERT INTO idempotency_records (actor_id, key, request_hash, outcome) VALUES ($1, $2, $3, $4::jsonb)";

    // For creating a draft, which has no row to lock: a concurrent insert of the same key waits for the
    // other transaction and then inserts nothing, so the caller can read and replay the winner's outcome.
    private const string InsertIfAbsentSql = InsertSql + " ON CONFLICT (actor_id, key) DO NOTHING";

    private const string SweepSql =
        """
        DELETE FROM idempotency_records
        WHERE (actor_id, key) IN (
            SELECT actor_id, key FROM idempotency_records WHERE created_at < clock_timestamp() - $1 LIMIT $2)
        """;

    public static bool IsValidKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= MaxKeyLength;

    public static NpgsqlBatchCommand SelectCommand(CommandContext context) =>
        new(SelectSql) { Parameters = { Param(context.Actor.Value), Param(context.IdempotencyKey) } };

    public static NpgsqlBatchCommand InsertCommand(CommandContext context, byte[] requestHash, CommandOutcome outcome) =>
        Insert(InsertSql, context, requestHash, outcome);

    private static NpgsqlBatchCommand Insert(string sql, CommandContext context, byte[] requestHash, CommandOutcome outcome) =>
        new(sql)
        {
            Parameters =
            {
                Param(context.Actor.Value),
                Param(context.IdempotencyKey),
                Param(requestHash),
                Param(JsonSerializer.Serialize(outcome)),
            },
        };

    /// <summary>Reads the result of <see cref="SelectCommand"/> at the reader's current position.</summary>
    public static async Task<RecordedOutcome?> ReadAsync(NpgsqlDataReader reader, bool existingAuction, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var hash = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken);
        var outcome = JsonSerializer.Deserialize<CommandOutcome>(await reader.GetFieldValueAsync<string>(1, cancellationToken))
            ?? throw new InvalidOperationException("An idempotency record holds no outcome.");
        // Older receipts have the original database record time but no decision time in their JSON.
        if (existingAuction && outcome.DecidedAt is null && outcome.Error != CommandError.NotFound)
        {
            outcome = outcome with { DecidedAt = reader.GetFieldValue<DateTimeOffset>(2) };
        }

        return new RecordedOutcome(hash, outcome);
    }

    public static async Task<RecordedOutcome?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandContext context, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection, transaction) { BatchCommands = { SelectCommand(context) } };
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        return await ReadAsync(reader, existingAuction: false, cancellationToken);
    }

    /// <summary>Inserts the record; false when another transaction recorded the same key first.</summary>
    public static async Task<bool> TryInsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandContext context, byte[] requestHash, CommandOutcome outcome, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection, transaction)
        {
            BatchCommands = { Insert(InsertIfAbsentSql, context, requestHash, outcome) },
        };
        return await batch.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Deletes up to <paramref name="limit"/> records older than <paramref name="retention"/>.</summary>
    public static async Task<int> SweepAsync(
        NpgsqlConnection connection, TimeSpan retention, int limit, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection)
        {
            BatchCommands = { new NpgsqlBatchCommand(SweepSql) { Parameters = { Param(retention), Param(limit) } } },
        };
        return await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter Param(object value) => new() { Value = value };
}
