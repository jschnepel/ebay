namespace Auction.Migrator.Schema;

/// <summary>
/// Lets the API tell a bidder their own proxy maximum without being able to read the table. The function runs as
/// its owner and returns one bidder's maximum on one auction; the API passes the signed-in account. Listing
/// maxima, or finding who holds one, stays impossible for the api role.
/// </summary>
internal static class OwnMaximumSchemaSql
{
    public const string Create = """
        CREATE FUNCTION auction.own_maximum(auction uuid, bidder uuid) RETURNS bigint
            LANGUAGE sql STABLE SECURITY DEFINER SET search_path = auction, pg_temp
            RETURN (SELECT m.maximum_cents FROM auction.proxy_maxima m WHERE m.auction_id = auction AND m.bidder_id = bidder);

        REVOKE ALL ON FUNCTION auction.own_maximum(uuid, uuid) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION auction.own_maximum(uuid, uuid) TO api;
        """;

    public const string Drop = "DROP FUNCTION auction.own_maximum(uuid, uuid);";
}
