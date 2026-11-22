namespace Auction.Migrator.Schema;

/// <summary>Demo resets: the API records an admin's request and reads the history; only the migrator runs them.</summary>
internal static class DemoResetSchemaSql
{
    /// <summary>At most one request waits at a time; a second admin's request is refused until the first is done.</summary>
    public const string OnePending = "CREATE UNIQUE INDEX ux_demo_resets_one_pending ON auction.demo_resets ((true)) WHERE completed_at IS NULL;";

    public const string DropOnePending = "DROP INDEX auction.ux_demo_resets_one_pending;";

    public const string Grants = "GRANT SELECT, INSERT (id, requested_at, requested_by) ON auction.demo_resets TO api;";

    public const string RevokeGrants = "REVOKE ALL ON auction.demo_resets FROM api;";
}
