namespace Auction.Migrator.Schema;

/// <summary>
/// The parts of the first schema that EF Core can't model: integrity triggers and the grants for each
/// runtime role. The roles themselves come from deploy/postgres/init-roles.sh.
/// </summary>
internal static class InitialSchemaSql
{
    // Trigger errors carry a constraint name, so callers can tell them apart like CHECK violations.
    public const string Triggers = """
        CREATE FUNCTION auction.refuse_bid_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'accepted bids are append-only'
                USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'bids_append_only';
        END
        $$;

        CREATE TRIGGER bids_append_only
            BEFORE UPDATE OR DELETE ON auction.bids
            FOR EACH ROW EXECUTE FUNCTION auction.refuse_bid_change();

        CREATE FUNCTION auction.refuse_published_delete() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD.status <> 'draft' THEN
                RAISE EXCEPTION 'only a draft can be deleted'
                    USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'auctions_delete_draft_only';
            END IF;
            RETURN OLD;
        END
        $$;

        CREATE TRIGGER auctions_delete_draft_only
            BEFORE DELETE ON auction.auctions
            FOR EACH ROW EXECUTE FUNCTION auction.refuse_published_delete();

        -- Publishing freezes the terms. The draft-to-published update itself may still set them
        -- (for example, a start time in the past becomes "now").
        CREATE FUNCTION auction.refuse_term_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF OLD.status <> 'draft' AND (
                NEW.status = 'draft'
                OR (NEW.seller_id, NEW.title, NEW.description, NEW.opening_price_cents, NEW.increment_cents,
                    NEW.floor_type, NEW.reserve_cents, NEW.bid_entry, NEW.starts_at, NEW.ends_at)
                   IS DISTINCT FROM
                   (OLD.seller_id, OLD.title, OLD.description, OLD.opening_price_cents, OLD.increment_cents,
                    OLD.floor_type, OLD.reserve_cents, OLD.bid_entry, OLD.starts_at, OLD.ends_at)
            ) THEN
                RAISE EXCEPTION 'the terms of a published auction cannot change'
                    USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'auctions_terms_frozen';
            END IF;
            RETURN NEW;
        END
        $$;

        CREATE TRIGGER auctions_terms_frozen
            BEFORE UPDATE ON auction.auctions
            FOR EACH ROW EXECUTE FUNCTION auction.refuse_term_change();
        """;

    // Least privilege per role. The api role can't write auction state or read private maxima; the
    // verifier only reads; no runtime role can change the schema, TRUNCATE or rewrite bid history.
    public const string Grants = """
        GRANT USAGE ON SCHEMA auction TO engine, api, verifier;

        GRANT SELECT, INSERT, UPDATE, DELETE ON auction.auctions TO engine;
        GRANT SELECT, INSERT ON auction.bids TO engine;
        GRANT SELECT, INSERT, UPDATE ON auction.proxy_maxima TO engine;
        GRANT SELECT, INSERT, DELETE ON auction.idempotency_records TO engine;
        GRANT INSERT ON auction.outbox_events, auction.notifications, auction.admin_audit TO engine;
        GRANT SELECT, INSERT, UPDATE ON auction.user_limits TO engine;

        GRANT SELECT ON auction.auctions, auction.bids, auction.admin_audit TO api;
        GRANT SELECT, UPDATE (published_at) ON auction.outbox_events TO api;
        GRANT SELECT, UPDATE (read_at) ON auction.notifications TO api;

        GRANT SELECT ON auction.auctions, auction.proxy_maxima, auction.bids, auction.idempotency_records,
            auction.outbox_events, auction.notifications, auction.admin_audit, auction.user_limits TO verifier;
        """;

    public const string DropTriggers = """
        DROP TRIGGER auctions_terms_frozen ON auction.auctions;
        DROP TRIGGER auctions_delete_draft_only ON auction.auctions;
        DROP TRIGGER bids_append_only ON auction.bids;
        DROP FUNCTION auction.refuse_term_change();
        DROP FUNCTION auction.refuse_published_delete();
        DROP FUNCTION auction.refuse_bid_change();
        """;
}
