using Auction.Web.Client;

namespace Auction.UnitTests;

/// <summary>Live mode adds what's new in its place, never shows an event twice, and fetches every page of a busy moment.</summary>
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

    [Fact]
    public void Nothing_new_hands_back_the_same_list_without_rebuilding_it()
    {
        IReadOnlyList<LogEvent> shown = [At("b", 20), At("a", 10)];

        Assert.Same(shown, LiveEvents.Merge(shown, [At("b", 20)]));
    }

    [Fact]
    public void The_list_keeps_only_its_newest_events_so_a_long_watch_doesnt_grow_without_end()
    {
        IReadOnlyList<LogEvent> shown = [At("c", 30), At("b", 20), At("a", 10)];

        var merged = LiveEvents.Merge(shown, [At("e", 50), At("d", 40)], keep: 4);

        Assert.Equal(["e", "d", "c", "b"], merged.Select(e => e.Key));
    }

    [Fact]
    public void An_event_that_committed_late_goes_in_its_place_by_time_not_on_top()
    {
        // Stamped before the newest shown event, but only visible once its transaction committed.
        IReadOnlyList<LogEvent> shown = [At("c", 30), At("a", 10)];
        IReadOnlyList<LogEvent> newer = [At("d", 40), At("b", 20)];

        Assert.Equal(["d", "c", "b", "a"], LiveEvents.Merge(shown, newer).Select(e => e.Key));
    }

    [Fact]
    public async Task Collecting_follows_the_cursor_until_the_last_page()
    {
        var pages = new Dictionary<string, Page<LogEvent>>
        {
            [""] = new([At("f", 59), At("e", 50)], "2"),
            ["2"] = new([At("d", 40), At("c", 30)], "3"),
            ["3"] = new([At("b", 20)], null),
        };

        var (events, fetch) = await LiveEvents.CollectAsync(cursor => Task.FromResult<Page<LogEvent>?>(pages[cursor ?? ""]), maxPages: 5);

        Assert.Equal(["f", "e", "d", "c", "b"], events.Select(e => e.Key));
        Assert.Equal(LiveFetch.Complete, fetch);
    }

    [Fact]
    public async Task Collecting_stops_at_its_cap_and_says_it_did()
    {
        var calls = 0;
        var (events, fetch) = await LiveEvents.CollectAsync(
            _ => { calls++; return Task.FromResult<Page<LogEvent>?>(new([At($"k{calls}", 59 - calls)], "more")); },
            maxPages: 3);

        Assert.Equal(3, calls);
        Assert.Equal(3, events.Count);
        Assert.Equal(LiveFetch.More, fetch);
    }

    [Fact]
    public async Task A_failed_page_is_a_failure_not_more_events_than_fit()
    {
        var (events, fetch) = await LiveEvents.CollectAsync(_ => Task.FromResult<Page<LogEvent>?>(null), maxPages: 3);

        Assert.Empty(events);
        Assert.Equal(LiveFetch.Failed, fetch);
    }
}
