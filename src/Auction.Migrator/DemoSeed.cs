using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Auction.Migrator;

/// <summary>The seeded accounts' passwords, from the environment; never stored anywhere else.</summary>
public sealed record DemoSeedOptions(string AdminPassword, string DemoPassword)
{
    public const string AdminEmail = "admin@auction.test";
    public const string SellerEmail = "seller@auction.test";
    public const string BuyerEmail = "buyer@auction.test";
}

/// <summary>What one seed run added; zero everywhere when the data was already there.</summary>
public sealed record DemoSeedResult(int AccountsAdded, int AuctionsAdded);

/// <summary>
/// Demo data for a fresh database: an admin, a demo seller and a demo buyer (password accounts), and a
/// handful of active auctions from the seller with staggered end times. Runs as the schema owner, like
/// the migrations. Safe to repeat: existing accounts keep their passwords, and auctions are added only
/// while the demo seller has none.
/// </summary>
public static class DemoSeed
{
    private sealed record Item(string Title, string Description, long OpeningCents, long IncrementCents, string FloorType, long? ReserveCents, string BidEntry, TimeSpan EndsIn);

    private static readonly Item[] Items =
    [
        new("Vintage film camera", "A 35 mm rangefinder in working order, with its leather case.", 2000, 100, "reserve", 4500, "proxy", TimeSpan.FromHours(2)),
        new("Mechanical keyboard", "Tenkeyless, tactile switches, barely used.", 3500, 100, "minimum_bid", null, "proxy", TimeSpan.FromHours(6)),
        new("Road bike, 54 cm frame", "Aluminium frame, new tyres, serviced last month.", 15000, 1000, "reserve", 30000, "fixed_increment", TimeSpan.FromDays(1)),
        new("First-edition paperback", "A well-read copy of a 1970s science-fiction classic.", 500, 100, "absolute", null, "proxy", TimeSpan.FromMinutes(30)),
        new("Espresso machine", "Single boiler with a steam wand; descaled and tested.", 6000, 200, "reserve", 12000, "proxy", TimeSpan.FromDays(3)),
        new("Desk lamp", "Adjustable arm, warm LED bulb included.", 800, 100, "absolute", null, "fixed_increment", TimeSpan.FromMinutes(10)),
    ];

    private const string InsertUserSql = """
        INSERT INTO auction.users (id, email, normalized_email, display_name, kind, password_hash, created_at)
        VALUES ($1, $2, upper($2), $3, $4, $5, now())
        ON CONFLICT (normalized_email) DO NOTHING
        """;

    private const string InsertAuctionSql = """
        INSERT INTO auction.auctions (
            id, seller_id, status, title, description, opening_price_cents, increment_cents, floor_type, reserve_cents,
            bid_entry, starts_at, ends_at, accepted_bid_count, version, published_at, created_at, updated_at)
        VALUES ($1, $2, 'active', $3, $4, $5, $6, $7, $8, $9, now() - interval '1 minute', now() + $10, 0, 1, now(), now(), now())
        """;

    public static async Task<DemoSeedResult> RunAsync(string connectionString, DemoSeedOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hasher = new PasswordHasher<object>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var accounts = 0;
        foreach (var (email, name, kind, password) in new[]
        {
            (DemoSeedOptions.AdminEmail, "Admin", "admin", options.AdminPassword),
            (DemoSeedOptions.SellerEmail, "Demo seller", "seeded", options.DemoPassword),
            (DemoSeedOptions.BuyerEmail, "Demo buyer", "seeded", options.DemoPassword),
        })
        {
            accounts += await ExecuteAsync(
                connection, InsertUserSql, cancellationToken, Guid.CreateVersion7(), email, name, kind, hasher.HashPassword(new object(), password));
        }

        var sellerId = (Guid)(await ScalarAsync(connection, "SELECT id FROM auction.users WHERE normalized_email = upper($1)", cancellationToken, DemoSeedOptions.SellerEmail))!;
        var existing = (long)(await ScalarAsync(connection, "SELECT count(*) FROM auction.auctions WHERE seller_id = $1", cancellationToken, sellerId))!;
        var auctions = 0;
        if (existing == 0)
        {
            foreach (var item in Items)
            {
                auctions += await ExecuteAsync(
                    connection, InsertAuctionSql, cancellationToken, Guid.CreateVersion7(), sellerId, item.Title, item.Description,
                    item.OpeningCents, item.IncrementCents, item.FloorType, item.ReserveCents, item.BidEntry, item.EndsIn);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new DemoSeedResult(accounts, auctions);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object?[] args)
    {
        await using var command = Command(connection, sql, args);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params object?[] args)
    {
        await using var command = Command(connection, sql, args);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object?[] args)
    {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var arg in args)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        }

        return command;
    }
}
