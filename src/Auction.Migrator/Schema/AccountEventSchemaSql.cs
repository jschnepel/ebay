namespace Auction.Migrator.Schema;

/// <summary>Account events: written once by the API, read by the API's admin event log and the verifier, never changed.</summary>
internal static class AccountEventSchemaSql
{
    public const string AppendOnly = """
        CREATE FUNCTION auction.refuse_account_event_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'account events are append-only'
                USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'account_events_append_only';
        END
        $$;

        CREATE TRIGGER account_events_append_only
            BEFORE UPDATE OR DELETE ON auction.account_events
            FOR EACH ROW EXECUTE FUNCTION auction.refuse_account_event_change();
        """;

    public const string DropAppendOnly = """
        DROP TRIGGER account_events_append_only ON auction.account_events;
        DROP FUNCTION auction.refuse_account_event_change();
        """;

    // The id comes from its identity and the time from the database clock, so the API can't set either.
    public const string Grants = """
        GRANT SELECT, INSERT (type, user_id, email, detail, session_id) ON auction.account_events TO api;
        GRANT SELECT ON auction.account_events TO verifier;
        """;

    public const string RevokeGrants = """
        REVOKE ALL ON auction.account_events FROM api;
        REVOKE ALL ON auction.account_events FROM verifier;
        """;
}
