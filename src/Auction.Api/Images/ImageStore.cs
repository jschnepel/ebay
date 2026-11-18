using System.Security.Cryptography;
using Auction.Domain;
using Npgsql;

namespace Auction.Api.Images;

/// <summary>The image limits (UI-05); the same in the demo, configurable otherwise.</summary>
public sealed class ImageOptions
{
    public const int MaxImageBytes = 5 * 1024 * 1024;
    public const int MaxRequestBytes = 8 * 1024 * 1024;

    public int UploadsPerHour { get; init; } = 30;

    public long UserQuotaBytes { get; init; } = 100L * 1024 * 1024;

    public long TotalQuotaBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Unattached blobs older than this are swept and stop counting toward the quotas.</summary>
    public TimeSpan UnattachedLifetime { get; init; } = TimeSpan.FromHours(1);

    public bool IsValid() => UploadsPerHour > 0 && UserQuotaBytes > 0 && TotalQuotaBytes > 0 && UnattachedLifetime > TimeSpan.Zero;
}

public enum UploadResult
{
    Stored,
    Existing,
    RateLimited,
    UserQuotaFull,
    TotalQuotaFull,
}

/// <summary>A stored image as served.</summary>
public sealed record StoredImage(string ContentType, byte[] Data, string ETag, bool Public);

/// <summary>
/// Image blobs, written by the API (the engine only links them to auctions). An upload is content-addressed
/// per owner, so a retried upload returns the same blob. The counted limits can't be overshot by concurrent
/// uploads: the user's quota row and then the single global row are locked FOR UPDATE in the upload's own
/// transaction, so the checks and the insert are serialized per user and for the total.
/// </summary>
public sealed class ImageStore(NpgsqlDataSource dataSource, ImageOptions options)
{
    private const string ExistingSql = "SELECT id FROM auction.image_blobs WHERE owner_id = $1 AND sha256 = $2";

    private const string EnsureUserSql = "INSERT INTO auction.upload_quotas (user_id) VALUES ($1) ON CONFLICT (user_id) DO NOTHING";

    private const string LockUserSql =
        "SELECT bytes_used, window_start, uploads_in_window FROM auction.upload_quotas WHERE user_id = $1 FOR UPDATE";

    private const string LockTotalSql = "SELECT bytes_used FROM auction.global_quota WHERE id = 1 FOR UPDATE";

    private const string InsertSql = """
        INSERT INTO auction.image_blobs (id, owner_id, sha256, content_type, size_bytes, width, height, data)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
        """;

    // One statement: the user's counters and the global total move together.
    private const string CountSql = """
        WITH user_row AS (
            UPDATE auction.upload_quotas
            SET bytes_used = bytes_used + $2,
                window_start = CASE WHEN window_start > now() - interval '1 hour' THEN window_start ELSE now() END,
                uploads_in_window = CASE WHEN window_start > now() - interval '1 hour' THEN uploads_in_window + 1 ELSE 1 END
            WHERE user_id = $1
            RETURNING 1)
        UPDATE auction.global_quota SET bytes_used = bytes_used + $2 WHERE id = 1
        """;

    // An image is public once any auction showing it is published; until then only its owner may see it.
    private const string ServeSql = """
        SELECT b.content_type, b.data, encode(b.sha256, 'hex'), b.owner_id,
               EXISTS (SELECT 1 FROM auction.auction_images l JOIN auction.auctions a ON a.id = l.auction_id
                       WHERE l.blob_id = b.id AND a.status <> 'draft')
        FROM auction.image_blobs b WHERE b.id = $1
        """;

    private const string SweepSql = """
        WITH gone AS (
            DELETE FROM auction.image_blobs b
            WHERE b.id IN (SELECT id FROM auction.image_blobs x
                           WHERE x.created_at < now() - $1::interval
                             AND NOT EXISTS (SELECT 1 FROM auction.auction_images l WHERE l.blob_id = x.id)
                           LIMIT 500)
            RETURNING owner_id, size_bytes),
        per_owner AS (SELECT owner_id, sum(size_bytes) AS bytes FROM gone GROUP BY owner_id),
        users AS (
            UPDATE auction.upload_quotas q SET bytes_used = q.bytes_used - p.bytes FROM per_owner p WHERE q.user_id = p.owner_id)
        UPDATE auction.global_quota SET bytes_used = bytes_used - (SELECT coalesce(sum(size_bytes), 0) FROM gone) WHERE id = 1
        RETURNING (SELECT count(*) FROM gone)
        """;

    public async Task<(UploadResult Result, Guid? Id)> UploadAsync(UserId owner, byte[] data, ImageInfo info, CancellationToken cancellationToken)
    {
        var hash = SHA256.HashData(data);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // The user's quota row is locked first, so two uploads of the same bytes take turns and the second
        // finds the first's blob instead of racing it.
        var quota = await LockUserAsync(connection, transaction, owner, cancellationToken);
        if (await ScalarAsync(connection, transaction, ExistingSql, cancellationToken, owner.Value, hash) is Guid existing)
        {
            return (UploadResult.Existing, existing);
        }

        var refusal = await CheckLimitsAsync(connection, transaction, quota, data.Length, cancellationToken);
        if (refusal is { } refused)
        {
            return (refused, null);
        }

        var id = Guid.CreateVersion7();
        await ExecuteAsync(connection, transaction, InsertSql, cancellationToken, id, owner.Value, hash, info.ContentType, data.Length, info.Width, info.Height, data);
        await ExecuteAsync(connection, transaction, CountSql, cancellationToken, owner.Value, (long)data.Length);
        await transaction.CommitAsync(cancellationToken);
        return (UploadResult.Stored, id);
    }

    /// <summary>The image if the viewer may see it, with whether it may be cached publicly.</summary>
    public async Task<StoredImage?> FindAsync(Guid id, UserId? viewer, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ServeSql);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var image = Read(reader);
        return image.Public || image.Owner == viewer?.Value ? image.Image : null;
    }

    /// <summary>Deletes unattached blobs past their lifetime, in batches; returns how many went.</summary>
    public async Task<long> SweepAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(SweepSql);
        command.Parameters.Add(new NpgsqlParameter { Value = options.UnattachedLifetime });
        return await command.ExecuteScalarAsync(cancellationToken) is long count ? count : 0;
    }

    private static async Task<(long Used, DateTimeOffset WindowStart, int InWindow)> LockUserAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, UserId owner, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction, EnsureUserSql, cancellationToken, owner.Value);
        await using var command = Command(connection, transaction, LockUserSql, [owner.Value]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadQuota(reader);
    }

    private async Task<UploadResult?> CheckLimitsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, (long Used, DateTimeOffset WindowStart, int InWindow) quota, int size,
        CancellationToken cancellationToken)
    {
        if (quota.WindowStart > DateTimeOffset.UtcNow.AddHours(-1) && quota.InWindow >= options.UploadsPerHour)
        {
            return UploadResult.RateLimited;
        }

        if (quota.Used + size > options.UserQuotaBytes)
        {
            return UploadResult.UserQuotaFull;
        }

        var total = (long)(await ScalarAsync(connection, transaction, LockTotalSql, cancellationToken))!;
        return total + size > options.TotalQuotaBytes ? UploadResult.TotalQuotaFull : null;
    }

    private static (long Used, DateTimeOffset WindowStart, int InWindow) ReadQuota(NpgsqlDataReader reader) =>
        (reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2));

    private static (StoredImage Image, Guid Owner, bool Public) Read(NpgsqlDataReader reader)
    {
        var isPublic = reader.GetBoolean(4);
        return (new StoredImage(reader.GetString(0), reader.GetFieldValue<byte[]>(1), $"\"{reader.GetString(2)}\"", isPublic), reader.GetGuid(3), isPublic);
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params object[] args)
    {
        await using var command = Command(connection, transaction, sql, args);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params object[] args)
    {
        await using var command = Command(connection, transaction, sql, args);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, object[] args)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg });
        }

        return command;
    }
}
