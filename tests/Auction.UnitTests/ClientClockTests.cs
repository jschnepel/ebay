using Auction.Web.Client;

namespace Auction.UnitTests;

/// <summary>The auction clock people read on cards and auction pages: time left in words, and how much has run.</summary>
public sealed class ClientClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static AuctionSummary Auction(string status, TimeSpan startedAgo, TimeSpan endsIn) =>
        new() { Status = status, StartsAt = Now - startedAgo, EndsAt = Now + endsIn };

    [Theory]
    [InlineData(3 * 24 * 60 + 5 * 60 + 7, "3 d 5 h left")]
    [InlineData(2 * 24 * 60, "2 d left")]
    [InlineData(5 * 60 + 14, "5 h 14 min left")]
    [InlineData(3 * 60, "3 h left")]
    [InlineData(42, "42 min left")]
    [InlineData(1, "1 min left")]
    public void An_active_auction_says_how_long_is_left_in_words(int minutes, string expected)
    {
        var auction = Auction("active", TimeSpan.FromHours(1), TimeSpan.FromMinutes(minutes));

        Assert.Equal(expected, Clock.Text(auction, Now));
    }

    [Fact]
    public void The_last_minute_and_the_moments_after_the_end_read_plainly()
    {
        Assert.Equal("Under a minute left", Clock.Text(Auction("active", TimeSpan.FromHours(1), TimeSpan.FromSeconds(40)), Now));
        Assert.Equal("Closing", Clock.Text(Auction("active", TimeSpan.FromHours(1), TimeSpan.FromSeconds(-3)), Now));
    }

    [Theory]
    [InlineData("closed", "Ended")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("suspended", "Paused by an admin")]
    [InlineData("draft", "Not listed yet")]
    public void Auctions_that_are_not_running_say_why_instead_of_counting(string status, string expected)
    {
        Assert.Equal(expected, Clock.Text(Auction(status, TimeSpan.FromHours(1), TimeSpan.FromHours(1)), Now));
    }

    [Fact]
    public void A_scheduled_auction_says_when_it_starts()
    {
        var auction = Auction("scheduled", TimeSpan.FromMinutes(-90), TimeSpan.FromHours(5));

        Assert.Equal("Starts in 1 h 30 min", Clock.Text(auction, Now));
    }

    [Fact]
    public void Ending_soon_means_active_with_an_hour_or_less_left()
    {
        Assert.True(Clock.EndingSoon(Auction("active", TimeSpan.FromHours(2), TimeSpan.FromMinutes(60)), Now));
        Assert.False(Clock.EndingSoon(Auction("active", TimeSpan.FromHours(2), TimeSpan.FromMinutes(61)), Now));
        Assert.False(Clock.EndingSoon(Auction("closed", TimeSpan.FromHours(2), TimeSpan.FromMinutes(-5)), Now));
    }

    [Fact]
    public void The_meter_is_the_share_of_the_auction_that_has_run_and_stays_between_0_and_1()
    {
        Assert.Equal(0.25m, Clock.Elapsed(Auction("active", TimeSpan.FromHours(1), TimeSpan.FromHours(3)), Now));
        Assert.Equal(0, Clock.Elapsed(Auction("scheduled", TimeSpan.FromHours(-1), TimeSpan.FromHours(3)), Now));
        Assert.Equal(1, Clock.Elapsed(Auction("closed", TimeSpan.FromHours(4), TimeSpan.FromHours(-1)), Now));
    }
}
