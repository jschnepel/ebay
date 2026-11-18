namespace Auction.Migrator.Schema;

/// <summary>Grants for the accounts table: only the API signs users up and in; no other role reads emails.</summary>
internal static class AccountsSchemaSql
{
    public const string Grants = "GRANT SELECT, INSERT ON auction.users TO api;";
}
