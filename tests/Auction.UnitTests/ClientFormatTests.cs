using Auction.Web.Client;

namespace Auction.UnitTests;

/// <summary>Display text the admin tables and bid history show.</summary>
public sealed class ClientFormatTests
{
    [Theory]
    [InlineData("2026-11-22T14:30", 0, 0)]
    [InlineData("2026-11-22T14:30:00", 0, 0)]
    [InlineData("2026-11-22T14:30:07.125", 7, 125)]
    public void An_edited_schedule_preserves_the_local_time(string input, int second, int millisecond)
    {
        var expected = new DateTime(2026, 11, 22, 14, 30, second, millisecond, DateTimeKind.Local);
        Assert.Equal(new DateTimeOffset(expected), Format.ParseLocalInput(input));
    }

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
