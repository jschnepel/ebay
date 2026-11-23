using Auction.Web.Client;

namespace Auction.UnitTests;

/// <summary>Display text the admin tables and bid history show.</summary>
public sealed class ClientFormatTests
{
    [Theory]
    [InlineData("active", "is-live")]
    [InlineData("suspended", "is-off")]
    [InlineData("cancelled", "is-off")]
    [InlineData("closed", "")]
    [InlineData("scheduled", "")]
    [InlineData("draft", "")]
    public void A_status_tag_is_green_while_bidding_is_open_and_red_when_an_admin_stopped_it(string status, string expected)
    {
        Assert.Equal(expected, Format.StatusTone(status));
    }

    [Theory]
    [InlineData("active", true, null, "Leading")]
    [InlineData("active", false, null, "Outbid")]
    [InlineData("closed", true, null, "Won")]
    [InlineData("closed", true, true, "Won")]
    [InlineData("closed", true, false, "Didn't win")]
    [InlineData("closed", false, null, "Didn't win")]
    [InlineData("cancelled", true, null, "Cancelled")]
    public void A_bid_on_an_ended_auction_says_whether_it_won(string status, bool youLead, bool? reserveMet, string expected)
    {
        var auction = new BiddingSummary { Status = status, YouLead = youLead, ReserveMet = reserveMet };

        Assert.Equal(expected, Format.Standing(auction));
    }

    [Theory]
    [InlineData("absolute", null, "No reserve")]
    [InlineData("minimum_bid", null, "No reserve beyond the opening price")]
    [InlineData("reserve", false, "Hidden reserve, not met yet")]
    [InlineData("reserve", true, "Hidden reserve, met")]
    public void A_bidder_is_told_the_price_floor_of_a_live_auction(string floorType, bool? reserveMet, string expected)
    {
        var auction = new AuctionSummary { Status = "active", FloorType = floorType, ReserveMet = reserveMet };

        Assert.Equal(expected, Format.Floor(auction));
    }

    [Theory]
    [InlineData("closed", 0L, "reserve", false, "No bids")]
    [InlineData("closed", 3L, "reserve", false, "Reserve not met")]
    [InlineData("closed", 3L, "reserve", true, "Sold")]
    [InlineData("closed", 1L, "absolute", null, "Sold")]
    [InlineData("active", 1L, "absolute", null, null)]
    [InlineData("cancelled", 1L, "absolute", null, null)]
    public void An_ended_auction_says_how_it_came_out(string status, long bids, string floorType, bool? reserveMet, string? expected)
    {
        var auction = new AuctionSummary { Status = status, BidCount = bids, FloorType = floorType, ReserveMet = reserveMet };

        Assert.Equal(expected, Format.Outcome(auction));
    }

    [Theory]
    [InlineData(true, true, 7000L, 2000L, "Bid placed. You are the highest bidder, up to your maximum of $70.00. The price shows only what it takes to lead.")]
    [InlineData(true, false, 1100L, 1100L, "Bid placed. You are the highest bidder.")]
    [InlineData(false, true, 5000L, 5000L, "Bid placed, but an earlier bid with the same maximum leads.")]
    [InlineData(false, true, 5000L, 5100L, "Bid placed, but another bidder's maximum is higher.")]
    public void A_placed_bid_is_described_as_it_landed(bool leads, bool proxy, long cents, long price, string expected)
    {
        Assert.Equal(expected, Format.Placed(leads, proxy, cents, price));
    }

    [Fact]
    public void A_date_names_its_month_so_it_reads_the_same_everywhere()
    {
        var local = new DateTime(2026, 11, 3, 8, 4, 0, DateTimeKind.Local);

        Assert.Equal("3 Nov 2026, 08:04", Format.Time(new DateTimeOffset(local)));
    }

    [Fact]
    public void A_recent_time_says_today_and_an_older_one_gives_its_date()
    {
        var now = DateTimeOffset.Now;
        var noon = new DateTimeOffset(now.Year, now.Month, now.Day, 12, 0, 5, now.Offset);
        var lastYear = new DateTimeOffset(new DateTime(now.Year - 1, 3, 9, 17, 30, 0, DateTimeKind.Local));

        Assert.Equal("Today 12:00:05", Format.Recent(noon));
        Assert.Equal($"9 Mar {now.Year - 1}, 17:30", Format.Recent(lastYear));
    }

    [Fact]
    public void The_time_zone_is_given_as_its_distance_from_utc()
    {
        Assert.Matches(@"^UTC[+\u2212]\d{2}:\d{2}$", Format.Zone(DateTimeOffset.Now));
        Assert.Equal("UTC\u221207:00", Format.Zone(new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.FromHours(-7))));
        Assert.Equal("UTC+05:30", Format.Zone(new DateTimeOffset(2026, 11, 23, 8, 0, 0, new TimeSpan(5, 30, 0))));
    }

    [Theory]
    [InlineData(0, "live auctions")]
    [InlineData(1, "live auction")]
    [InlineData(2, "live auctions")]
    public void A_count_label_agrees_with_its_number(long count, string expected)
    {
        Assert.Equal(expected, Format.Noun(count, "live auction", "live auctions"));
    }

    [Theory]
    [InlineData("bid", 1200L, "bid")]
    [InlineData("proxy", 1200L, "automatic")]
    [InlineData("bid", null, "outbid at once")]
    public void A_bid_says_how_it_was_made_and_a_beaten_one_keeps_its_amount_private(string source, long? cents, string expected)
    {
        Assert.Equal(expected, Format.How(source, cents));
    }
}
