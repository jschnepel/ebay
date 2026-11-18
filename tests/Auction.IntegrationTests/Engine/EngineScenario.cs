using Auction.Domain;
using Auction.Engine;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Engine;

/// <summary>Shared setup for the engine tests: terms, commands with fresh keys, and published auctions.</summary>
internal static class EngineScenario
{
    /// <summary>Limits that allow auctions lasting a few seconds, so deadline tests run quickly.</summary>
    public static readonly EngineOptions ShortAuctions = new()
    {
        Limits = AuctionLimits.Default with { MinDuration = TimeSpan.FromSeconds(1) },
    };

    public static CommandContext As(UserId actor) => new(actor, Guid.NewGuid().ToString());

    /// <summary>$10.00 opening, $1.00 increment, $20.00 reserve; starts at once when published.</summary>
    public static AuctionTerms Terms(DateTimeOffset now, BidEntry entry = BidEntry.Proxy, TimeSpan? duration = null) => new()
    {
        Title = "Film camera",
        Description = "A 35 mm rangefinder in working order.",
        OpeningPrice = new Money(1000),
        Increment = new Money(100),
        FloorType = FloorType.Reserve,
        Reserve = new Money(2000),
        BidEntry = entry,
        StartsAt = now.AddMinutes(-1),
        EndsAt = now + (duration ?? TimeSpan.FromHours(1)),
    };

    public static async Task<AuctionId> DraftAsync(AuctionEngine engine, UserId seller, AuctionTerms terms)
    {
        var created = await engine.CreateDraftAsync(As(seller), terms, TestContext.Current.CancellationToken);
        Assert.True(created.Succeeded, $"create failed: {created.Error}");
        return created.AuctionId!.Value;
    }

    public static async Task<AuctionId> ActiveAsync(AuctionEngine engine, UserId seller, AuctionTerms terms)
    {
        var id = await DraftAsync(engine, seller, terms);
        var published = await engine.PublishAsync(As(seller), id, TestContext.Current.CancellationToken);
        Assert.True(published.Succeeded, $"publish failed: {published.Error}");
        return id;
    }

    public static async Task<AuctionId> ActiveAsync(MigratedDatabase db, AuctionEngine engine, BidEntry entry = BidEntry.Proxy) =>
        await ActiveAsync(engine, UserId.New(), Terms(await db.NowAsync(), entry));

    public static Task<CommandOutcome> BidAsync(AuctionEngine engine, UserId bidder, AuctionId auction, long cents) =>
        engine.PlaceBidAsync(As(bidder), auction, new Money(cents), TestContext.Current.CancellationToken);

    public static Task<long> CountAsync(MigratedDatabase db, string table, AuctionId auction) =>
        db.ScalarAsync<long>(MigratedDatabase.Verifier, $"SELECT count(*) FROM {table} WHERE auction_id = $1", auction.Value);
}
