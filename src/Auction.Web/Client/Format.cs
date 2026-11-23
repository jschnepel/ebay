using System.Globalization;
using Auction.Domain;

namespace Auction.Web.Client;

/// <summary>Display text for amounts, times and statuses. Amounts stay integer cents until shown.</summary>
public static class Format
{
    private const string LocalInputFormat = "yyyy-MM-dd'T'HH:mm";

    public static string Dollars(long cents) => "$" + new Money(cents).ToString();

    public static string Price(AuctionSummary auction) =>
        auction.CurrentPriceCents is { } price ? Dollars(price) : $"{Dollars(auction.OpeningPriceCents)} (opening)";

    public static string Time(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>An account's kind in the words the admin area uses.</summary>
    public static string Kind(string kind) => kind switch
    {
        "self_registered" => "User",
        "visitor_admin" => "Admin",
        "seeded" => "Demo account",
        "simulator" => "Simulator",
        "admin" => "Built-in admin",
        _ => kind.Replace('_', ' '),
    };

    /// <summary>A time from today as hours, minutes and seconds; an older one with its date.</summary>
    public static string Recent(DateTimeOffset instant)
    {
        var local = instant.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>How a bid was made. A bid beaten at once has no amount: it stays private.</summary>
    public static string How(string source, long? amountCents) => (source, amountCents) switch
    {
        (_, null) => "outbid at once",
        ("proxy", _) => "automatic",
        _ => "bid",
    };

    public static string Status(string status) => status switch
    {
        "active" => "Active",
        "scheduled" => "Starts later",
        "draft" => "Draft",
        "suspended" => "Suspended",
        "closed" => "Closed",
        "cancelled" => "Cancelled",
        _ => status,
    };

    /// <summary>What the price floor means to a bidder. "No reserve" is said in those words.</summary>
    public static string Floor(AuctionSummary auction) => (auction.FloorType, auction.ReserveMet) switch
    {
        ("reserve", true) => "Hidden reserve, met",
        ("reserve", _) => auction.Status == "closed" ? "Hidden reserve, not met" : "Hidden reserve, not met yet",
        ("minimum_bid", _) => "No reserve beyond the opening price",
        _ => "No reserve",
    };

    /// <summary>How a closed auction came out, or null for one that hasn't closed.</summary>
    public static string? Outcome(AuctionSummary auction) => auction switch
    {
        { Status: not "closed" } => null,
        { BidCount: 0 } => "No bids",
        { ReserveMet: false } => "Reserve not met",
        _ => "Sold",
    };

    /// <summary>
    /// The result of an accepted bid. A proxy bid moves the price only as far as it takes to lead (or to the
    /// reserve), and a bid that lands on the price without leading tied with an earlier maximum.
    /// </summary>
    public static string Placed(bool leads, bool proxy, long cents, long? priceCents) => (leads, proxy) switch
    {
        (true, true) => $"Bid placed. You are the highest bidder, up to your maximum of {Dollars(cents)}. The price shows only what it takes to lead.",
        (true, false) => "Bid placed. You are the highest bidder.",
        _ when priceCents == cents => "Bid placed, but an earlier bid with the same maximum leads.",
        _ => "Bid placed, but another bidder's maximum is higher.",
    };

    /// <summary>
    /// Where the bidder stands, for My bids. The leader of a closed auction won it unless its reserve wasn't met.
    /// </summary>
    public static string Standing(BiddingSummary auction) => auction.Status switch
    {
        "closed" => auction.YouLead && auction.ReserveMet != false ? "Won" : "Didn't win",
        "cancelled" => "Cancelled",
        _ => auction.YouLead ? "Leading" : "Outbid",
    };

    /// <summary>The label that agrees with a count: "1 live auction", "2 live auctions".</summary>
    public static string Noun(long count, string one, string many) => count == 1 ? one : many;

    /// <summary>The tag class for a status: green while bidding is open, red when an admin stopped it.</summary>
    public static string StatusTone(string status) => status switch
    {
        "active" => "is-live",
        "suspended" or "cancelled" => "is-off",
        _ => "",
    };

    /// <summary>Exact cents from dollars typed by a person ("12", "12.5", "$12.50"); null if it isn't an amount.</summary>
    public static long? ParseDollars(string? text) =>
        Money.TryParseDollars(text?.Trim().TrimStart('$'), long.MaxValue, out var money) == MoneyError.None ? money.Cents : null;

    /// <summary>Cents as the dollars a person would type back, without the sign.</summary>
    public static string DollarsInput(long? cents) => cents is { } value ? new Money(value).ToString() : "";

    /// <summary>The value of a datetime-local input, in the browser's time zone.</summary>
    public static string LocalInput(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString(LocalInputFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseLocalInput(string? text) =>
        DateTime.TryParseExact(text, LocalInputFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local)
            : null;
}
