using Auction.Api.Auctions;
using Auction.Domain;
using Npgsql;

namespace Auction.Api.Notifications;

/// <summary>One of the user's own notifications (NOT-01).</summary>
public sealed record NotificationView(long Id, Guid? AuctionId, string? AuctionTitle, string Kind, string? Reason, DateTimeOffset CreatedAt, bool Read);

/// <summary>A page of the user's notifications, newest first, with the unread count across all of them.</summary>
public sealed record NotificationPage(IReadOnlyList<NotificationView> Items, string? NextCursor, long Unread);

/// <summary>
/// The signed-in user's notifications, as the api role: it reads them and may set only <c>read_at</c>, and
/// only ever on the user's own rows.
/// </summary>
internal sealed class NotificationStore(NpgsqlDataSource dataSource)
{
    private const string PageSql = """
        SELECT n.id, n.auction_id, a.title, n.kind, n.reason, n.created_at, n.read_at IS NOT NULL,
               (SELECT count(*) FROM auction.notifications WHERE user_id = $1 AND read_at IS NULL)
        FROM auction.notifications n LEFT JOIN auction.auctions a ON a.id = n.auction_id
        WHERE n.user_id = $1 AND ($2::bigint IS NULL OR n.id < $2)
        ORDER BY n.id DESC
        LIMIT $3
        """;

    private const string ReadSql = "UPDATE auction.notifications SET read_at = coalesce(read_at, now()) WHERE id = $1 AND user_id = $2";

    private const string UnreadSql = "SELECT count(*) FROM auction.notifications WHERE user_id = $1 AND read_at IS NULL";

    /// <summary>The users notified about these auction versions, for live delivery by the relay.</summary>
    private const string RecipientsSql = """
        SELECT DISTINCT user_id FROM auction.notifications
        WHERE (auction_id, auction_version) IN (SELECT * FROM unnest($1::uuid[], $2::bigint[]))
        """;

    public async Task<NotificationPage> PageAsync(UserId user, long? before, int pageSize, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(PageSql);
        command.Parameters.Add(new NpgsqlParameter { Value = user.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)before ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = pageSize + 1 });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<NotificationView>();
        long unread = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(Read(reader));
            unread = reader.GetInt64(7);
        }

        if (items.Count == 0)
        {
            return new NotificationPage([], null, await UnreadAsync(user, cancellationToken));
        }

        return items.Count > pageSize
            ? new NotificationPage(items[..pageSize], Cursors.Encode(items[pageSize - 1].Id), unread)
            : new NotificationPage(items, null, unread);
    }

    /// <summary>Marks one of the user's notifications read; false if it isn't theirs or doesn't exist.</summary>
    public async Task<bool> MarkReadAsync(UserId user, long id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ReadSql);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = user.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<Guid>> RecipientsAsync(IReadOnlyList<(Guid AuctionId, long Version)> changes, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(RecipientsSql);
        command.Parameters.Add(new NpgsqlParameter { Value = changes.Select(c => c.AuctionId).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = changes.Select(c => c.Version).ToArray() });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(reader.GetGuid(0));
        }

        return users;
    }

    private async Task<long> UnreadAsync(UserId user, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(UnreadSql);
        command.Parameters.Add(new NpgsqlParameter { Value = user.Value });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static NotificationView Read(NpgsqlDataReader reader) => new(
        reader.GetInt64(0),
        reader.IsDBNull(1) ? null : reader.GetGuid(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetBoolean(6));
}
