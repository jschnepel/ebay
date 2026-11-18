namespace Auction.Migrator.Schema;

/// <summary>Bounds shared by every money column.</summary>
internal static class Amounts
{
    /// <summary>
    /// The hard ceiling for any amount: 1,000,000,000 cents ($10,000,000.00). The configured cap
    /// can only be lower, so this never has to change with configuration.
    /// </summary>
    public const long CeilingCents = 1_000_000_000;

    public static string InRange(string column) => $"{column} BETWEEN 1 AND {CeilingCents}";
}
