using Auction.Domain;

namespace Auction.UnitTests;

/// <summary>USD integer cents, documented limits, checked arithmetic.</summary>
public sealed class MoneyTests
{
    private const long Cap = 1_000_000_000;

    [Theory]
    [InlineData("12", 1200)]
    [InlineData("12.5", 1250)]
    [InlineData("12.50", 1250)]
    [InlineData("0.01", 1)]
    [InlineData("12.500", 1250)]
    [InlineData("10000000", Cap)]
    public void Valid_amounts_parse_to_exact_cents(string text, long cents)
    {
        var error = Money.TryParseDollars(text, Cap, out var money);

        Assert.Equal(MoneyError.None, error);
        Assert.Equal(cents, money.Cents);
    }

    [Theory]
    [InlineData("12.345", MoneyError.FractionalCents)]
    [InlineData("0.001", MoneyError.FractionalCents)]
    [InlineData("10000000.01", MoneyError.AboveCap)]
    [InlineData("99999999999999999999", MoneyError.Overflow)]
    [InlineData("92233720368547758.08", MoneyError.Overflow)]
    [InlineData("9223372036854775808", MoneyError.Overflow)]   // whole part alone exceeds a long
    [InlineData("18446744073709551621", MoneyError.Overflow)]  // 2^64 + 5: wraps to 5 if unchecked
    [InlineData("0", MoneyError.NotPositive)]
    [InlineData("-5", MoneyError.NotPositive)]
    [InlineData("", MoneyError.Malformed)]
    [InlineData("1e3", MoneyError.Malformed)]
    [InlineData("1,000", MoneyError.Malformed)]
    [InlineData("12.", MoneyError.Malformed)]
    [InlineData(".5", MoneyError.Malformed)]
    [InlineData(null, MoneyError.Malformed)]
    public void Invalid_amounts_are_rejected_with_their_specific_error(string? text, MoneyError expected)
    {
        var error = Money.TryParseDollars(text, Cap, out var money);

        Assert.Equal(expected, error);
        Assert.Equal(default, money);
    }

    [Fact]
    public void Addition_that_would_overflow_throws_instead_of_wrapping()
    {
        var nearMax = new Money(long.MaxValue - 1);

        Assert.Throws<OverflowException>(() => nearMax.Add(new Money(2)));
        Assert.Equal(long.MaxValue, nearMax.Add(new Money(1)).Cents); // control
    }

    [Fact]
    public void Formats_as_dollars_and_cents_with_invariant_culture()
    {
        Assert.Equal("1234.05", new Money(123_405).ToString());
    }
}
