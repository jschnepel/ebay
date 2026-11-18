namespace Auction.Domain;

/// <summary>A field that failed validation, for field-level Problem Details.</summary>
public sealed record FieldError(string Field, string Code);

/// <summary>Publish-time listing validation.</summary>
public static class ListingValidator
{
    public static IReadOnlyList<FieldError> ValidateForPublish(
        AuctionTerms terms, DateTimeOffset now, AuctionLimits limits)
    {
        throw new NotImplementedException();
    }
}
