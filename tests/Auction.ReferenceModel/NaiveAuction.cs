namespace Auction.ReferenceModel;

/// <summary>Floor type, mirrored here on purpose so this project never references production code.</summary>
public enum RefFloor
{
    Reserve,
    MinimumBid,
    Absolute,
}

/// <summary>Bid entry mode, mirrored on purpose.</summary>
public enum RefEntry
{
    Proxy,
    FixedIncrement,
}

/// <summary>The terms the naive model needs, in plain cents.</summary>
public sealed record RefTerms(long OpeningCents, long IncrementCents, RefFloor Floor, long? ReserveCents, RefEntry Entry);

/// <summary>One bid request, in arrival order.</summary>
public sealed record RefBid(Guid BidderId, long AmountCents);

/// <summary>What the naive model concludes after replaying a history.</summary>
public sealed record RefResult(IReadOnlyList<bool> Accepted, Guid? Leader, long? PriceCents, string Outcome);

/// <summary>The standing the naive model derives from bids already known to be accepted.</summary>
public sealed record RefStanding(Guid? Leader, long? PriceCents, string Outcome);

/// <summary>
/// A deliberately naive statement of the auction rules. It keeps no
/// incremental state: before every bid it recomputes the leader and price from scratch by scanning the
/// accepted history. It is slow on purpose and shares no code with Auction.Domain, so agreement between the
/// two is evidence, not an echo.
/// </summary>
public static class NaiveAuction
{
    public static RefResult Replay(RefTerms terms, Guid sellerId, IEnumerable<RefBid> bids)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(bids);

        var accepted = new List<RefBid>();
        var flags = new List<bool>();
        foreach (var bid in bids)
        {
            var ok = bid.BidderId != sellerId && IsAcceptable(terms, accepted, bid);
            flags.Add(ok);
            if (ok)
            {
                accepted.Add(bid);
            }
        }

        var (leader, price) = Standing(terms, accepted);
        return new RefResult(flags, leader, price, Outcome(terms, accepted, price));
    }

    /// <summary>
    /// The leader, price and closing outcome for bids already accepted, in acceptance order (for proxy
    /// bidding, each bidder's maximum in the order it was reached). The verifier uses this on the database's
    /// own records.
    /// </summary>
    public static RefStanding Judge(RefTerms terms, IReadOnlyList<RefBid> accepted)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(accepted);
        var bids = accepted.ToList();
        var (leader, price) = Standing(terms, bids);
        return new RefStanding(leader, price, Outcome(terms, bids, price));
    }

    private static bool IsAcceptable(RefTerms terms, List<RefBid> accepted, RefBid bid)
    {
        if (accepted.Count == 0)
        {
            return bid.AmountCents >= terms.OpeningCents;
        }

        var (_, price) = Standing(terms, accepted);
        var next = price!.Value + terms.IncrementCents;
        if (terms.Entry == RefEntry.FixedIncrement)
        {
            return bid.AmountCents == next;
        }

        var ownMax = accepted.Where(b => b.BidderId == bid.BidderId).Select(b => (long?)b.AmountCents).Max();
        return bid.AmountCents >= next && (ownMax is null || bid.AmountCents >= ownMax.Value);
    }

    private static (Guid? Leader, long? Price) Standing(RefTerms terms, List<RefBid> accepted)
    {
        if (accepted.Count == 0)
        {
            return (null, null);
        }

        if (terms.Entry == RefEntry.FixedIncrement)
        {
            var last = accepted[^1];
            return (last.BidderId, last.AmountCents);
        }

        return ProxyStanding(terms, accepted);
    }

    // Each bidder's maximum is their largest accepted amount. Among equal maxima, the bidder who reached
    // that amount first wins: find the first index in history at which each bidder submitted their maximum.
    private static (Guid? Leader, long? Price) ProxyStanding(RefTerms terms, List<RefBid> accepted)
    {
        var bidders = accepted.Select(b => b.BidderId).Distinct().ToList();
        var best = bidders
            .Select(id =>
            {
                var max = accepted.Where(b => b.BidderId == id).Max(b => b.AmountCents);
                var firstAt = accepted.FindIndex(b => b.BidderId == id && b.AmountCents == max);
                return (Id: id, Max: max, FirstAt: firstAt);
            })
            .ToList();

        var leader = best[0];
        foreach (var candidate in best.Where(c => c.Max > leader.Max || (c.Max == leader.Max && c.FirstAt < leader.FirstAt)))
        {
            leader = candidate;
        }

        long price;
        if (best.Count == 1)
        {
            price = terms.OpeningCents;
        }
        else
        {
            var runnerUp = best.Where(c => c.Id != leader.Id).Max(c => c.Max);
            price = Math.Min(leader.Max, runnerUp + terms.IncrementCents);
        }

        if (terms.Floor == RefFloor.Reserve && terms.ReserveCents is long reserve && leader.Max >= reserve && price < reserve)
        {
            price = reserve;
        }

        return (leader.Id, price);
    }

    private static string Outcome(RefTerms terms, List<RefBid> accepted, long? price)
    {
        if (accepted.Count == 0)
        {
            return "NoBids";
        }

        if (terms.Floor == RefFloor.Reserve && terms.ReserveCents is long reserve && price < reserve)
        {
            return "ReserveNotMet";
        }

        return "Sold";
    }
}
