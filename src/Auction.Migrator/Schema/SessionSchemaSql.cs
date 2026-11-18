namespace Auction.Migrator.Schema;

/// <summary>Sessions belong to the API alone: it starts them and may set only <c>revoked_at</c>.</summary>
internal static class SessionSchemaSql
{
    public const string Grants = "GRANT SELECT, INSERT, UPDATE (revoked_at) ON auction.sessions TO api;";

    public const string RevokeGrants = "REVOKE ALL ON auction.sessions FROM api;";
}
