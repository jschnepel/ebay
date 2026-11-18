namespace Auction.Domain;

/// <summary>A field that failed validation, for field-level Problem Details.</summary>
public sealed record FieldError(string Field, string Code);

/// <summary>Publish-time listing validation.</summary>
public static class ListingValidator
{
    public static IReadOnlyList<FieldError> ValidateForPublish(
        AuctionTerms terms, DateTimeOffset now, AuctionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(limits);

        var errors = new List<FieldError>();
        CheckText(terms, limits, errors);
        CheckAmounts(terms, limits, errors);
        CheckSchedule(terms, now, limits, errors);
        if (terms.ImageCount < 0 || terms.ImageCount > limits.MaxImages)
        {
            errors.Add(new FieldError("images", "count_out_of_range"));
        }

        return errors;
    }

    private static void CheckText(AuctionTerms terms, AuctionLimits limits, List<FieldError> errors)
    {
        var title = terms.Title.Trim().Length;
        if (title < limits.TitleMinLength)
        {
            errors.Add(new FieldError("title", "too_short"));
        }
        else if (title > limits.TitleMaxLength)
        {
            errors.Add(new FieldError("title", "too_long"));
        }

        if (terms.Description.Length > limits.DescriptionMaxLength)
        {
            errors.Add(new FieldError("description", "too_long"));
        }
    }

    private static void CheckAmounts(AuctionTerms terms, AuctionLimits limits, List<FieldError> errors)
    {
        if (!InRange(terms.OpeningPrice, limits))
        {
            errors.Add(new FieldError("openingPrice", "out_of_range"));
        }

        if (!InRange(terms.Increment, limits))
        {
            errors.Add(new FieldError("increment", "out_of_range"));
        }

        var reserveError = (terms.FloorType, terms.Reserve) switch
        {
            (FloorType.Reserve, null) => "required",
            (FloorType.Reserve, { } r) when !InRange(r, limits) => "out_of_range",
            (FloorType.Reserve, { } r) when !r.IsAtLeast(terms.OpeningPrice) => "below_opening_price",
            (FloorType.Reserve, _) => null,
            (_, { }) => "not_allowed",
            _ => null,
        };
        if (reserveError is not null)
        {
            errors.Add(new FieldError("reserve", reserveError));
        }
    }

    private static void CheckSchedule(AuctionTerms terms, DateTimeOffset now, AuctionLimits limits, List<FieldError> errors)
    {
        if (terms.EndsAt <= terms.StartsAt)
        {
            errors.Add(new FieldError("endsAt", "not_after_start"));
            return;
        }

        if (terms.EndsAt <= now)
        {
            errors.Add(new FieldError("endsAt", "in_past"));
            return;
        }

        // A past start means "start now", so the duration that matters runs from the effective start.
        var effectiveStart = terms.StartsAt > now ? terms.StartsAt : now;
        var duration = terms.EndsAt - effectiveStart;
        if (duration < limits.MinDuration)
        {
            errors.Add(new FieldError("duration", "too_short"));
        }
        else if (duration > limits.MaxDuration)
        {
            errors.Add(new FieldError("duration", "too_long"));
        }
    }

    private static bool InRange(Money amount, AuctionLimits limits) =>
        amount.Cents >= 1 && amount.Cents <= limits.AmountCapCents;
}
