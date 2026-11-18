namespace Auction.Migrator.Schema;

/// <summary>
/// Image storage rules the database enforces (UI-05, SEC-05). The API writes blobs and quota rows; only the
/// engine links images to auctions, under the auction lock. Once an auction is published its images are
/// frozen with its other terms: a trigger refuses any change to its links, except as the schema owner,
/// which the seed and reset-demo use. Two links on one auction never share a position (checked at commit,
/// so a reorder can swap them).
/// </summary>
internal static class ImageSchemaSql
{
    public const string Rules = """
        ALTER TABLE auction.auction_images
            ADD CONSTRAINT uq_auction_images_position UNIQUE (auction_id, position) DEFERRABLE INITIALLY DEFERRED;

        CREATE FUNCTION auction.auction_images_frozen_after_publish() RETURNS trigger
        LANGUAGE plpgsql AS $fn$
        DECLARE
            target uuid := CASE WHEN TG_OP = 'DELETE' THEN OLD.auction_id ELSE NEW.auction_id END;
            current_status text;
        BEGIN
            SELECT status INTO current_status FROM auction.auctions WHERE id = target;
            -- A cascade from deleting the draft itself finds no row: that's allowed.
            IF current_status IS NOT NULL AND current_status <> 'draft' AND current_user <> 'migrator' THEN
                RAISE EXCEPTION 'images are frozen once the auction is published'
                    USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'auction_images_frozen';
            END IF;
            RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
        END;
        $fn$;

        CREATE TRIGGER auction_images_frozen_after_publish
            BEFORE INSERT OR UPDATE OR DELETE ON auction.auction_images
            FOR EACH ROW EXECUTE FUNCTION auction.auction_images_frozen_after_publish();

        GRANT SELECT, INSERT, DELETE ON auction.image_blobs TO api;
        GRANT SELECT ON auction.auction_images TO api;
        GRANT SELECT, INSERT, UPDATE ON auction.upload_quotas TO api;
        GRANT SELECT, UPDATE ON auction.global_quota TO api;
        GRANT SELECT ON auction.image_blobs TO engine;
        GRANT SELECT, INSERT, UPDATE, DELETE ON auction.auction_images TO engine;
        GRANT SELECT ON auction.image_blobs, auction.auction_images, auction.upload_quotas, auction.global_quota TO verifier;
        """;

    public const string DropRules = """
        REVOKE ALL ON auction.image_blobs, auction.auction_images, auction.upload_quotas, auction.global_quota FROM api, engine, verifier;
        DROP TRIGGER IF EXISTS auction_images_frozen_after_publish ON auction.auction_images;
        DROP FUNCTION IF EXISTS auction.auction_images_frozen_after_publish();
        ALTER TABLE auction.auction_images DROP CONSTRAINT IF EXISTS uq_auction_images_position;
        """;
}
