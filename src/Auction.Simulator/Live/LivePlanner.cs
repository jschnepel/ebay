namespace Auction.Simulator.Live;

/// <summary>The terms of one new listing, ready for the API.</summary>
public sealed record Listing(
    string Title, string Description, long OpeningCents, long IncrementCents, string FloorType, long? ReserveCents, string BidEntry, TimeSpan Duration, string Photo);

/// <summary>
/// The live marketplace's choices, from a seeded random source and nothing else (no clock): what each new
/// listing looks like and how long it runs, what a simulated bidder will pay, what they bid next and how long
/// they wait before acting again.
/// </summary>
public sealed class LivePlanner(Random random)
{
    private static readonly TimeSpan Shortest = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    // Durations are 15 minutes plus an exponential tail, capped at a day: about 3 hours on average, with
    // plenty of short auctions so something is always about to end.
    private const double TailMeanHours = 2.75; // not money: a duration

    /// <summary>Above this opening price a visitor's listing gets no simulated bids.</summary>
    public const long VisitorOpeningCapCents = 500_000;

    public Listing NextListing(CatalogueItem item, bool catchUp)
    {
        ArgumentNullException.ThrowIfNull(item);
        var opening = RoundDown(Math.Max(100, (long)(item.PriceLowCents * Between(0.3, 0.8))), 100);
        var floor = Pick(("reserve", 0.4), ("minimum_bid", 0.3), ("absolute", 0.3));
        // A reserve must sit above the opening price and within the item's range; a range too narrow gets none.
        if (floor == "reserve" && opening + 100 > item.PriceHighCents)
        {
            floor = "minimum_bid";
        }

        long? reserve = floor == "reserve"
            ? Math.Clamp(RoundDown((long)Between(item.PriceLowCents, item.PriceHighCents), 100), opening + 100, item.PriceHighCents)
            : null;
        return new Listing(
            item.Title, item.Description, opening, IncrementFor(opening), floor, reserve,
            random.NextDouble() < 0.6 ? "proxy" : "fixed_increment", Duration(catchUp), item.Photos[random.Next(item.Photos.Count)]);
    }

    /// <summary>
    /// The most a simulated bidder will pay: for the marketplace's own listings, somewhere around the item's
    /// price range (so reserves are sometimes not met); for a visitor's listing, one to three times its
    /// opening price, and nothing at all when the opening price is absurd.
    /// </summary>
    public long Valuation(long openingCents, CatalogueItem? item)
    {
        if (item is not null)
        {
            return (long)Between(item.PriceLowCents * 0.5, item.PriceHighCents * 1.1);
        }

        return openingCents is <= 0 or > VisitorOpeningCapCents ? 0 : (long)(openingCents * Between(1.0, 3.0));
    }

    /// <summary>
    /// The next bid on an auction, never above the valuation: exactly the minimum for fixed-increment bidding;
    /// for proxy bidding a maximum somewhere between the minimum and the valuation. Null when the minimum
    /// is already beyond what the bidder will pay.
    /// </summary>
    public long? NextBid(AuctionView auction, long valuation)
    {
        ArgumentNullException.ThrowIfNull(auction);
        var minimum = auction.MinimumNextBidCents;
        if (valuation < minimum)
        {
            return null;
        }

        if (auction.BidEntry != "proxy")
        {
            return minimum;
        }

        // A proxy maximum part of the way to the valuation, so later bidders still have room to outbid.
        return minimum + (long)((valuation - minimum) * Between(0.2, 0.9));
    }

    /// <summary>How long a bidder waits before acting again: minutes when an auction is far from its end, seconds near it.</summary>
    public TimeSpan Think(TimeSpan untilEnd)
    {
        var (low, high) = untilEnd switch
        {
            _ when untilEnd <= TimeSpan.FromMinutes(10) => (3.0, 20.0),
            _ when untilEnd <= TimeSpan.FromHours(1) => (20.0, 90.0),
            _ => (60.0, 300.0),
        };
        return TimeSpan.FromSeconds(Between(low, high));
    }

    /// <summary>A uniformly chosen index below <paramref name="count"/>.</summary>
    public int Pick(int count) => random.Next(count);

    private TimeSpan Duration(bool catchUp)
    {
        // While the marketplace fills up, a third of new listings end within half an hour, so results appear soon.
        if (catchUp && random.NextDouble() < 0.35)
        {
            return Shortest + TimeSpan.FromMinutes(Between(0, 15));
        }

        var hours = -TailMeanHours * Math.Log(1 - random.NextDouble());
        var duration = Shortest + TimeSpan.FromHours(hours);
        return duration > Longest ? Longest : duration;
    }

    private static long IncrementFor(long openingCents) => openingCents switch
    {
        < 2_000 => 100,
        < 10_000 => 250,
        < 50_000 => 500,
        _ => 1_000,
    };

    private string Pick(params (string Value, double Weight)[] choices) // not money: odds
    {
        var roll = random.NextDouble() * choices.Sum(c => c.Weight);
        foreach (var (value, weight) in choices)
        {
            roll -= weight;
            if (roll < 0)
            {
                return value;
            }
        }

        return choices[^1].Value;
    }

    // A uniform draw; any amount made from it is rounded to whole cents by the caller.
    private double Between(double low, double high) => low + (random.NextDouble() * (high - low)); // not money

    private static long RoundDown(long cents, long step) => cents / step * step;
}
