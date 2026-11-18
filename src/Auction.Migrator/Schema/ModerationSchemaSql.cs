namespace Auction.Migrator.Schema;

/// <summary>
/// The API disables and enables user accounts itself (ADM-01): it may set only <c>disabled_at</c>, and it
/// audits each change in the same transaction.
/// </summary>
internal static class ModerationSchemaSql
{
    public const string Grants = """
        GRANT UPDATE (disabled_at) ON auction.users TO api;
        GRANT INSERT ON auction.admin_audit TO api;
        """;

    public const string RevokeGrants = """
        REVOKE INSERT ON auction.admin_audit FROM api;
        REVOKE UPDATE (disabled_at) ON auction.users FROM api;
        """;
}
