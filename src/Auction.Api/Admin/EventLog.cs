using System.Text.Json;
using Auction.Api.Auctions;
using Npgsql;

namespace Auction.Api.Admin;

/// <summary>Someone or something an event involves: a user, an auction, or a thing without a page of its own.</summary>
/// <param name="Kind"><c>user</c>, <c>auction</c>, <c>email</c> (an address no account uses), <c>live_marketplace</c> or <c>simulator_run</c>.</param>
public sealed record EventParty(string Kind, Guid? Id, string Name, string? Email);

/// <summary>One entry in the admin event log. <see cref="Key"/> is unique and stable, for paging and live updates.</summary>
public sealed record LogEvent(
    string Key, DateTimeOffset At, string Category, string Type, string Level, EventParty? Actor, EventParty? Target,
    IReadOnlyDictionary<string, string?> Detail);

/// <summary>How many events of each category fall in one bucket of time.</summary>
public sealed record EventBucket(DateTimeOffset Start, IReadOnlyDictionary<string, long> Counts);

/// <summary>Events over time for the log's chart: equal buckets covering the whole range, empty ones included.</summary>
public sealed record EventHistogram(int BucketSeconds, IReadOnlyList<EventBucket> Buckets);

/// <summary>A slice of the log: a time range, any of the categories (all when empty), a search and one user's events.</summary>
public sealed record EventQuery(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<string> Categories, string? Search, Guid? User);

/// <summary>
/// The admin event log (ADM-03), read as the api role by merging where each kind of event already lives: account
/// events, the admin audit, demo resets, listings and bids. Every source is bounded by the time range (and the
/// page's cursor), so a busy week of bids never loads whole. Nothing is written here and the engine is untouched.
/// </summary>
internal sealed class EventLog(NpgsqlDataSource dataSource)
{
    public static readonly string[] Categories = ["account", "admin", "listing", "bid"];

    // $1 from, $2 to, $3 the cursor's time (or null), $5 the user (or null). Each branch returns the same columns:
    // at, key, category, type, level, then the actor's kind, id, name and email, the target's, a title for search,
    // and the detail as JSON with text values.
    private const string EventsSql = """
        SELECT e.at, 'a:' || e.id, 'account', e.type,
               CASE WHEN e.type = 'sign_in_failed' THEN 'warning' ELSE 'info' END,
               CASE WHEN e.type <> 'sign_in_failed' THEN 'user' END, CASE WHEN e.type <> 'sign_in_failed' THEN e.user_id END,
               CASE WHEN e.type <> 'sign_in_failed' THEN u.display_name END, CASE WHEN e.type <> 'sign_in_failed' THEN coalesce(u.email, e.email) END,
               CASE WHEN e.type = 'sign_in_failed' THEN CASE WHEN e.user_id IS NULL THEN 'email' ELSE 'user' END END,
               CASE WHEN e.type = 'sign_in_failed' THEN e.user_id END,
               CASE WHEN e.type = 'sign_in_failed' THEN coalesce(u.display_name, e.email) END,
               CASE WHEN e.type = 'sign_in_failed' THEN coalesce(u.email, e.email) END,
               NULL::text,
               jsonb_strip_nulls(jsonb_build_object('cause', e.detail, 'sessionId', e.session_id::text))
        FROM auction.account_events e LEFT JOIN auction.users u ON u.id = e.user_id
        WHERE e.at >= $1 AND e.at < $2 AND ($3::timestamptz IS NULL OR e.at <= $3) AND ($5::uuid IS NULL OR e.user_id = $5)
        UNION ALL
        SELECT m.at, 'm:' || m.id, 'admin', m.action,
               CASE WHEN m.action IN ('suspend', 'cancel', 'disable', 'live_marketplace_paused') THEN 'warning' ELSE 'info' END,
               'user', m.admin_id, a.display_name, a.email,
               m.target_type, CASE WHEN m.target_type IN ('user', 'auction') THEN m.target_id END,
               CASE m.target_type WHEN 'user' THEN tu.display_name WHEN 'auction' THEN ta.title
                                  WHEN 'live_marketplace' THEN 'the live marketplace' ELSE 'a simulator run' END,
               tu.email, ta.title,
               jsonb_build_object('reason', m.reason)
        FROM auction.admin_audit m
        LEFT JOIN auction.users a ON a.id = m.admin_id
        LEFT JOIN auction.users tu ON m.target_type = 'user' AND tu.id = m.target_id
        LEFT JOIN auction.auctions ta ON m.target_type = 'auction' AND ta.id = m.target_id
        WHERE m.at >= $1 AND m.at < $2 AND ($3::timestamptz IS NULL OR m.at <= $3)
          AND ($5::uuid IS NULL OR m.admin_id = $5 OR (m.target_type = 'user' AND m.target_id = $5))
        UNION ALL
        SELECT r.requested_at, 'r:' || r.id || ':requested', 'admin', 'demo_reset_requested', 'info',
               'user', NULL, r.requested_by, NULL, NULL, NULL, NULL, NULL, NULL, '{}'::jsonb
        FROM auction.demo_resets r
        WHERE r.requested_by IS NOT NULL AND $5::uuid IS NULL
          AND r.requested_at >= $1 AND r.requested_at < $2 AND ($3::timestamptz IS NULL OR r.requested_at <= $3)
        UNION ALL
        SELECT r.completed_at, 'r:' || r.id || ':completed', 'admin', 'demo_reset_completed', 'info',
               NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '{}'::jsonb
        FROM auction.demo_resets r
        WHERE r.completed_at IS NOT NULL AND $5::uuid IS NULL
          AND r.completed_at >= $1 AND r.completed_at < $2 AND ($3::timestamptz IS NULL OR r.completed_at <= $3)
        UNION ALL
        SELECT l.published_at, 'p:' || l.id, 'listing', 'published', 'info',
               'user', l.seller_id, s.display_name, s.email, 'auction', l.id, l.title, NULL, l.title, '{}'::jsonb
        FROM auction.auctions l LEFT JOIN auction.users s ON s.id = l.seller_id
        WHERE l.published_at >= $1 AND l.published_at < $2 AND ($3::timestamptz IS NULL OR l.published_at <= $3)
          AND ($5::uuid IS NULL OR l.seller_id = $5)
        UNION ALL
        SELECT l.closed_at, 'c:' || l.id, 'listing', 'closed', 'info',
               NULL, NULL, NULL, NULL, 'auction', l.id, l.title, NULL, l.title,
               jsonb_strip_nulls(jsonb_build_object(
                   'outcome', l.outcome,
                   'finalPriceCents', CASE WHEN l.outcome = 'sold' THEN l.current_price_cents::text END,
                   'sellerName', s.display_name))
        FROM auction.auctions l LEFT JOIN auction.users s ON s.id = l.seller_id
        WHERE l.outcome IS NOT NULL AND l.closed_at >= $1 AND l.closed_at < $2 AND ($3::timestamptz IS NULL OR l.closed_at <= $3)
          AND ($5::uuid IS NULL OR l.seller_id = $5 OR (l.outcome = 'sold' AND l.leader_id = $5))
        UNION ALL
        SELECT o.created_at, 'x:' || o.id, 'listing', 'cancelled', 'info',
               'user', l.seller_id, s.display_name, s.email, 'auction', l.id, l.title, NULL, l.title, '{}'::jsonb
        FROM auction.outbox_events o
        JOIN auction.auctions l ON l.id = o.auction_id
        LEFT JOIN auction.users s ON s.id = l.seller_id
        WHERE o.type = 'auction.cancelled' AND o.created_at >= $1 AND o.created_at < $2 AND ($3::timestamptz IS NULL OR o.created_at <= $3)
          AND ($5::uuid IS NULL OR l.seller_id = $5)
          AND NOT EXISTS (SELECT 1 FROM auction.admin_audit m WHERE m.action = 'cancel' AND m.target_type = 'auction' AND m.target_id = o.auction_id)
        UNION ALL
        SELECT b.accepted_at, 'b:' || b.auction_id || ':' || b.seq, 'bid', 'bid', 'info',
               'user', b.bidder_id, u.display_name, u.email, 'auction', b.auction_id, l.title, NULL, l.title,
               jsonb_build_object('amountCents', b.amount_cents::text, 'source', b.source)
        FROM auction.bids b
        JOIN auction.auctions l ON l.id = b.auction_id
        LEFT JOIN auction.users u ON u.id = b.bidder_id
        WHERE b.accepted_at >= $1 AND b.accepted_at < $2 AND ($3::timestamptz IS NULL OR b.accepted_at <= $3)
          AND ($5::uuid IS NULL OR b.bidder_id = $5)
        """;

    // $4 the cursor's key, $6 the categories (or null for all), $7 the search (or null).
    private const string FilteredSql = $"""
        SELECT * FROM ({EventsSql}) AS events (at, key, category, type, level,
                                                actor_kind, actor_id, actor_name, actor_email,
                                                target_kind, target_id, target_name, target_email, title, detail)
        WHERE ($3::timestamptz IS NULL OR (at, key) < ($3::timestamptz, $4::text))
          AND ($6::text[] IS NULL OR category = ANY ($6::text[]))
          AND ($7::text IS NULL
               OR actor_name ILIKE '%' || $7 || '%' OR actor_email ILIKE '%' || $7 || '%'
               OR target_name ILIKE '%' || $7 || '%' OR target_email ILIKE '%' || $7 || '%' OR title ILIKE '%' || $7 || '%'
               OR actor_id::text = $7 OR target_id::text = $7 OR type = $7)
        """;

    // $8 the row limit.
    private const string PageSql = $"{FilteredSql} ORDER BY at DESC, key DESC LIMIT $8";

    // $8 the bucket size; buckets are aligned to the range's start.
    private const string HistogramSql = $"SELECT date_bin($8::interval, at, $1), category, count(*) FROM ({FilteredSql}) AS f GROUP BY 1, 2";

    /// <summary>The bucket size for a range: about 30 to 60 bars whatever the range.</summary>
    public static TimeSpan BucketFor(TimeSpan range) => range switch
    {
        _ when range <= TimeSpan.FromMinutes(15) => TimeSpan.FromSeconds(30),
        _ when range <= TimeSpan.FromHours(1) => TimeSpan.FromMinutes(1),
        _ when range <= TimeSpan.FromDays(1) => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromHours(6),
    };

    public async Task<Page<LogEvent>> PageAsync(
        EventQuery query, (DateTimeOffset At, string Key)? before, int pageSize, CancellationToken cancellationToken)
    {
        await using var command = Command(PageSql, query, before);
        command.Parameters.Add(new NpgsqlParameter { Value = pageSize + 1 });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<LogEvent>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(await ReadAsync(reader, cancellationToken));
        }

        if (items.Count <= pageSize)
        {
            return new Page<LogEvent>(items, null);
        }

        var last = items[pageSize - 1];
        return new Page<LogEvent>(items[..pageSize], Cursors.Encode(last.At, last.Key));
    }

    public async Task<EventHistogram> HistogramAsync(EventQuery query, CancellationToken cancellationToken)
    {
        var size = BucketFor(query.To - query.From);
        var count = (int)Math.Ceiling((query.To - query.From) / size);
        var buckets = Enumerable.Range(0, count)
            .Select(i => (Start: query.From + (size * i), Counts: Categories.ToDictionary(c => c, _ => 0L)))
            .ToList();

        await using var command = Command(HistogramSql, query, null);
        command.Parameters.Add(new NpgsqlParameter { Value = size, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Interval });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var start = await reader.GetFieldValueAsync<DateTimeOffset>(0, cancellationToken);
            var index = (int)((start - query.From) / size);
            if (index >= 0 && index < count)
            {
                buckets[index].Counts[reader.GetString(1)] = reader.GetInt64(2);
            }
        }

        return new EventHistogram((int)size.TotalSeconds, [.. buckets.Select(b => new EventBucket(b.Start, b.Counts))]);
    }

    // The parameters both queries share, $1 to $7.
    private NpgsqlCommand Command(string sql, EventQuery query, (DateTimeOffset At, string Key)? before)
    {
        var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = query.From, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = query.To, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)before?.At ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)before?.Key ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)query.User ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter
        {
            Value = query.Categories.Count == 0 ? DBNull.Value : query.Categories.ToArray(),
            DataTypeName = "text[]",
        });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)query.Search ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        return command;
    }

    private static async Task<LogEvent> ReadAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        var detail = JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(14)) ?? [];
        return new LogEvent(
            reader.GetString(1),
            await reader.GetFieldValueAsync<DateTimeOffset>(0, cancellationToken),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            await PartyAsync(reader, 5, cancellationToken),
            await PartyAsync(reader, 9, cancellationToken),
            detail);
    }

    // A party is four columns: kind, id, name, email. No kind means nobody (a system event).
    private static async Task<EventParty?> PartyAsync(NpgsqlDataReader reader, int first, CancellationToken cancellationToken)
    {
        if (await reader.IsDBNullAsync(first, cancellationToken))
        {
            return null;
        }

        return new EventParty(
            reader.GetString(first),
            await reader.IsDBNullAsync(first + 1, cancellationToken) ? null : reader.GetGuid(first + 1),
            await reader.IsDBNullAsync(first + 2, cancellationToken) ? "someone no longer here" : reader.GetString(first + 2),
            await reader.IsDBNullAsync(first + 3, cancellationToken) ? null : reader.GetString(first + 3));
    }
}
