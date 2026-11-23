using Auction.Api.Auctions;
using Npgsql;

namespace Auction.Api.Admin;

/// <summary>One user as an admin sees them: who they are and how much they've sold and bid.</summary>
public sealed record UserProfile(
    Guid Id, string DisplayName, string Email, string Kind, bool Disabled, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt,
    long Listings, long Sold, long Bids, long AuctionsBidOn, long Won);

/// <summary>One of a user's published auctions (drafts stay private, even from admins).</summary>
public sealed record UserListing(
    Guid AuctionId, string Title, string Status, long? CurrentPriceCents, long BidCount, string? Outcome, DateTimeOffset? EndsAt);

/// <summary>
/// An auction a user has bid on. <see cref="TheirHighestCents"/> is their highest visible amount, or null when every
/// one of theirs is a private maximum's step; <see cref="Standing"/> is leading, outbid, won or lost.
/// </summary>
public sealed record UserBidding(
    Guid AuctionId, string Title, string Status, long? TheirHighestCents, long? CurrentPriceCents, string Standing, DateTimeOffset LastBidAt);

/// <summary>The admin reads behind a user's page, as the api role.</summary>
internal sealed class AdminUserReader(NpgsqlDataSource dataSource)
{
    private const string ProfileSql = """
        SELECT u.id, u.display_name, u.email, u.kind, u.disabled_at IS NOT NULL, u.created_at,
               (SELECT max(e.at) FROM auction.account_events e WHERE e.user_id = u.id AND e.type IN ('sign_in', 'sign_up')),
               (SELECT count(*) FROM auction.auctions a WHERE a.seller_id = u.id AND a.status <> 'draft'),
               (SELECT count(*) FROM auction.auctions a WHERE a.seller_id = u.id AND a.outcome = 'sold'),
               (SELECT count(*) FROM auction.bids b WHERE b.bidder_id = u.id),
               (SELECT count(DISTINCT b.auction_id) FROM auction.bids b WHERE b.bidder_id = u.id),
               (SELECT count(*) FROM auction.auctions a WHERE a.leader_id = u.id AND a.outcome = 'sold')
        FROM auction.users u
        WHERE u.id = $1
        """;

    // Newest published first; the last column is the sort key.
    private const string ListingsSql = """
        SELECT a.id, a.title, a.status, a.current_price_cents, a.accepted_bid_count, a.outcome, a.ends_at, a.published_at
        FROM auction.auctions a
        WHERE a.seller_id = $1 AND a.status <> 'draft'
          AND ($2::timestamptz IS NULL OR (a.published_at, a.id) < ($2::timestamptz, $3::uuid))
        ORDER BY a.published_at DESC, a.id DESC
        LIMIT $4
        """;

    // Most recently bid on first; the last column is the sort key.
    private const string BiddingSql = """
        SELECT a.id, a.title, a.status,
               (SELECT max(b.amount_cents) FROM auction.bids b WHERE b.auction_id = a.id AND b.bidder_id = $1),
               a.current_price_cents,
               CASE WHEN a.status IN ('closed', 'cancelled') THEN CASE WHEN a.outcome = 'sold' AND a.leader_id = $1 THEN 'won' ELSE 'lost' END
                    WHEN a.leader_id = $1 THEN 'leading' ELSE 'outbid' END,
               x.last_bid, x.last_bid
        FROM (SELECT auction_id, max(accepted_at) AS last_bid FROM auction.bids WHERE bidder_id = $1 GROUP BY auction_id) x
        JOIN auction.auctions a ON a.id = x.auction_id
        WHERE ($2::timestamptz IS NULL OR (x.last_bid, a.id) < ($2::timestamptz, $3::uuid))
        ORDER BY x.last_bid DESC, a.id DESC
        LIMIT $4
        """;

    public async Task<UserProfile?> ProfileAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ProfileSql);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UserProfile(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4),
            await reader.GetFieldValueAsync<DateTimeOffset>(5, cancellationToken),
            await reader.IsDBNullAsync(6, cancellationToken) ? null : await reader.GetFieldValueAsync<DateTimeOffset>(6, cancellationToken),
            reader.GetInt64(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11));
    }

    public Task<Page<UserListing>> ListingsAsync(Guid id, (DateTimeOffset At, Guid Id)? before, int pageSize, CancellationToken cancellationToken) =>
        PageAsync(ListingsSql, id, before, pageSize, reader => new UserListing(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6)),
            cancellationToken);

    public Task<Page<UserBidding>> BiddingAsync(Guid id, (DateTimeOffset At, Guid Id)? before, int pageSize, CancellationToken cancellationToken) =>
        PageAsync(BiddingSql, id, before, pageSize, reader => new UserBidding(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.GetString(5), reader.GetFieldValue<DateTimeOffset>(6)),
            cancellationToken);

    private const int KeyColumn = 7;

    // Newest-first keyset paging: $1 the user, $2 and $3 the cursor's time and id, $4 the limit. Column 0 is the
    // item's id and column KeyColumn its sort time.
    private async Task<Page<T>> PageAsync<T>(
        string sql, Guid user, (DateTimeOffset At, Guid Id)? before, int pageSize, Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = user });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)before?.At ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)before?.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = pageSize + 1 });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(T Item, DateTimeOffset At, Guid Id)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((read(reader), await reader.GetFieldValueAsync<DateTimeOffset>(KeyColumn, cancellationToken), reader.GetGuid(0)));
        }

        if (rows.Count <= pageSize)
        {
            return new Page<T>([.. rows.Select(r => r.Item)], null);
        }

        var last = rows[pageSize - 1];
        return new Page<T>([.. rows.Take(pageSize).Select(r => r.Item)], Cursors.Encode(last.At, last.Id));
    }
}
