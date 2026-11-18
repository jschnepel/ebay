namespace Auction.Migrator.Schema;

/// <summary>
/// Wakes the outbox relay when the engine commits an event. NOTIFY is delivered only at commit, once per
/// statement, and carries nothing: it's only a wake-up; the relay still reads the table in commit-safe order.
/// </summary>
internal static class OutboxSchemaSql
{
    public const string Channel = "auction_outbox";

    public const string WakeUp = $"""
        CREATE FUNCTION auction.outbox_wake_up() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM pg_notify('{Channel}', '');
            RETURN NULL;
        END;
        $$;

        CREATE TRIGGER outbox_events_wake_up
            AFTER INSERT ON auction.outbox_events
            FOR EACH STATEMENT EXECUTE FUNCTION auction.outbox_wake_up();
        """;

    public const string DropWakeUp = """
        DROP TRIGGER IF EXISTS outbox_events_wake_up ON auction.outbox_events;
        DROP FUNCTION IF EXISTS auction.outbox_wake_up();
        """;
}
