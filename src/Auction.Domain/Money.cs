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
        money = default;
        var body = text?.Trim() ?? "";
        var negative = body.StartsWith('-');
        if (negative)
        {
            body = body[1..];
        }

        var dot = body.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? body : body[..dot];
        var fraction = dot < 0 ? "" : body[(dot + 1)..];
        if (!IsDigits(whole) || (dot >= 0 && !IsDigits(fraction)))
        {
            return MoneyError.Malformed;
        }

        if (fraction.Length > 2 && fraction[2..].Any(c => c != '0'))
        {
            return MoneyError.FractionalCents;
        }

        if (!TryToCents(whole, fraction.PadRight(2, '0')[..2], out var cents))
        {
            return MoneyError.Overflow;
        }

        if (negative || cents == 0)
        {
            return MoneyError.NotPositive;
        }

        if (cents > capCents)
        {
            return MoneyError.AboveCap;
        }

        money = new Money(cents);
        return MoneyError.None;
    }

    private static bool IsDigits(string text) => text.Length > 0 && text.All(char.IsAsciiDigit);

    private static bool TryToCents(string whole, string twoDigits, out long cents)
    {
        cents = 0;
        try
        {
            foreach (var digit in whole)
            {
                cents = checked((cents * 10) + (digit - '0'));
            }

            cents = checked((cents * 100) + ((twoDigits[0] - '0') * 10) + (twoDigits[1] - '0'));
            return true;
        }
        catch (OverflowException)
        {
            cents = 0;
            return false;
        }
    }
}
