using Auction.Domain;
using Npgsql;

namespace Auction.Api.Auctions;

/// <summary>
/// Auction reads straight from PostgreSQL as the api role, which can't write auction state or read
/// private maxima. Each read is a fixed number of queries whatever the page size. A draft is visible
/// only to its seller; to anyone else it doesn't exist.
/// </summary>
internal sealed class AuctionReader(NpgsqlDataSource dataSource, Caching.AuctionCache cache)
{
    private const string SummaryColumns =
        "id, title, status, opening_price_cents, current_price_cents, accepted_bid_count, floor_type, bid_entry, " +
        "reserve_cents, starts_at, ends_at, version, " +
        "(SELECT i.blob_id FROM auction.auction_images i WHERE i.auction_id = a.id AND i.is_cover) AS cover_image_id";

    private const string ListSql = $"""
        SELECT {SummaryColumns}
        FROM auction.auctions a
        WHERE status <> 'draft' AND ($1::timestamptz IS NULL OR (ends_at, id) > ($1::timestamptz, $2::uuid))
        ORDER BY ends_at, id
        LIMIT $3
        """;

    // UI-03: a title keyword (wildcards in it are taken literally) and a status: active, ending soon (active
    // and ending within the window), or closed. Same order and keyset as the directory.
    private const string SearchSql = $"""
        SELECT {SummaryColumns}
        FROM auction.auctions a
        WHERE status <> 'draft'
          AND ($4::text IS NULL OR title ILIKE '%' || $4 || '%' ESCAPE '\')
          AND ($5::text IS NULL
               OR ($5 = 'active' AND status = 'active')
               OR ($5 = 'ending_soon' AND status = 'active' AND ends_at <= clock_timestamp() + $6::interval)
               OR ($5 = 'closed' AND status = 'closed'))
          AND ($1::timestamptz IS NULL OR (ends_at, id) > ($1::timestamptz, $2::uuid))
        ORDER BY ends_at, id
        LIMIT $3
        """;

    private const string MineSql = $"""
        SELECT {SummaryColumns}
        FROM auction.auctions a
        WHERE seller_id = $4 AND ($1::timestamptz IS NULL OR (ends_at, id) > ($1::timestamptz, $2::uuid))
        ORDER BY ends_at, id
        LIMIT $3
        """;

    private const string BiddingSql = $"""
        SELECT {SummaryColumns}, leader_id
        FROM auction.auctions a
        WHERE EXISTS (SELECT 1 FROM auction.bids b WHERE b.auction_id = a.id AND b.bidder_id = $4)
          AND ($1::timestamptz IS NULL OR (ends_at, id) > ($1::timestamptz, $2::uuid))
        ORDER BY ends_at, id
        LIMIT $3
        """;

    private const string DetailSql = $"""
        SELECT {SummaryColumns}, description, increment_cents, outcome, seller_id, leader_id, published_at, closed_at,
               ARRAY(SELECT i.blob_id FROM auction.auction_images i WHERE i.auction_id = a.id ORDER BY i.position)
        FROM auction.auctions a
        WHERE a.id = $1 AND (status <> 'draft' OR seller_id = $2::uuid)
        """;

    private const string HasBidSql = "SELECT EXISTS (SELECT 1 FROM auction.bids WHERE auction_id = $1 AND bidder_id = $2)";

    // No auction has the id, and a reset finished after the id was made, so the reset is what removed it.
    private const string ClearedSql = """
        SELECT NOT EXISTS (SELECT 1 FROM auction.auctions WHERE id = $1)
           AND EXISTS (SELECT 1 FROM auction.demo_resets WHERE completed_at > $2)
        """;

    private const string VisibleSql =
        "SELECT status FROM auction.auctions WHERE id = $1 AND (status <> 'draft' OR seller_id = $2::uuid)";

    // Labels number bidders by their first bid in this auction; simulator accounts are always marked.
    private const string BidsSql = """
        WITH firsts AS (
            SELECT bidder_id, min(seq) AS first_seq FROM auction.bids WHERE auction_id = $1 GROUP BY bidder_id),
        labels AS (
            SELECT bidder_id, row_number() OVER (ORDER BY first_seq) AS n FROM firsts)
        SELECT b.seq, b.bidder_id, l.n, coalesce(u.kind = 'simulator', false), b.amount_cents, b.source, b.accepted_at
        FROM auction.bids b
        JOIN labels l ON l.bidder_id = b.bidder_id
        LEFT JOIN auction.users u ON u.id = b.bidder_id
        WHERE b.auction_id = $1 AND ($2::bigint IS NULL OR b.seq < $2::bigint)
        ORDER BY b.seq DESC
        LIMIT $3
        """;

    /// <summary>
    /// Published auctions by end time, then id; <paramref name="after"/> is the last one already seen. The
    /// first page comes from the cache while no change has been announced since it was read.
    /// </summary>
    public async Task<Page<AuctionSummary>> ListAsync((DateTimeOffset EndsAt, Guid Id)? after, int pageSize, CancellationToken cancellationToken)
    {
        if (after is not null)
        {
            return await PageAsync(ListSql, owner: null, after, pageSize, ReadSummary, cancellationToken);
        }

        if (await cache.GetDirectoryAsync<Page<AuctionSummary>>(pageSize) is { } cached)
        {
            return cached;
        }

        var generation = await cache.DirectoryGenerationAsync();
        var page = await PageAsync(ListSql, owner: null, after, pageSize, ReadSummary, cancellationToken);
        if (generation is { } current)
        {
            await cache.FillDirectoryAsync(current, pageSize, page);
        }

        return page;
    }

    /// <summary>The directory filtered by a title keyword and a status (UI-03), never from the cache.</summary>
    public async Task<Page<AuctionSummary>> SearchAsync(
        DirectoryFilter filter, (DateTimeOffset EndsAt, Guid Id)? after, int pageSize, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(SearchSql);
        command.Parameters.Add(Param(after?.EndsAt));
        command.Parameters.Add(Param(after?.Id));
        command.Parameters.Add(Param(pageSize + 1));
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)EscapeLike(filter.Query) ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)filter.Status ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(Param(filter.EndingSoonWindow));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<AuctionSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadSummary(reader));
        }

        return items.Count > pageSize
            ? new Page<AuctionSummary>(items[..pageSize], Cursors.Encode(items[pageSize - 1].EndsAt, items[pageSize - 1].Id))
            : new Page<AuctionSummary>(items, null);
    }

    private static string? EscapeLike(string? text) =>
        text?.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>The seller's own auctions, drafts included, in the same order as the directory.</summary>
    public Task<Page<AuctionSummary>> MineAsync(UserId seller, (DateTimeOffset EndsAt, Guid Id)? after, int pageSize, CancellationToken cancellationToken) =>
        PageAsync(MineSql, seller, after, pageSize, ReadSummary, cancellationToken);

    /// <summary>The auctions the bidder has an accepted bid on, each with whether they lead it now.</summary>
    public Task<Page<BiddingSummary>> BiddingAsync(UserId bidder, (DateTimeOffset EndsAt, Guid Id)? after, int pageSize, CancellationToken cancellationToken) =>
        PageAsync(
            BiddingSql,
            bidder,
            after,
            pageSize,
            reader => BiddingSummary.From(ReadSummary(reader), Nullable<Guid>(reader, 13) == bidder.Value),
            cancellationToken);

    /// <summary>
    /// One auction as the viewer may see it. Published auctions come from the read cache when it holds the
    /// newest announced version; drafts are always read from PostgreSQL and never cached.
    /// </summary>
    public async Task<AuctionDetail?> DetailAsync(Guid id, UserId? viewer, CancellationToken cancellationToken)
    {
        var row = await cache.GetAsync<DetailRow>(id) ?? await LoadAsync(id, viewer, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var isSeller = viewer is { } v && row.SellerId == v.Value;
        var leads = viewer is { } leader && row.LeaderId == leader.Value;
        return row.Public with
        {
            YouAreSeller = isSeller,
            YouLead = leads,
            YouHaveBid = leads || (viewer is { } bidder && !isSeller && await HasBidAsync(id, bidder, cancellationToken)),
            ReserveCents = isSeller ? row.ReserveCents : null,
        };
    }

    private async Task<bool> HasBidAsync(Guid id, UserId bidder, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(HasBidSql);
        command.Parameters.Add(Param(id));
        command.Parameters.Add(Param(bidder.Value));
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// Whether the id is one a demo reset cleared: it's shaped like the ids auctions get (UUIDv7, which carry the
    /// time they were made), no auction has it, and a reset finished after that time.
    /// </summary>
    public async Task<bool> ClearedByResetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (id.Version != 7)
        {
            return false;
        }

        var made = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(id.ToString("N")[..12], 16));
        await using var command = dataSource.CreateCommand(ClearedSql);
        command.Parameters.Add(Param(id));
        command.Parameters.Add(Param(made));
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<DetailRow?> LoadAsync(Guid id, UserId? viewer, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(DetailSql);
        command.Parameters.Add(Param(id));
        command.Parameters.Add(Param(viewer?.Value));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var images = await reader.GetFieldValueAsync<Guid[]>(20, cancellationToken);
        var row = ReadDetail(reader, images);
        if (row.Public.Status != "draft")
        {
            await cache.FillAsync(id, row.Public.Version, row);
        }

        return row;
    }

    private static DetailRow ReadDetail(NpgsqlDataReader reader, Guid[] images)
    {
        var summary = ReadSummary(reader);
        var detail = new AuctionDetail
        {
            Id = summary.Id,
            Title = summary.Title,
            Status = summary.Status,
            OpeningPriceCents = summary.OpeningPriceCents,
            CurrentPriceCents = summary.CurrentPriceCents,
            BidCount = summary.BidCount,
            FloorType = summary.FloorType,
            BidEntry = summary.BidEntry,
            ReserveMet = summary.ReserveMet,
            StartsAt = summary.StartsAt,
            EndsAt = summary.EndsAt,
            Version = summary.Version,
            Description = reader.GetString(13),
            IncrementCents = reader.GetInt64(14),
            MinimumNextBidCents = summary.CurrentPriceCents is { } price ? price + reader.GetInt64(14) : summary.OpeningPriceCents,
            Outcome = Text(reader, 15),
            YouAreSeller = false,
            YouLead = false,
            PublishedAt = Nullable<DateTimeOffset>(reader, 18),
            ClosedAt = Nullable<DateTimeOffset>(reader, 19),
            CoverImageId = summary.CoverImageId,
            ImageIds = images,
        };
        return new DetailRow(detail, reader.GetGuid(16), Nullable<Guid>(reader, 17), Nullable<long>(reader, 8));
    }

    private async Task<Page<T>> PageAsync<T>(
        string sql, UserId? owner, (DateTimeOffset EndsAt, Guid Id)? after, int pageSize, Func<NpgsqlDataReader, T> read,
        CancellationToken cancellationToken)
        where T : AuctionSummary
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(Param(after?.EndsAt));
        command.Parameters.Add(Param(after?.Id));
        command.Parameters.Add(Param(pageSize + 1));
        if (owner is { } id)
        {
            command.Parameters.Add(Param(id.Value));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(read(reader));
        }

        return items.Count > pageSize
            ? new Page<T>(items[..pageSize], Cursors.Encode(items[pageSize - 1].EndsAt, items[pageSize - 1].Id))
            : new Page<T>(items, null);
    }

    /// <summary>The auction's status if the viewer may see it, otherwise null.</summary>
    public async Task<string?> StatusAsync(Guid id, UserId? viewer, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(VisibleSql);
        command.Parameters.Add(Param(id));
        command.Parameters.Add(Param(viewer?.Value));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>The accepted bids, newest first, or null if the viewer may not see the auction.</summary>
    public async Task<Page<BidView>?> BidsAsync(Guid id, UserId? viewer, long? before, int pageSize, CancellationToken cancellationToken)
    {
        if (await StatusAsync(id, viewer, cancellationToken) is null)
        {
            return null;
        }

        await using var command = dataSource.CreateCommand(BidsSql);
        command.Parameters.Add(Param(id));
        command.Parameters.Add(Param(before));
        command.Parameters.Add(Param(pageSize + 1));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<BidView>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BidView
            {
                Sequence = reader.GetInt64(0),
                Bidder = Label(reader.GetGuid(1) == viewer?.Value, reader.GetInt64(2), reader.GetBoolean(3)),
                AmountCents = Nullable<long>(reader, 4),
                Source = reader.GetString(5),
                AcceptedAt = Nullable<DateTimeOffset>(reader, 6) ?? default,
            });
        }

        return items.Count > pageSize
            ? new Page<BidView>(items[..pageSize], Cursors.Encode(items[pageSize - 1].Sequence))
            : new Page<BidView>(items, null);
    }

    private static string Label(bool isViewer, long number, bool simulated)
    {
        if (isViewer)
        {
            return "You";
        }

        return simulated ? $"Bidder {number} (simulated)" : $"Bidder {number}";
    }

    // Drafts may still be missing values in the schema; the engine never writes one that is, so the
    // fallbacks only keep a read from failing.
    private static AuctionSummary ReadSummary(NpgsqlDataReader reader)
    {
        var floorType = reader.GetString(6);
        var current = Nullable<long>(reader, 4);
        var reserve = Nullable<long>(reader, 8);
        return new AuctionSummary
        {
            Id = reader.GetGuid(0),
            Title = Text(reader, 1) ?? "",
            Status = reader.GetString(2),
            OpeningPriceCents = Nullable<long>(reader, 3) ?? 0,
            CurrentPriceCents = current,
            BidCount = reader.GetInt64(5),
            FloorType = floorType,
            BidEntry = reader.GetString(7),
            ReserveMet = floorType == "reserve" ? current >= reserve : null,
            StartsAt = Nullable<DateTimeOffset>(reader, 9) ?? default,
            EndsAt = Nullable<DateTimeOffset>(reader, 10) ?? default,
            Version = reader.GetInt64(11),
            CoverImageId = Nullable<Guid>(reader, 12),
        };
    }

    private static T? Nullable<T>(NpgsqlDataReader reader, int ordinal)
        where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static NpgsqlParameter Param(object? value) => new() { Value = value ?? DBNull.Value };
}
