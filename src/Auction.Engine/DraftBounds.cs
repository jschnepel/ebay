using Auction.Domain;

namespace Auction.Engine;

/// <summary>
/// The checks a draft must pass to be saved at all: values the database could not store. The full
/// listing rules apply at publish (<see cref="ListingValidator"/>).
/// </summary>
internal static class DraftBounds
{
    public static IReadOnlyList<FieldError> Check(AuctionTerms terms, AuctionLimits limits)
    {
        var errors = new List<FieldError>();
        AddIf(errors, !InRange(terms.OpeningPrice, limits), "openingPrice", "out_of_range");
        AddIf(errors, !InRange(terms.Increment, limits), "increment", "out_of_range");
        AddIf(errors, terms.Reserve is { } reserve && !InRange(reserve, limits), "reserve", "out_of_range");
        AddIf(errors, terms.Title.Length > limits.TitleMaxLength, "title", "too_long");
        AddIf(errors, terms.Description.Length > limits.DescriptionMaxLength, "description", "too_long");
        AddIf(errors, terms.EndsAt <= terms.StartsAt, "endsAt", "not_after_start");
        return errors;
    }

    private static bool InRange(Money amount, AuctionLimits limits) =>
        amount.Cents >= 1 && amount.Cents <= limits.AmountCapCents;

    private static void AddIf(List<FieldError> errors, bool condition, string field, string code)
    {
        if (condition)
        {
            errors.Add(new FieldError(field, code));
        }
    }
}
