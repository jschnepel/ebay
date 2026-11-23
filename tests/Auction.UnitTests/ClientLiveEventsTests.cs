using Auction.Web.Client;

namespace Auction.UnitTests;

/// <summary>Live mode adds what's new at the top and never shows an event twice.</summary>
public sealed class ClientLiveEventsTests
{
    private static LogEvent At(string key, int second) =>
        new(key, new DateTimeOffset(2026, 11, 23, 6, 0, second, TimeSpan.Zero), "bid", "bid", "info", null, null, new Dictionary<string, string?>());

    [Fact]
    public void Live_merge_adds_only_events_not_already_shown()
    {
        IReadOnlyList<LogEvent> shown = [At("b", 20), At("a", 10)];
        IReadOnlyList<LogEvent> newer = [At("d", 40), At("c", 30), At("b", 20)];

        var merged = LiveEvents.Merge(shown, newer);

        Assert.Equal(["d", "c", "b", "a"], merged.Select(e => e.Key));
    }

    [Fact]
    public void Nothing_new_leaves_the_list_as_it_was()
    {
        IReadOnlyList<LogEvent> shown = [At("b", 20), At("a", 10)];

        Assert.Equal(["b", "a"], LiveEvents.Merge(shown, []).Select(e => e.Key));
    }
}
