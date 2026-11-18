namespace Auction.IntegrationTests.Database;

/// <summary>
/// Each runtime role can do exactly what it is granted. Statements use <c>WHERE false</c> or
/// <c>SELECT … WHERE false</c>, so PostgreSQL checks the privilege without touching any row.
/// </summary>
public sealed class RoleGrantTests(MigratedDatabase db)
{
    private const string InsufficientPrivilege = "42501";

    private const string InsertBid =
        "INSERT INTO bids (auction_id, seq, bidder_id, source, accepted_at, request_id) " +
        "SELECT gen_random_uuid(), 1, gen_random_uuid(), 'bidder', now(), gen_random_uuid() WHERE false";

    public static TheoryData<string, string> Allowed => new()
    {
        { MigratedDatabase.Engine, "SELECT 1 FROM auctions FOR UPDATE" },
        { MigratedDatabase.Engine, "INSERT INTO auctions (id, seller_id) SELECT gen_random_uuid(), gen_random_uuid() WHERE false" },
        { MigratedDatabase.Engine, "UPDATE auctions SET version = version + 1 WHERE false" },
        { MigratedDatabase.Engine, "DELETE FROM auctions WHERE false" },
        { MigratedDatabase.Engine, InsertBid },
        { MigratedDatabase.Engine, "SELECT 1 FROM bids" },
        { MigratedDatabase.Engine, "SELECT 1 FROM proxy_maxima" },
        { MigratedDatabase.Engine, "UPDATE proxy_maxima SET maximum_cents = maximum_cents WHERE false" },
        { MigratedDatabase.Engine, "SELECT 1 FROM idempotency_records" },
        { MigratedDatabase.Engine, "DELETE FROM idempotency_records WHERE false" },
        { MigratedDatabase.Engine, "INSERT INTO outbox_events (auction_id, auction_version, type, payload) SELECT gen_random_uuid(), 1, 't', '{}' WHERE false" },
        { MigratedDatabase.Engine, "INSERT INTO notifications (user_id, kind) SELECT gen_random_uuid(), 'outbid' WHERE false" },
        { MigratedDatabase.Engine, "INSERT INTO admin_audit (admin_id, action, target_type, target_id, reason) SELECT gen_random_uuid(), 'suspend', 'auction', gen_random_uuid(), 'r' WHERE false" },
        { MigratedDatabase.Engine, "UPDATE user_limits SET open_drafts = open_drafts WHERE false" },
        { MigratedDatabase.Api, "SELECT 1 FROM auctions" },
        { MigratedDatabase.Api, "SELECT 1 FROM bids" },
        { MigratedDatabase.Api, "SELECT 1 FROM admin_audit" },
        { MigratedDatabase.Api, "SELECT 1 FROM outbox_events" },
        { MigratedDatabase.Api, "UPDATE outbox_events SET published_at = now() WHERE false" },
        { MigratedDatabase.Api, "SELECT 1 FROM notifications" },
        { MigratedDatabase.Api, "UPDATE notifications SET read_at = now() WHERE false" },
        { MigratedDatabase.Verifier, "SELECT 1 FROM auctions" },
        { MigratedDatabase.Verifier, "SELECT 1 FROM bids" },
        { MigratedDatabase.Verifier, "SELECT 1 FROM proxy_maxima" },
        { MigratedDatabase.Verifier, "SELECT 1 FROM idempotency_records" },
        { MigratedDatabase.Verifier, "SELECT 1 FROM outbox_events" },
        { MigratedDatabase.Verifier, "SELECT 1 FROM notifications" },
        { MigratedDatabase.Verifier, "SELECT 1 FROM admin_audit" },
        { MigratedDatabase.Verifier, "SELECT 1 FROM user_limits" },
    };

    public static TheoryData<string, string> Refused => new()
    {
        // The API reads auction state but never writes it, and never sees private maxima.
        { MigratedDatabase.Api, InsertBid },
        { MigratedDatabase.Api, "INSERT INTO auctions (id, seller_id) SELECT gen_random_uuid(), gen_random_uuid() WHERE false" },
        { MigratedDatabase.Api, "UPDATE auctions SET version = version + 1 WHERE false" },
        { MigratedDatabase.Api, "DELETE FROM auctions WHERE false" },
        { MigratedDatabase.Api, "SELECT 1 FROM proxy_maxima" },
        { MigratedDatabase.Api, "SELECT 1 FROM idempotency_records" },
        { MigratedDatabase.Api, "SELECT 1 FROM user_limits" },
        { MigratedDatabase.Api, "UPDATE outbox_events SET payload = '{}' WHERE false" },
        { MigratedDatabase.Api, "UPDATE notifications SET kind = 'x' WHERE false" },
        { MigratedDatabase.Api, "INSERT INTO notifications (user_id, kind) SELECT gen_random_uuid(), 'outbid' WHERE false" },
        { MigratedDatabase.Api, "UPDATE bids SET amount_cents = amount_cents WHERE false" },
        { MigratedDatabase.Api, "DELETE FROM bids WHERE false" },
        { MigratedDatabase.Api, "TRUNCATE bids" },
        { MigratedDatabase.Api, "TRUNCATE auctions CASCADE" },
        { MigratedDatabase.Api, "INSERT INTO proxy_maxima (auction_id, bidder_id, maximum_cents, sequence) SELECT gen_random_uuid(), gen_random_uuid(), 1, 1 WHERE false" },
        { MigratedDatabase.Api, "UPDATE proxy_maxima SET maximum_cents = 1 WHERE false" },
        { MigratedDatabase.Api, "INSERT INTO outbox_events (auction_id, auction_version, type, payload) SELECT gen_random_uuid(), 1, 'x', '{}' WHERE false" },
        { MigratedDatabase.Api, "DELETE FROM outbox_events WHERE false" },

        // The verifier only reads.
        { MigratedDatabase.Verifier, "UPDATE auctions SET version = version + 1 WHERE false" },
        { MigratedDatabase.Verifier, InsertBid },
        { MigratedDatabase.Verifier, "DELETE FROM notifications WHERE false" },
        { MigratedDatabase.Verifier, "UPDATE outbox_events SET published_at = now() WHERE false" },
        { MigratedDatabase.Verifier, "TRUNCATE bids" },
        { MigratedDatabase.Verifier, "TRUNCATE auctions CASCADE" },
        { MigratedDatabase.Verifier, "DELETE FROM idempotency_records WHERE false" },

        // The engine writes rows but can't change the schema, rewrite history or read the relay's queue.
        { MigratedDatabase.Engine, "ALTER TABLE auctions ADD COLUMN extra integer" },
        { MigratedDatabase.Engine, "CREATE TABLE auction.extra (id integer)" },
        { MigratedDatabase.Engine, "DROP TABLE bids" },
        { MigratedDatabase.Engine, "TRUNCATE bids" },
        { MigratedDatabase.Engine, "TRUNCATE auctions CASCADE" },
        { MigratedDatabase.Engine, "UPDATE idempotency_records SET outcome = outcome WHERE false" },
        { MigratedDatabase.Engine, "UPDATE bids SET amount_cents = amount_cents WHERE false" },
        { MigratedDatabase.Engine, "DELETE FROM bids WHERE false" },
        { MigratedDatabase.Engine, "DELETE FROM proxy_maxima WHERE false" },
        { MigratedDatabase.Engine, "SELECT 1 FROM outbox_events" },
        { MigratedDatabase.Engine, "UPDATE notifications SET read_at = now() WHERE false" },

        // No runtime role can create objects anywhere.
        { MigratedDatabase.Engine, "CREATE TABLE public.extra (id integer)" },
        { MigratedDatabase.Api, "CREATE TABLE public.extra (id integer)" },
        { MigratedDatabase.Verifier, "CREATE TEMPORARY TABLE extra (id integer)" },
    };

    [Theory]
    [MemberData(nameof(Allowed))]
    public async Task Role_can_use_its_grants(string role, string sql)
    {
        var error = await Record.ExceptionAsync(() => db.ExecuteAsync(role, sql));

        Assert.Null(error);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Role_is_refused_outside_its_grants(string role, string sql)
    {
        var error = await db.RefusedAsync(role, sql);

        Assert.Equal(InsufficientPrivilege, error.SqlState);
    }
}
