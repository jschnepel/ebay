using System.Globalization;

namespace Auction.Web.Client;

/// <summary>
/// The auction clock as people read it: how long is left in words, whether the end is near, and how much of the
/// auction has run. The server's end time stays authoritative; this only describes it.
/// </summary>
public static class Clock
{
    /// <summary>An active auction this close to its end is shown as ending soon (the directory's "ending soon" filter too).</summary>
    public static readonly TimeSpan SoonWindow = TimeSpan.FromHours(1);

    public static string Text(AuctionSummary auction, DateTimeOffset now) => auction.Status switch
    {
        "active" when auction.EndsAt <= now => "Closing",
        "active" when auction.EndsAt - now < TimeSpan.FromMinutes(1) => "Under a minute left",
        "active" => $"{Span(auction.EndsAt - now)} left",
        "scheduled" => $"Starts in {Span(auction.StartsAt - now)}",
        "closed" => "Ended",
        "cancelled" => "Cancelled",
        "suspended" => "Paused by an admin",
        "draft" => "Not listed yet",
        _ => auction.Status,
    };

    public static bool EndingSoon(AuctionSummary auction, DateTimeOffset now) =>
        auction.Status == "active" && auction.EndsAt - now <= SoonWindow;

    /// <summary>The share of the auction's run that has passed, from 0 before it starts to 1 at its end.</summary>
    public static decimal Elapsed(AuctionSummary auction, DateTimeOffset now) // display-only: progress meter
    {
        var length = (auction.EndsAt - auction.StartsAt).Ticks;
        return length <= 0 ? 1 : Math.Clamp((decimal)(now - auction.StartsAt).Ticks / length, 0, 1); // display-only
    }

    /// <summary>How often a view showing this auction's clock needs to redraw it.</summary>
    public static TimeSpan RedrawEvery(AuctionSummary auction, DateTimeOffset now) =>
        auction.Status == "active" && auction.EndsAt - now < TimeSpan.FromMinutes(2) ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(20);

    // Two units at most, largest first: "3 d 5 h", "5 h 14 min", "42 min". A zero second unit is left out.
    private static string Span(TimeSpan span)
    {
        var minutes = Math.Max(1, (long)span.TotalMinutes);
        var (days, hours, mins) = (minutes / 1440, minutes / 60 % 24, minutes % 60);
        if (days > 0)
        {
            return Join(days, "d", hours, "h");
        }

        return hours > 0 ? Join(hours, "h", mins, "min") : Join(mins, "min", 0, "");
    }

    private static string Join(long first, string firstUnit, long second, string secondUnit) =>
        second == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{first} {firstUnit}")
            : string.Create(CultureInfo.InvariantCulture, $"{first} {firstUnit} {second} {secondUnit}");
}
