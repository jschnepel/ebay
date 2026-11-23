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
