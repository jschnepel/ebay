using Auction.Simulator;
using Auction.Simulator.Live;

namespace Auction.IntegrationTests.Simulation;

/// <summary>The live marketplace's choices: what it lists, for how long, what simulated people will pay and how often they act.</summary>
public sealed class LivePlannerTests
{
    private static readonly CatalogueItem Camera =
        new("Cameras", "Rangefinder camera", "A 35 mm rangefinder in working order.", 6_000, 18_000, ["cameras-2.jpg"]);

    [Fact]
    public void Durations_span_15_minutes_to_24_hours_and_average_about_3_hours()
    {
        var planner = new LivePlanner(new Random(1));
        var durations = Enumerable.Range(0, 10_000).Select(_ => planner.NextListing(Camera, catchUp: false).Duration).ToList();

        Assert.True(durations.Min() >= TimeSpan.FromMinutes(15), $"shortest {durations.Min()}");
        Assert.True(durations.Max() <= TimeSpan.FromHours(24), $"longest {durations.Max()}");
        Assert.InRange(durations.Average(d => d.TotalHours), 2.5, 3.5);
    }

    [Fact]
    public void While_catching_up_at_least_a_quarter_of_new_listings_end_within_half_an_hour()
    {
        var planner = new LivePlanner(new Random(2));
        var durations = Enumerable.Range(0, 2_000).Select(_ => planner.NextListing(Camera, catchUp: true).Duration).ToList();

        Assert.True(durations.Count(d => d <= TimeSpan.FromMinutes(30)) >= 500);
        Assert.True(durations.Min() >= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void Listings_mix_floor_types_and_bid_entries_with_reserves_between_the_opening_and_the_top_of_the_range()
    {
        var planner = new LivePlanner(new Random(3));
        var listings = Enumerable.Range(0, 1_000).Select(_ => planner.NextListing(Camera, catchUp: false)).ToList();

        foreach (var floor in new[] { "reserve", "minimum_bid", "absolute" })
        {
            Assert.True(listings.Count(l => l.FloorType == floor) >= 100, floor);
        }

        foreach (var entry in new[] { "proxy", "fixed_increment" })
        {
            Assert.True(listings.Count(l => l.BidEntry == entry) >= 100, entry);
        }

        Assert.All(listings, l =>
        {
            Assert.Equal(Camera.Title, l.Title);
            Assert.Contains(l.Photo, Camera.Photos);
            Assert.InRange(l.OpeningCents, 100, Camera.PriceLowCents);
            Assert.True(l.IncrementCents >= 100);
            if (l.FloorType == "reserve")
            {
                Assert.InRange(l.ReserveCents!.Value, l.OpeningCents + 1, Camera.PriceHighCents);
            }
            else
            {
                Assert.Null(l.ReserveCents);
            }
        });
    }

    [Fact]
    public void Simulated_bids_never_exceed_the_valuation_and_skip_absurd_visitor_listings()
    {
        var planner = new LivePlanner(new Random(4));

        Assert.Equal(0, planner.Valuation(600_000, item: null));
        for (var i = 0; i < 1_000; i++)
        {
            Assert.InRange(planner.Valuation(2_000, item: null), 2_000, 6_000);
            Assert.InRange(planner.Valuation(3_000, Camera), Camera.PriceLowCents / 2, Camera.PriceHighCents * 11 / 10);
        }

        var oneCentSteps = Auction(minimumNext: 2_001, increment: 1, bidEntry: "proxy");
        for (var i = 0; i < 1_000; i++)
        {
            var bid = planner.NextBid(oneCentSteps, valuation: 2_500);
            Assert.NotNull(bid);
            Assert.InRange(bid.Value, 2_001, 2_500);
        }

        Assert.Equal(3_000, planner.NextBid(Auction(minimumNext: 3_000, increment: 100, bidEntry: "fixed_increment"), valuation: 9_000));
        Assert.Null(planner.NextBid(Auction(minimumNext: 3_000, increment: 100, bidEntry: "proxy"), valuation: 2_999));
        Assert.Null(planner.NextBid(Auction(minimumNext: 3_000, increment: 100, bidEntry: "proxy"), valuation: 0));
    }

    [Fact]
    public void A_price_range_too_narrow_for_a_reserve_lists_without_one()
    {
        var planner = new LivePlanner(new Random(6));
        var narrow = Camera with { PriceLowCents = 150, PriceHighCents = 160 };

        var listings = Enumerable.Range(0, 500).Select(_ => planner.NextListing(narrow, catchUp: false)).ToList();

        Assert.All(listings, l => Assert.True(l.ReserveCents is null || (l.ReserveCents > l.OpeningCents && l.ReserveCents <= narrow.PriceHighCents), $"{l.OpeningCents} / {l.ReserveCents}"));
    }

    [Fact]
    public void A_failing_sign_in_waits_longer_each_time_up_to_half_an_hour()
    {
        Assert.Equal(
            [1, 2, 4, 8, 16, 30, 30],
            Enumerable.Range(1, 7).Select(failures => (int)LiveMarketplace.SignInBackoff(failures).TotalMinutes));
    }

    [Fact]
    public void Bidders_act_faster_as_the_end_approaches()
    {
        var planner = new LivePlanner(new Random(5));

        var nearEnd = Enumerable.Range(0, 1_000).Average(_ => planner.Think(TimeSpan.FromMinutes(2)).TotalSeconds);
        var farFromEnd = Enumerable.Range(0, 1_000).Average(_ => planner.Think(TimeSpan.FromHours(2)).TotalSeconds);

        Assert.True(nearEnd * 4 < farFromEnd, $"near the end {nearEnd:F1} s, far from it {farFromEnd:F1} s");
    }

    private static AuctionView Auction(long minimumNext, long increment, string bidEntry) =>
        new(Guid.NewGuid(), "active", bidEntry, 1_000, increment, minimumNext, minimumNext - increment, DateTimeOffset.UtcNow.AddHours(1), 3);
}
