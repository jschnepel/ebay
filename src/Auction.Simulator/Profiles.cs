namespace Auction.Simulator;

/// <summary>
/// A workload: how many auctions the simulated seller lists, how many bidders run, for how long, and how long
/// each bidder pauses between bids (zero for a closed loop).
/// </summary>
public sealed record Profile(string Name, int Auctions, int Bidders, TimeSpan Duration, TimeSpan ThinkMin, TimeSpan ThinkMax)
{
    /// <summary>A quick check that everything is wired: one auction, three bidders.</summary>
    public static Profile Smoke { get; } = new("smoke", 1, 3, TimeSpan.FromSeconds(15), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1));

    /// <summary>People browsing several auctions and bidding at a human pace, in bursts and lulls.</summary>
    public static Profile Realistic { get; } = new("realistic", 5, 20, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(4));

    /// <summary>The contention test: 50 bidders on one auction, as fast as they can, for 30 seconds.</summary>
    public static Profile HotBurst { get; } = new("hot-burst", 1, 50, TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.Zero);

    public static Profile? Named(string name) => name switch
    {
        "smoke" => Smoke,
        "realistic" => Realistic,
        "hot-burst" => HotBurst,
        _ => null,
    };
}

/// <summary>The seeded simulator accounts: one seller and up to 50 bidders, all with the simulator password.</summary>
public static class SimulatorAccounts
{
    public const int MaxBidders = 50;

    public const string SellerEmail = "sim-seller@auction.test";

    public static string BidderEmail(int index) => $"sim-{index:000}@auction.test";

    /// <summary>The live marketplace's sellers, seeded beside the short runs' single seller.</summary>
    public const int LiveSellers = 24;

    public static string LiveSellerEmail(int index) => $"sim-seller-{index:00}@auction.test";
}
