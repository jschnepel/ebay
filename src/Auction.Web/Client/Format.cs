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
