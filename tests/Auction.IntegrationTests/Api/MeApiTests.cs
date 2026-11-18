using System.Net;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

/// <summary>The signed-in user's own lists: the auctions they sell (drafts included) and the ones they bid on.</summary>
public sealed class MeApiTests(MigratedDatabase db)
{
    [Fact]
    public async Task My_auctions_lists_the_sellers_drafts_and_published_auctions_and_nobody_elses()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var other = await api.SignUpAsync();
        var draft = await seller.Http.CreateDraftAsync();
        var published = await seller.PublishedAsync();
        var foreign = await other.PublishedAsync();

        var mine = await AllIdsAsync(seller, "/api/v1/me/auctions");

        Assert.Equal(new[] { draft, published }.Order(), mine.Order());
        Assert.DoesNotContain(foreign, mine);
        Assert.Equal(HttpStatusCode.Unauthorized, await api.Anonymous().StatusOfGetAsync("/api/v1/me/auctions"));
    }

    [Fact]
    public async Task My_bids_lists_the_auctions_a_user_bid_on_and_whether_they_lead()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var first = await api.SignUpAsync();
        var second = await api.SignUpAsync();
        var contested = await seller.PublishedAsync(ApiCalls.Terms(bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));
        var untouched = await seller.PublishedAsync();
        using (var bid = await first.Http.BidAsync(contested, 1000))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        using (var outbid = await second.Http.BidAsync(contested, 1100))
        {
            Assert.Equal(HttpStatusCode.Created, outbid.StatusCode);
        }

        var firstItem = Assert.Single((await first.Http.GetJsonAsync("/api/v1/me/bids")).GetProperty("items").EnumerateArray());
        var secondItem = Assert.Single((await second.Http.GetJsonAsync("/api/v1/me/bids")).GetProperty("items").EnumerateArray());

        Assert.Equal((contested, false, 1100L), (firstItem.GetProperty("id").GetGuid(), firstItem.GetProperty("youLead").GetBoolean(),
            firstItem.GetProperty("currentPriceCents").GetInt64()));
        Assert.True(secondItem.GetProperty("youLead").GetBoolean());
        Assert.Empty((await seller.Http.GetJsonAsync("/api/v1/me/bids")).GetProperty("items").EnumerateArray());
        Assert.DoesNotContain(untouched, await AllIdsAsync(first, "/api/v1/me/bids"));
        Assert.Equal(HttpStatusCode.Unauthorized, await api.Anonymous().StatusOfGetAsync("/api/v1/me/bids"));
    }

    [Fact]
    public async Task The_detail_shows_the_minimum_next_bid()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));

        var before = await api.Anonymous().GetJsonAsync($"/api/v1/auctions/{id}");
        using (var bid = await buyer.Http.BidAsync(id, 1000))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        var after = await api.Anonymous().GetJsonAsync($"/api/v1/auctions/{id}");

        Assert.Equal(1000, before.GetProperty("minimumNextBidCents").GetInt64());
        Assert.Equal(1100, after.GetProperty("minimumNextBidCents").GetInt64());
    }

    private static async Task<List<Guid>> AllIdsAsync(ApiUser user, string url)
    {
        var ids = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await user.Http.GetJsonAsync(cursor is null ? url : $"{url}?cursor={Uri.EscapeDataString(cursor)}");
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        return ids;
    }
}
