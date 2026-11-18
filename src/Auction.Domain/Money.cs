using System.Globalization;

namespace Auction.Domain;

/// <summary>Why an amount was refused. Each case has its own error so the API can name it.</summary>
public enum MoneyError
{
    None,
    Malformed,
    NotPositive,
    FractionalCents,
    AboveCap,
    Overflow,
}

/// <summary>
/// A USD amount in integer cents. Arithmetic is checked: an overflow throws
/// instead of wrapping around.
/// </summary>
public readonly record struct Money(long Cents)
{
    public Money Add(Money other) => new(checked(Cents + other.Cents));

    public bool IsAtLeast(Money other) => Cents >= other.Cents;

    public static Money Min(Money a, Money b) => a.Cents <= b.Cents ? a : b;

    public static Money Max(Money a, Money b) => a.Cents >= b.Cents ? a : b;

    public override string ToString() =>
        (Cents / 100m).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses decimal dollars exactly ("12", "12.5", "12.50"), never through floating point.
    /// </summary>
    public static MoneyError TryParseDollars(string? text, long capCents, out Money money)
    {
        throw new NotImplementedException();
    }
}
