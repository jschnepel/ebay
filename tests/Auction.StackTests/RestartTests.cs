using System.Net;

namespace Auction.StackTests;

/// <summary>What a redeploy or a crash does to people using the site: they stay signed in and keep their data.</summary>
[Collection(StackRuns.Name)]
public sealed class RestartTests(StackUnderTest stack)
{
    [Fact]
    public async Task A_signed_in_seller_and_their_listing_survive_the_api_container_being_replaced()
    {
        var seller = await stack.SignUpAsync("Kept seller");
        var title = $"Kept lamp {Guid.NewGuid():N}"[..18];
        var auction = await seller.PublishAsync(title);
        var before = await stack.ContainerOfAsync("api");

        // What a redeploy does: a new container from the same image, with the same volumes.
        await stack.ComposeAsync("up", "-d", "--no-deps", "--no-build", "--force-recreate", "--wait", "api");
        await stack.WaitUntilReadyAsync();

        Assert.NotEqual(before, await stack.ContainerOfAsync("api"));
        Assert.Equal(seller.Id, (await seller.GetJsonAsync("/api/v1/account/me")).GetProperty("id").GetGuid());
        Assert.Equal(title, (await seller.GetJsonAsync($"/api/v1/auctions/{auction}")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Bids_go_through_again_once_the_engine_container_has_restarted()
    {
        var seller = await stack.SignUpAsync("Seller");
        var buyer = await stack.SignUpAsync("Buyer");
        var auction = await seller.PublishAsync($"Restart {Guid.NewGuid():N}"[..16]);

        await stack.ComposeAsync("restart", "engine");
        await stack.ComposeAsync("up", "-d", "--no-deps", "--no-build", "--wait", "engine");
        await stack.WaitUntilReadyAsync();

        using var bid = await buyer.BidAsync(auction, 1200);
        Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        Assert.Equal(1, (await buyer.GetJsonAsync($"/api/v1/auctions/{auction}")).GetProperty("bidCount").GetInt64());
    }
}
