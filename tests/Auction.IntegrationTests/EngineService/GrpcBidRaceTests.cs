using Auction.Domain;
using Auction.Engine;
using Auction.IntegrationTests.Database;
using Auction.IntegrationTests.Engine;

namespace Auction.IntegrationTests.EngineService;

/// <summary>The fifty-bidder races, with every command sent over gRPC to the engine container.</summary>
public sealed class GrpcBidRaceTests(MigratedDatabase db) : BidRaceScenarios(db)
{
    protected override async Task<AuctionId> ActiveAsync(UserId seller, AuctionTerms terms)
    {
        var commands = await CommandsAsync();
        var created = await commands.CreateDraftAsync(EngineScenario.As(seller), terms);
        Assert.True(created.Succeeded, $"create failed: {created.Error}");
        var published = await commands.PublishAsync(EngineScenario.As(seller), created.AuctionId!.Value);
        Assert.True(published.Succeeded, $"publish failed: {published.Error}");
        return created.AuctionId.Value;
    }

    protected override async Task<CommandOutcome> BidAsync(UserId bidder, AuctionId auction, long cents) =>
        await (await CommandsAsync()).PlaceBidAsync(EngineScenario.As(bidder), auction, cents);

    private async Task<GrpcCommands> CommandsAsync() => new((await Db.EngineServiceAsync()).Commands);
}
