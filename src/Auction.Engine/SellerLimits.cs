using Auction.Domain;
using Npgsql;

namespace Auction.Engine;

/// <summary>The open connection and transaction a command reads through while it holds its locks.</summary>
internal sealed record NpgsqlBatchFactory(NpgsqlConnection Connection, NpgsqlTransaction Transaction)
{
    public NpgsqlCommand Command(string sql, params object?[] args)
    {
        var command = new NpgsqlCommand(sql, Connection, Transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }

        return command;
    }
}

/// <summary>
/// The per-seller counted limits (UI-05), race-free: the seller's <c>user_limits</c> row is locked FOR UPDATE
/// before counting, so concurrent creates or publishes by one seller take turns and can't overshoot.
/// </summary>
internal static class SellerLimits
{
    private const string LockSql = """
        INSERT INTO user_limits (user_id) VALUES ($1) ON CONFLICT (user_id) DO NOTHING;
        """;

    private const string LockedRowSql = "SELECT published_on, published_count FROM user_limits WHERE user_id = $1 FOR UPDATE";

    public static async Task<long> OpenDraftsAsync(NpgsqlBatchFactory db, UserId seller, CancellationToken cancellationToken)
    {
        await LockAsync(db, seller, cancellationToken);
        await using var count = db.Command("SELECT count(*) FROM auctions WHERE seller_id = $1 AND status = 'draft'", seller.Value);
        return (long)(await count.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>How many auctions the seller has published on <paramref name="day"/>, with their row locked.</summary>
    public static async Task<int> PublishedOnAsync(NpgsqlBatchFactory db, UserId seller, DateOnly day, CancellationToken cancellationToken)
    {
        await LockAsync(db, seller, cancellationToken);
        await using var read = db.Command(LockedRowSql, seller.Value);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return await reader.IsDBNullAsync(0, cancellationToken) || await reader.GetFieldValueAsync<DateOnly>(0, cancellationToken) != day
            ? 0
            : await reader.GetFieldValueAsync<int>(1, cancellationToken);
    }

    /// <summary>Counts one more publish for the day, starting a new count on a new day.</summary>
    public static NpgsqlBatchCommand CountPublish(UserId seller, DateOnly day) =>
        AuctionStore.Command(
            """
            UPDATE user_limits
            SET published_count = CASE WHEN published_on = $2 THEN published_count + 1 ELSE 1 END, published_on = $2
            WHERE user_id = $1
            """,
            seller.Value,
            day);

    private static async Task LockAsync(NpgsqlBatchFactory db, UserId seller, CancellationToken cancellationToken)
    {
        await using (var ensure = db.Command(LockSql, seller.Value))
        {
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var lockRow = db.Command("SELECT 1 FROM user_limits WHERE user_id = $1 FOR UPDATE", seller.Value);
        await lockRow.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>One image link on an auction.</summary>
internal sealed record AuctionImage(Guid BlobId, int Position, bool IsCover);

/// <summary>
/// The links from an auction to its uploaded images, written only here, under the auction lock. Positions
/// run 0..n-1; exactly one image is the cover while there are any.
/// </summary>
internal static class AuctionImages
{
    public static async Task<List<AuctionImage>> ListAsync(NpgsqlBatchFactory db, AuctionId auction, CancellationToken cancellationToken)
    {
        await using var command = db.Command("SELECT blob_id, position, is_cover FROM auction_images WHERE auction_id = $1 ORDER BY position", auction.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var images = new List<AuctionImage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            images.Add(new AuctionImage(reader.GetGuid(0), reader.GetInt32(1), reader.GetBoolean(2)));
        }

        return images;
    }

    /// <summary>
    /// The blob's owner, or null if it doesn't exist (or was swept). Inserting the link then takes the
    /// foreign key's lock on the blob, so a sweep can't delete it under a committed link.
    /// </summary>
    public static async Task<Guid?> OwnerOfAsync(NpgsqlBatchFactory db, Guid blob, CancellationToken cancellationToken)
    {
        await using var command = db.Command("SELECT owner_id FROM image_blobs WHERE id = $1", blob);
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    public static IReadOnlyList<NpgsqlBatchCommand> Attach(AuctionId auction, Guid blob, int position, bool isCover) =>
    [
        AuctionStore.Command(
            "INSERT INTO auction_images (auction_id, blob_id, position, is_cover) VALUES ($1, $2, $3, $4)", auction.Value, blob, position, isCover),
    ];

    // The rest close up their positions, and if the cover went, the new first image takes over.
    public static IReadOnlyList<NpgsqlBatchCommand> Remove(AuctionId auction, Guid blob) =>
    [
        AuctionStore.Command("DELETE FROM auction_images WHERE auction_id = $1 AND blob_id = $2", auction.Value, blob),
        AuctionStore.Command(
            """
            UPDATE auction_images l SET position = r.n - 1
            FROM (SELECT blob_id, row_number() OVER (ORDER BY position) AS n FROM auction_images WHERE auction_id = $1) r
            WHERE l.auction_id = $1 AND l.blob_id = r.blob_id
            """,
            auction.Value),
        AuctionStore.Command(
            """
            UPDATE auction_images SET is_cover = (position = 0)
            WHERE auction_id = $1 AND NOT EXISTS (SELECT 1 FROM auction_images WHERE auction_id = $1 AND is_cover)
            """,
            auction.Value),
    ];

    public static IReadOnlyList<NpgsqlBatchCommand> Reorder(AuctionId auction, IReadOnlyList<Guid> order) =>
    [
        AuctionStore.Command(
            """
            UPDATE auction_images l SET position = o.n - 1
            FROM unnest($2::uuid[]) WITH ORDINALITY AS o(blob_id, n)
            WHERE l.auction_id = $1 AND l.blob_id = o.blob_id
            """,
            auction.Value,
            order.ToArray()),
    ];

    public static IReadOnlyList<NpgsqlBatchCommand> SetCover(AuctionId auction, Guid blob) =>
    [
        AuctionStore.Command("UPDATE auction_images SET is_cover = (blob_id = $2) WHERE auction_id = $1", auction.Value, blob),
    ];
}
