namespace Auction.Domain;

/// <summary>
/// Configurable limits for amounts and listings. The defaults are the demo values.
/// </summary>
public sealed record AuctionLimits
{
    public static AuctionLimits Default { get; } = new();

    /// <summary>Largest amount accepted anywhere: 1,000,000,000 cents ($10,000,000.00).</summary>
    public long AmountCapCents { get; init; } = 1_000_000_000;

    public int TitleMinLength { get; init; } = 3;

    public int TitleMaxLength { get; init; } = 120;

    public int DescriptionMaxLength { get; init; } = 4_000;

    public TimeSpan MinDuration { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromDays(14);

    public int MaxImages { get; init; } = 4;
}
