using Auction.Web.Client;

namespace Auction.UnitTests;

/// <summary>RT-02: answers that arrive out of order never take a view back to an older version.</summary>
public sealed class ClientVersionGateTests
{
    private static readonly Guid Id = Guid.CreateVersion7();

    private static AuctionSummary At(long version, long price, Guid? id = null) =>
        new() { Id = id ?? Id, Version = version, CurrentPriceCents = price };

    [Fact]
    public void An_older_answer_arriving_after_a_newer_one_is_ignored()
    {
        var newer = At(5, 2_000);

        Assert.Same(newer, VersionGate.Newest(newer, At(4, 1_500)));
    }

    [Fact]
    public void A_newer_or_equal_answer_replaces_what_is_shown()
    {
        var shown = At(4, 1_500);
        var newer = At(5, 2_000);
        var same = At(4, 1_500);

        Assert.Same(newer, VersionGate.Newest(shown, newer));
        Assert.Same(same, VersionGate.Newest(shown, same));
        Assert.Same(newer, VersionGate.Newest<AuctionSummary>(null, newer));
    }

    [Fact]
    public void A_reloaded_list_keeps_items_already_shown_at_a_newer_version_and_takes_the_rest()
    {
        var other = Guid.CreateVersion7();
        var shown = new[] { At(7, 3_000), At(1, 100, other) };
        var fetched = new[] { At(6, 2_500), At(2, 200, other) };

        var merged = VersionGate.Merge(shown, fetched);

        Assert.Equal([7L, 2L], merged.Select(item => item.Version));
        Assert.Equal([3_000L, 200L], merged.Select(item => item.CurrentPriceCents!.Value));
    }
}
