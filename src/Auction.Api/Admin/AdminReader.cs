using Auction.Api.Auctions;
using Npgsql;

namespace Auction.Api.Admin;

public sealed record RecentBid(Guid AuctionId, string Title, long? AmountCents, string Source, DateTimeOffset AcceptedAt);

public sealed record Activity(long ActiveAuctions, long BidsLastFiveMinutes, IReadOnlyList<RecentBid> RecentBids, int OnlineUsers);

/// <summary>A published auction in the moderation list, with its seller (never a draft).</summary>
public sealed record ModeratedAuction(
    Guid Id, string Title, string Status, Guid SellerId, string SellerName, long? CurrentPriceCents, long BidCount, DateTimeOffset EndsAt, long Version);

/// <summary>One admin action (ADM-03): who, what, on which target, why and when.</summary>
public sealed record AuditEntry(long Id, DateTimeOffset At, Guid AdminId, string AdminName, string Action, string TargetType, Guid TargetId, string Reason);

/// <summary>A row of the admin Users screen: who, what kind, whether disabled, when they last signed in, and how active.</summary>
public sealed record UserSummary(
    Guid Id, string DisplayName, string Email, string Kind, bool Disabled, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt,
    long ListingCount, long BidCount);

/// <summary>
/// The admin area's reads, as the api role, including the read-only audit log (ADM-03). Drafts are never listed: they stay private to their seller
/// even from admins. Searches take a title or display-name fragment, or an exact id.
/// </summary>
internal sealed class AdminReader(NpgsqlDataSource dataSource)
{
    private const string ActivitySql = """
        SELECT (SELECT count(*) FROM auction.auctions WHERE status = 'active'),
               (SELECT count(*) FROM auction.bids WHERE accepted_at > clock_timestamp() - interval '5 minutes')
        """;

    private const string RecentBidsSql = """
        SELECT b.auction_id, a.title, b.amount_cents, b.source, b.accepted_at
        FROM auction.bids b JOIN auction.auctions a ON a.id = b.auction_id
        ORDER BY b.accepted_at DESC
        LIMIT 10
        """;

    private const string AuctionsSql = """
        SELECT a.id, a.title, a.status, a.seller_id, u.display_name, a.current_price_cents, a.accepted_bid_count, a.ends_at, a.version
        FROM auction.auctions a JOIN auction.users u ON u.id = a.seller_id
        WHERE a.status <> 'draft'
          AND ($4::text IS NULL OR a.status = $4)
          AND ($5::text IS NULL OR a.title ILIKE '%' || $5 || '%' OR u.display_name ILIKE '%' || $5 || '%' OR a.seller_id::text = $5 OR a.id::text = $5)
          AND ($1::timestamptz IS NULL OR (a.ends_at, a.id) > ($1::timestamptz, $2::uuid))
        ORDER BY a.ends_at, a.id
        LIMIT $3
        """;

    // $4 a name, email or id; $5 a kind; $6 enabled or disabled. A sign-up counts as the first sign-in.
    private const string UsersSql = """
        SELECT u.id, u.display_name, u.email, u.kind, u.disabled_at IS NOT NULL, u.created_at,
               (SELECT max(e.at) FROM auction.account_events e WHERE e.user_id = u.id AND e.type IN ('sign_in', 'sign_up')),
               (SELECT count(*) FROM auction.auctions a WHERE a.seller_id = u.id AND a.status <> 'draft'),
               (SELECT count(*) FROM auction.bids b WHERE b.bidder_id = u.id)
        FROM auction.users u
        WHERE ($4::text IS NULL OR u.display_name ILIKE '%' || $4 || '%' OR u.email ILIKE '%' || $4 || '%' OR u.id::text = $4)
          AND ($5::text IS NULL OR u.kind = $5)
          AND ($6::text IS NULL OR (u.disabled_at IS NOT NULL) = ($6 = 'disabled'))
          AND ($1::timestamptz IS NULL OR (u.created_at, u.id) > ($1::timestamptz, $2::uuid))
        ORDER BY u.created_at, u.id
        LIMIT $3
        """;

    // Newest first; a search takes an exact admin, target or action, or a fragment of the admin's name.
    private const string AuditSql = """
        SELECT a.id, a.at, a.admin_id, u.display_name, a.action, a.target_type, a.target_id, a.reason
        FROM auction.admin_audit a JOIN auction.users u ON u.id = a.admin_id
        WHERE ($3::text IS NULL OR a.admin_id::text = $3 OR a.target_id::text = $3 OR a.action = $3 OR u.display_name ILIKE '%' || $3 || '%')
          AND ($1::bigint IS NULL OR a.id < $1)
        ORDER BY a.id DESC
        LIMIT $2
        """;

    public async Task<Activity> ActivityAsync(int onlineUsers, CancellationToken cancellationToken)
    {
        long active;
        long recentCount;
        await using (var counts = dataSource.CreateCommand(ActivitySql))
        await using (var reader = await counts.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            (active, recentCount) = (reader.GetInt64(0), reader.GetInt64(1));
        }

        var recent = new List<RecentBid>();
        await using (var bids = dataSource.CreateCommand(RecentBidsSql))
        await using (var reader = await bids.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                recent.Add(ReadRecentBid(reader));
            }
        }

        return new Activity(active, recentCount, recent, onlineUsers);
    }

    public Task<Page<ModeratedAuction>> AuctionsAsync(
        string? query, string? status, (DateTimeOffset At, Guid Id)? after, int pageSize, CancellationToken cancellationToken) =>
        PageAsync(new ListQuery<ModeratedAuction>(AuctionsSql, [status, Blank(query)], reader => new ModeratedAuction(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetInt64(6), reader.GetFieldValue<DateTimeOffset>(7), reader.GetInt64(8)),
            item => (item.EndsAt, item.Id)), after, pageSize, cancellationToken);

    public Task<Page<UserSummary>> UsersAsync(
        string? query, string? kind, string? state, (DateTimeOffset At, Guid Id)? after, int pageSize, CancellationToken cancellationToken) =>
        PageAsync(new ListQuery<UserSummary>(UsersSql, [Blank(query), Blank(kind), Blank(state)], reader => new UserSummary(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6), reader.GetInt64(7), reader.GetInt64(8)),
            item => (item.CreatedAt, item.Id)), after, pageSize, cancellationToken);

    public async Task<Page<AuditEntry>> AuditAsync(string? query, long? before, int pageSize, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(AuditSql);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)before ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        command.Parameters.Add(new NpgsqlParameter { Value = pageSize + 1 });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)Blank(query) ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<AuditEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadAuditEntry(reader));
        }

        return items.Count <= pageSize
            ? new Page<AuditEntry>(items, null)
            : new Page<AuditEntry>(items[..pageSize], Cursors.Encode(items[pageSize - 1].Id));
    }

    /// <summary>Unpublished events: the live-hint backlog shown in admin health.</summary>
    public async Task<long> OutboxBacklogAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT count(*) FROM auction.outbox_events WHERE published_at IS NULL");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static RecentBid ReadRecentBid(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3),
        reader.GetFieldValue<DateTimeOffset>(4));

    private static AuditEntry ReadAuditEntry(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetGuid(6), reader.GetString(7));

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>A keyset-paged list: its SQL, its text filters ($4 onward), how to read a row and its sort key.</summary>
    private sealed record ListQuery<T>(string Sql, string?[] Filters, Func<NpgsqlDataReader, T> Read, Func<T, (DateTimeOffset, Guid)> Key);

    private async Task<Page<T>> PageAsync<T>(ListQuery<T> query, (DateTimeOffset At, Guid Id)? after, int pageSize, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(query.Sql);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)after?.At ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)after?.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = pageSize + 1 });
        foreach (var filter in query.Filters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)filter ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(query.Read(reader));
        }

        if (items.Count <= pageSize)
        {
            return new Page<T>(items, null);
        }

        var (at, id) = query.Key(items[pageSize - 1]);
        return new Page<T>(items[..pageSize], Cursors.Encode(at, id));
    }
}
