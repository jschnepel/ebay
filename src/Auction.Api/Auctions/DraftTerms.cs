using Auction.Contracts.V1;
using Google.Protobuf.WellKnownTypes;

namespace Auction.Api.Auctions;

/// <summary>A seller's terms as sent by the browser. Amounts are US cents; times are ISO 8601.</summary>
public sealed class DraftTermsRequest
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public long? OpeningPriceCents { get; init; }

    /// <summary>Defaults to 100 cents ($1.00).</summary>
    public long? IncrementCents { get; init; }

    /// <summary>reserve (default), minimum_bid or absolute.</summary>
    public string? FloorType { get; init; }

    /// <summary>Required for a reserve auction.</summary>
    public long? ReserveCents { get; init; }

    /// <summary>proxy (default) or fixed_increment.</summary>
    public string? BidEntry { get; init; }

    public DateTimeOffset? StartsAt { get; init; }

    public DateTimeOffset? EndsAt { get; init; }
}

/// <summary>
/// Turns the browser's terms into the engine's. A draft needs every term; the engine refuses values it
/// can't store and checks the listing rules at publish.
/// </summary>
internal static class DraftTerms
{
    private const long DefaultIncrementCents = 100;

    private static readonly Dictionary<string, FloorType> FloorTypes = new(StringComparer.Ordinal)
    {
        ["reserve"] = FloorType.Reserve,
        ["minimum_bid"] = FloorType.MinimumBid,
        ["absolute"] = FloorType.Absolute,
    };

    private static readonly Dictionary<string, BidEntry> BidEntries = new(StringComparer.Ordinal)
    {
        ["proxy"] = BidEntry.Proxy,
        ["fixed_increment"] = BidEntry.FixedIncrement,
    };

    // The engine names fields by the domain's terms; the API reports them by the request's names.
    private static readonly Dictionary<string, string> RequestFieldNames = new(StringComparer.Ordinal)
    {
        ["openingPrice"] = "openingPriceCents",
        ["increment"] = "incrementCents",
        ["reserve"] = "reserveCents",
    };

    public static string RequestField(string engineField) => RequestFieldNames.GetValueOrDefault(engineField, engineField);

    public static bool TryMap(DraftTermsRequest body, out AuctionTerms terms, out Dictionary<string, string[]> errors)
    {
        errors = [];
        Require(errors, "title", !string.IsNullOrWhiteSpace(body.Title));
        Require(errors, "openingPriceCents", body.OpeningPriceCents is not null);
        Require(errors, "startsAt", body.StartsAt is not null);
        Require(errors, "endsAt", body.EndsAt is not null);
        var floorType = Choice(errors, "floorType", body.FloorType ?? "reserve", FloorTypes);
        var bidEntry = Choice(errors, "bidEntry", body.BidEntry ?? "proxy", BidEntries);

        terms = new AuctionTerms
        {
            Title = body.Title?.Trim() ?? "",
            Description = body.Description ?? "",
            OpeningPriceCents = body.OpeningPriceCents ?? 0,
            IncrementCents = body.IncrementCents ?? DefaultIncrementCents,
            FloorType = floorType,
            BidEntry = bidEntry,
            StartsAt = body.StartsAt is { } start ? Timestamp.FromDateTimeOffset(start) : null,
            EndsAt = body.EndsAt is { } end ? Timestamp.FromDateTimeOffset(end) : null,
        };
        if (body.ReserveCents is { } reserve)
        {
            terms.ReserveCents = reserve;
        }

        return errors.Count == 0;
    }

    private static void Require(Dictionary<string, string[]> errors, string field, bool present)
    {
        if (!present)
        {
            errors[field] = ["required"];
        }
    }

    private static T Choice<T>(Dictionary<string, string[]> errors, string field, string value, Dictionary<string, T> choices)
        where T : struct
    {
        if (choices.TryGetValue(value, out var choice))
        {
            return choice;
        }

        errors[field] = ["invalid"];
        return default;
    }
}
