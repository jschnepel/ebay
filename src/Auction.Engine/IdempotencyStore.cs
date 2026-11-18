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
        "SELECT request_hash, outcome FROM idempotency_records WHERE actor_id = $1 AND key = $2";

    // ON CONFLICT DO NOTHING: a concurrent insert of the same key waits for the other transaction and
    // then inserts nothing, so the caller can read and replay the winner's outcome.
    private const string InsertSql =
        "INSERT INTO idempotency_records (actor_id, key, request_hash, outcome) VALUES ($1, $2, $3, $4::jsonb) " +
        "ON CONFLICT (actor_id, key) DO NOTHING";

    public static bool IsValidKey(string key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= MaxKeyLength;

    public static NpgsqlBatchCommand SelectCommand(CommandContext context) =>
        new(SelectSql) { Parameters = { Param(context.Actor.Value), Param(context.IdempotencyKey) } };

    public static NpgsqlBatchCommand InsertCommand(CommandContext context, byte[] requestHash, CommandOutcome outcome) =>
        new(InsertSql)
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
    public static async Task<RecordedOutcome?> ReadAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var hash = await reader.GetFieldValueAsync<byte[]>(0, cancellationToken);
        var outcome = JsonSerializer.Deserialize<CommandOutcome>(await reader.GetFieldValueAsync<string>(1, cancellationToken))
            ?? throw new InvalidOperationException("An idempotency record holds no outcome.");
        return new RecordedOutcome(hash, outcome);
    }

    public static async Task<RecordedOutcome?> FindAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandContext context, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection, transaction) { BatchCommands = { SelectCommand(context) } };
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        return await ReadAsync(reader, cancellationToken);
    }

    /// <summary>Inserts the record; false when another transaction recorded the same key first.</summary>
    public static async Task<bool> TryInsertAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CommandContext context, byte[] requestHash, CommandOutcome outcome, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection, transaction) { BatchCommands = { InsertCommand(context, requestHash, outcome) } };
        return await batch.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static NpgsqlParameter Param(object value) => new() { Value = value };
}
