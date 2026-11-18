using Auction.ReferenceModel;
using Npgsql;

namespace Auction.Verifier;

/// <summary>One broken invariant: which auction, which rule, and what was found.</summary>
public sealed record Violation(Guid AuctionId, string Rule, string Detail);

/// <summary>Which published auctions to check: all, those published since a time, and/or one seller's.</summary>
public sealed record VerifierScope(DateTimeOffset? Since = null, Guid? SellerId = null);

/// <summary>What one verification pass checked and found.</summary>
public sealed record VerifierReport(int AuctionsChecked, int BidsChecked, IReadOnlyList<Violation> Violations)
{
    public bool Passed => Violations.Count == 0;
}

/// <summary>
/// Recomputes every published auction in scope from the database's own records and compares the result
/// with what the engine stored (SIM-03):
/// <list type="bullet">
/// <item>The accepted history is gap-free, never by the seller, inside the auction's open window, and its
/// visible prices never fall.</item>
/// <item>The accepted-bid count agrees with the history (fixed increment) or the latest maximum (proxy).</item>
/// <item>The reference model, given the accepted bids (fixed increment) or each bidder's maximum in the order
/// it was reached (proxy), names the same leader and price, and for a closed auction the same outcome.</item>
/// </list>
/// </summary>
public static class InvariantVerifier
{
    private const string AuctionsSql = """
        SELECT id, seller_id, status, opening_price_cents, increment_cents, floor_type, reserve_cents, bid_entry,
               starts_at, ends_at, current_price_cents, leader_id, accepted_bid_count, outcome
        FROM auction.auctions
        WHERE status <> 'draft' AND ($1::timestamptz IS NULL OR published_at >= $1) AND ($2::uuid IS NULL OR seller_id = $2)
        ORDER BY id
        """;

    private const string BidsSql = """
        SELECT auction_id, seq, bidder_id, amount_cents, source, accepted_at
        FROM auction.bids b
        WHERE EXISTS (SELECT 1 FROM auction.auctions a WHERE a.id = b.auction_id AND a.status <> 'draft'
                      AND ($1::timestamptz IS NULL OR a.published_at >= $1) AND ($2::uuid IS NULL OR a.seller_id = $2))
        ORDER BY auction_id, seq
        """;

    private const string MaximaSql = """
        SELECT auction_id, bidder_id, maximum_cents, sequence
        FROM auction.proxy_maxima m
        WHERE EXISTS (SELECT 1 FROM auction.auctions a WHERE a.id = m.auction_id AND a.status <> 'draft'
                      AND ($1::timestamptz IS NULL OR a.published_at >= $1) AND ($2::uuid IS NULL OR a.seller_id = $2))
        ORDER BY auction_id, sequence
        """;

    private sealed record AuctionRow(
        Guid Id, Guid SellerId, string Status, RefTerms Terms, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
        long? PriceCents, Guid? LeaderId, long AcceptedBidCount, string? Outcome);

    private sealed record BidRow(long Seq, Guid BidderId, long? AmountCents, string Source, DateTimeOffset AcceptedAt);

    private sealed record MaximumRow(Guid BidderId, long MaximumCents, long Sequence);

    /// <summary>Checks every published auction in <paramref name="scope"/>.</summary>
    public static async Task<VerifierReport> VerifyAsync(NpgsqlDataSource dataSource, VerifierScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(scope);

        // One snapshot for all three reads, so an auction and its history are always consistent with each other.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var auctions = await ReadAsync(connection, AuctionsSql, scope, ReadAuction, cancellationToken);
        var bids = (await ReadAsync(connection, BidsSql, scope, r => (r.GetGuid(0), new BidRow(
            r.GetInt64(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5))), cancellationToken))
            .ToLookup(b => b.Item1, b => b.Item2);
        var maxima = (await ReadAsync(connection, MaximaSql, scope, r => (r.GetGuid(0), new MaximumRow(r.GetGuid(1), r.GetInt64(2), r.GetInt64(3))), cancellationToken))
            .ToLookup(m => m.Item1, m => m.Item2);
        await transaction.CommitAsync(cancellationToken);

        var violations = new List<Violation>();
        foreach (var auction in auctions)
        {
            violations.AddRange(Check(auction, [.. bids[auction.Id]], [.. maxima[auction.Id]]));
        }

        return new VerifierReport(auctions.Count, bids.Sum(group => group.Count()), violations);
    }

    private static List<Violation> Check(AuctionRow auction, List<BidRow> history, List<MaximumRow> maxima)
    {
        var found = new List<Violation>();
        void Fail(string rule, string detail) => found.Add(new Violation(auction.Id, rule, detail));

        CheckHistory(auction, history, Fail);

        var bidderBids = history.Where(b => b.Source == "bidder").ToList();
        CheckCount(auction, bidderBids.Count, maxima, Fail);

        var accepted = auction.Terms.Entry == RefEntry.FixedIncrement
            ? bidderBids.Select(b => new RefBid(b.BidderId, b.AmountCents ?? 0)).ToList()
            : maxima.OrderBy(m => m.Sequence).Select(m => new RefBid(m.BidderId, m.MaximumCents)).ToList();
        if (auction.Terms.Entry == RefEntry.FixedIncrement)
        {
            var replay = NaiveAuction.Replay(auction.Terms, auction.SellerId, accepted);
            if (replay.Accepted.Any(ok => !ok))
            {
                Fail("fixed_increment_rules", "the reference model refuses a bid the engine accepted");
            }
        }

        var expected = NaiveAuction.Judge(auction.Terms, accepted);
        if (expected.Leader != auction.LeaderId || expected.PriceCents != auction.PriceCents)
        {
            Fail("standing", $"engine: leader {auction.LeaderId}, price {auction.PriceCents}; reference: leader {expected.Leader}, price {expected.PriceCents}");
        }

        if (auction.Status == "closed" && !string.Equals(Normalize(auction.Outcome), expected.Outcome, StringComparison.Ordinal))
        {
            Fail("outcome", $"engine: {auction.Outcome}; reference: {expected.Outcome}");
        }

        return found;
    }

    // Fixed increment: every accepted bid is a history row. Proxy: a leader raising their own maximum without
    // moving the price adds no public row (maxima stay private), so the history may hold fewer bidder rows,
    // and the latest maximum was reached by the auction's last accepted bid.
    private static void CheckCount(AuctionRow auction, int bidderRows, List<MaximumRow> maxima, Action<string, string> fail)
    {
        if (auction.Terms.Entry == RefEntry.FixedIncrement)
        {
            if (bidderRows != auction.AcceptedBidCount)
            {
                fail("bid_count", $"{bidderRows} bidder bids in history, the auction counts {auction.AcceptedBidCount}");
            }

            return;
        }

        var lastSequence = maxima.Count == 0 ? 0 : maxima.Max(m => m.Sequence);
        if (bidderRows > auction.AcceptedBidCount || lastSequence != auction.AcceptedBidCount)
        {
            fail("bid_count", $"{bidderRows} bidder rows and a latest maximum at {lastSequence}, the auction counts {auction.AcceptedBidCount}");
        }
    }

    private static void CheckHistory(AuctionRow auction, List<BidRow> history, Action<string, string> fail)
    {
        long? lastVisible = null;
        for (var i = 0; i < history.Count; i++)
        {
            var bid = history[i];
            if (bid.Seq != i + 1)
            {
                fail("history_gap", $"bid {i + 1} has sequence {bid.Seq}");
            }

            if (bid.BidderId == auction.SellerId)
            {
                fail("self_bid", $"bid {bid.Seq} is by the seller");
            }

            if (bid.AcceptedAt < auction.StartsAt || bid.AcceptedAt >= auction.EndsAt)
            {
                fail("outside_window", $"bid {bid.Seq} accepted at {bid.AcceptedAt:O}, open {auction.StartsAt:O} to {auction.EndsAt:O}");
            }

            if (bid.AmountCents is { } amount)
            {
                if (amount < lastVisible)
                {
                    fail("price_fell", $"bid {bid.Seq} shows {amount} after {lastVisible}");
                }

                lastVisible = amount;
            }
        }

        if (lastVisible != auction.PriceCents && !(lastVisible is null && auction.PriceCents is null))
        {
            fail("price_not_last_visible", $"the last visible amount is {lastVisible}, the auction shows {auction.PriceCents}");
        }
    }

    // The schema stores snake_case outcomes; the reference model names them in PascalCase.
    private static string? Normalize(string? outcome) => outcome switch
    {
        "sold" => "Sold",
        "reserve_not_met" => "ReserveNotMet",
        "no_bids" => "NoBids",
        _ => outcome,
    };

    private static AuctionRow ReadAuction(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetString(2),
        new RefTerms(
            r.GetInt64(3),
            r.GetInt64(4),
            r.GetString(5) switch { "reserve" => RefFloor.Reserve, "minimum_bid" => RefFloor.MinimumBid, _ => RefFloor.Absolute },
            r.IsDBNull(6) ? null : r.GetInt64(6),
            r.GetString(7) == "proxy" ? RefEntry.Proxy : RefEntry.FixedIncrement),
        r.GetFieldValue<DateTimeOffset>(8),
        r.GetFieldValue<DateTimeOffset>(9),
        r.IsDBNull(10) ? null : r.GetInt64(10),
        r.IsDBNull(11) ? null : r.GetGuid(11),
        r.GetInt64(12),
        r.IsDBNull(13) ? null : r.GetString(13));

    private static async Task<List<T>> ReadAsync<T>(
        NpgsqlConnection connection, string sql, VerifierScope scope, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)scope.Since ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)scope.SellerId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}
