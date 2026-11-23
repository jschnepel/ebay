namespace Auction.Web.Client;

/// <summary>An event log row as one sentence: who did what to whom, with the reason or amount where there is one.</summary>
public static class EventText
{
    public static string Summary(LogEvent e)
    {
        var actor = e.Actor?.Name ?? "Someone";
        var target = e.Target?.Name ?? "";
        var reason = Detail(e, "reason");
        return (e.Category, e.Type) switch
        {
            ("account", "sign_up") => $"{actor} created an account",
            ("account", "sign_in") => $"{actor} signed in",
            ("account", "sign_out") => $"{actor} signed out",
            ("account", "sign_in_failed") => $"Failed sign-in for {target}: {Cause(Detail(e, "cause"))}",
            ("account", "session_replaced") => $"A newer sign-in ended a session for {actor}",
            ("account", "session_ended") => $"Disabling {actor} ended a session",
            ("bid", _) => Bid(e, actor, target),
            ("listing", "published") => $"{actor} listed {target}",
            ("listing", "cancelled") => $"{actor} cancelled {target}",
            ("listing", "closed") => $"{target} closed: {Outcome(e)}",
            ("admin", "demo_reset_requested") => $"{actor} asked for a demo reset",
            ("admin", "demo_reset_completed") => "The demo was reset",
            // These record a ready-made sentence as their reason, such as "Started the smoke profile".
            ("admin", "simulator_start" or "simulator_stop" or "live_marketplace_paused" or "live_marketplace_resumed") when reason is { Length: > 0 } =>
                $"{actor} {char.ToLowerInvariant(reason[0])}{reason[1..]}",
            ("admin", _) => $"{actor} {Past(e.Type)} {target}{Quoted(reason)}".Replace("  ", " ", StringComparison.Ordinal),
            _ => $"{actor} {e.Type.Replace('_', ' ')} {target}".Trim(),
        };
    }

    /// <summary>A detail value, or null when the event doesn't carry it.</summary>
    public static string? Detail(LogEvent e, string key) => e.Detail.TryGetValue(key, out var value) ? value : null;

    private static string Bid(LogEvent e, string actor, string target)
    {
        if (!long.TryParse(Detail(e, "amountCents"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var cents))
        {
            return $"{actor} placed a private bid on {target}";
        }

        var automatic = Detail(e, "source") == "proxy" ? " automatically" : "";
        return $"{actor} bid {Format.Dollars(cents)} on {target}{automatic}";
    }

    private static string Outcome(LogEvent e) => Detail(e, "outcome") switch
    {
        "sold" when long.TryParse(Detail(e, "finalPriceCents"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var cents) =>
            $"sold for {Format.Dollars(cents)}",
        "sold" => "sold",
        "reserve_not_met" => "reserve not met",
        "no_bids" => "no bids",
        var other => other?.Replace('_', ' ') ?? "closed",
    };

    private static string Cause(string? cause) => cause switch
    {
        "unknown_email" => "unknown email",
        "wrong_password" => "wrong password",
        "no_password_given" => "no password given",
        "account_disabled" => "account disabled",
        "too_many_attempts" => "too many attempts",
        "email_sign_in_off" => "email sign-in is off",
        _ => cause?.Replace('_', ' ') ?? "unknown cause",
    };

    private static string Past(string action) => action switch
    {
        "disable" => "disabled",
        "enable" => "enabled",
        "suspend" => "suspended",
        "resume" => "resumed",
        "cancel" => "cancelled",
        _ => action.Replace('_', ' '),
    };

    private static string Quoted(string? reason) => reason is { Length: > 0 } ? $": \"{reason}\"" : "";
}
