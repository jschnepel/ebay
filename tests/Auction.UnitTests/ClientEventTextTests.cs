using Auction.Web.Client;

namespace Auction.UnitTests;

/// <summary>Each event log row reads as one sentence naming who did what to whom.</summary>
public sealed class ClientEventTextTests
{
    private static readonly EventParty Ada = new("user", Guid.NewGuid(), "Ada", "ada@example.test");
    private static readonly EventParty Sam = new("user", Guid.NewGuid(), "Sam", "sam@example.test");
    private static readonly EventParty Lamp = new("auction", Guid.NewGuid(), "Desk lamp", null);

    private static LogEvent Event(string category, string type, EventParty? actor, EventParty? target, params (string Key, string? Value)[] detail) =>
        new($"k:{type}", DateTimeOffset.UtcNow, category, type, "info", actor, target, detail.ToDictionary(d => d.Key, d => d.Value));

    public static TheoryData<LogEvent, string> Sentences => new()
    {
        { Event("admin", "disable", Ada, Sam, ("reason", "spam listings")), "Ada disabled Sam: \"spam listings\"" },
        { Event("admin", "enable", Ada, Sam, ("reason", "appeal")), "Ada enabled Sam: \"appeal\"" },
        { Event("admin", "suspend", Ada, Lamp, ("reason", "photos")), "Ada suspended Desk lamp: \"photos\"" },
        { Event("admin", "resume", Ada, Lamp, ("reason", "fixed")), "Ada resumed Desk lamp: \"fixed\"" },
        { Event("admin", "cancel", Ada, Lamp, ("reason", "fake")), "Ada cancelled Desk lamp: \"fake\"" },
        { Event("admin", "live_marketplace_paused", Ada, new("live_marketplace", null, "the live marketplace", null), ("reason", "Paused the live marketplace for up to 30 minutes")), "Ada paused the live marketplace for up to 30 minutes" },
        { Event("admin", "simulator_start", Ada, new("simulator_run", Guid.NewGuid(), "a simulator run", null), ("reason", "Started the smoke profile")), "Ada started the smoke profile" },
        { Event("admin", "demo_reset_requested", new("user", null, "Ada", null), null), "Ada asked for a demo reset" },
        { Event("admin", "demo_reset_completed", null, null), "The demo was reset" },
        { Event("account", "sign_in_failed", null, Sam, ("cause", "wrong_password")), "Failed sign-in for Sam: wrong password" },
        { Event("account", "sign_in_failed", null, new("email", null, "who@example.test", "who@example.test"), ("cause", "unknown_email")), "Failed sign-in for who@example.test: unknown email" },
        { Event("account", "sign_in_failed", null, Sam, ("cause", "account_disabled")), "Failed sign-in for Sam: account disabled" },
        { Event("account", "sign_in_failed", null, Sam, ("cause", "no_password_given")), "Failed sign-in for Sam: no password given" },
        { Event("account", "sign_in_failed", null, Sam, ("cause", "too_many_attempts")), "Failed sign-in for Sam: too many attempts" },
        { Event("account", "sign_in", Ada, null), "Ada signed in" },
        { Event("account", "sign_out", Ada, null), "Ada signed out" },
        { Event("account", "sign_up", Ada, null), "Ada created an account" },
        { Event("account", "session_replaced", Ada, null), "A newer sign-in ended a session for Ada" },
        { Event("account", "session_ended", Ada, null, ("cause", "account_disabled")), "Disabling Ada ended a session" },
        { Event("bid", "bid", Sam, Lamp, ("amountCents", "1250"), ("source", "bidder")), "Sam bid $12.50 on Desk lamp" },
        { Event("bid", "bid", Sam, Lamp, ("amountCents", "1300"), ("source", "proxy")), "Sam bid $13.00 on Desk lamp automatically" },
        { Event("bid", "bid", Sam, Lamp, ("amountCents", null), ("source", "bidder")), "Sam placed a private bid on Desk lamp" },
        { Event("listing", "published", Ada, Lamp), "Ada listed Desk lamp" },
        { Event("listing", "cancelled", Ada, Lamp), "Ada cancelled Desk lamp" },
        { Event("listing", "closed", null, Lamp, ("outcome", "sold"), ("finalPriceCents", "4200")), "Desk lamp closed: sold for $42.00" },
        { Event("listing", "closed", null, Lamp, ("outcome", "reserve_not_met")), "Desk lamp closed: reserve not met" },
        { Event("listing", "closed", null, Lamp, ("outcome", "no_bids")), "Desk lamp closed: no bids" },
    };

    [Theory]
    [MemberData(nameof(Sentences))]
    public void An_event_reads_as_a_sentence(LogEvent e, string expected)
    {
        Assert.Equal(expected, EventText.Summary(e));
    }

    [Fact]
    public void An_event_type_without_its_own_sentence_still_names_who_and_what()
    {
        Assert.Equal("Ada archived Desk lamp: \"old\"", EventText.Summary(Event("admin", "archived", Ada, Lamp, ("reason", "old"))));
    }
}
