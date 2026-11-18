using Auction.Engine;
using Auction.IntegrationTests.EngineService;
using Auction.Migrator;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Auction.IntegrationTests.Database.MigratedDatabase))]

namespace Auction.IntegrationTests.Database;

/// <summary>
/// One PostgreSQL 18 server for the database tests, initialised by the real bootstrap script and
/// migrated by the real migrator. Tests create their own rows with fresh ids, so they never collide.
/// </summary>
public sealed class MigratedDatabase : IAsyncLifetime
{
    public const string Migrator = "migrator";
    public const string Engine = "engine";
    public const string Api = "api";
    public const string Verifier = "verifier";

    /// <summary>The network alias other containers (the engine service) use to reach this database.</summary>
    public const string NetworkAlias = "postgres";

    // Same digest-pinned image as deploy/compose.yaml.
    private const string PostgresImage =
        "postgres:18@sha256:2ea8630df176ad090ec92136dee850397c5af8781e4bcf2e4162593691c61bbc";

    private readonly PostgreSqlContainer _postgres;
    private readonly SemaphoreSlim _engineServiceGate = new(1, 1);
    private NpgsqlDataSource? _engineDataSource;
    private EngineServiceContainer? _engineService;

    public MigratedDatabase()
    {
        _postgres = new PostgreSqlBuilder(PostgresImage)
            .WithNetwork(Network)
            .WithNetworkAliases(NetworkAlias)
            .WithDatabase("auction")
            // Room for both 50-bidder races (in-process and over gRPC) and the helpers running beside them.
            .WithCommand("-c", "max_connections=300")
            .WithEnvironment("MIGRATOR_DB_PASSWORD", PasswordFor(Migrator))
            .WithEnvironment("ENGINE_DB_PASSWORD", PasswordFor(Engine))
            .WithEnvironment("API_DB_PASSWORD", PasswordFor(Api))
            .WithEnvironment("VERIFIER_DB_PASSWORD", PasswordFor(Verifier))
            .WithResourceMapping(
                File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "init-roles.sh")),
                FilePath.Of("/docker-entrypoint-initdb.d/10-init-roles.sh"))
            .Build();
    }

    public INetwork Network { get; } = new NetworkBuilder().Build();

    /// <summary>The engine's connection pool, kept below the server's connection limit for the race tests.</summary>
    public NpgsqlDataSource EngineDataSource =>
        _engineDataSource ?? throw new InvalidOperationException("The database is not initialised yet.");

    public async ValueTask InitializeAsync()
    {
        await Network.CreateAsync();
        await _postgres.StartAsync();
        await DatabaseMigrator.MigrateAsync(ConnectionStringFor(Migrator), null, CancellationToken.None);
        _engineDataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(ConnectionStringFor(Engine)) { MaxPoolSize = 30 }.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (_engineService is not null)
        {
            await _engineService.DisposeAsync();
        }

        if (_engineDataSource is not null)
        {
            await _engineDataSource.DisposeAsync();
        }

        await _postgres.DisposeAsync();
        await Network.DisposeAsync();
        _engineServiceGate.Dispose();
    }

    /// <summary>
    /// The engine as a container built from its Dockerfile, on this database's network, started on first
    /// use. Its close worker is off, so it never advances the short auctions other tests rely on.
    /// </summary>
    public async Task<EngineServiceContainer> EngineServiceAsync()
    {
        await _engineServiceGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            return _engineService ??= await EngineServiceContainer.StartAsync(
                Network,
                $"Host={NetworkAlias};Port=5432;Database=auction;Username={Engine};Password={PasswordFor(Engine)};Maximum Pool Size=30");
        }
        finally
        {
            _engineServiceGate.Release();
        }
    }

    public AuctionEngine CreateEngine(EngineOptions? options = null) => new(EngineDataSource, options ?? new EngineOptions());

    /// <summary>The database clock, which decides every deadline.</summary>
    public async Task<DateTimeOffset> NowAsync() =>
        new(await ScalarAsync<DateTime>(Migrator, "SELECT clock_timestamp()"));

    /// <summary>Waits until the database clock reaches <paramref name="moment"/>.</summary>
    public async Task WaitUntilAsync(DateTimeOffset moment)
    {
        while (await NowAsync() < moment)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    public string ConnectionStringFor(string role) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Username = role,
            Password = PasswordFor(role),
        }.ConnectionString;

    public async Task<NpgsqlConnection> OpenAsync(string role)
    {
        var connection = new NpgsqlConnection(ConnectionStringFor(role));
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    /// <summary>Runs one statement as <paramref name="role"/>; arguments bind to $1, $2, ….</summary>
    public async Task<int> ExecuteAsync(string role, string sql, params object?[] args)
    {
        await using var connection = await OpenAsync(role);
        await using var command = Command(connection, sql, args);
        return await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    public async Task<T?> ScalarAsync<T>(string role, string sql, params object?[] args)
    {
        await using var connection = await OpenAsync(role);
        await using var command = Command(connection, sql, args);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Asserts that PostgreSQL refuses the statement, and returns the error for inspection.</summary>
    public async Task<PostgresException> RefusedAsync(string role, string sql, params object?[] args) =>
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(role, sql, args));

    /// <summary>A draft with nothing but its seller, as the Sell form first saves it.</summary>
    public async Task<Guid> InsertDraftAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(Engine, "INSERT INTO auctions (id, seller_id) VALUES ($1, $2)", id, Guid.NewGuid());
        return id;
    }

    /// <summary>A complete proxy auction with a $10.00 opening price and a $20.00 reserve, open for a day.</summary>
    public async Task<Guid> InsertActiveAuctionAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAsync(
            Engine,
            """
            INSERT INTO auctions (id, seller_id, status, title, opening_price_cents, reserve_cents,
                                  starts_at, ends_at, published_at)
            VALUES ($1, $2, 'active', 'Film camera', 1000, 2000, now(), now() + interval '1 day', now())
            """,
            id,
            Guid.NewGuid());
        return id;
    }

    /// <summary>Records an accepted bid as the engine does, and returns its sequence number.</summary>
    public async Task<long> InsertBidAsync(Guid auctionId, long? amountCents)
    {
        var seq = await ScalarAsync<long>(
            Engine, "SELECT coalesce(max(seq), 0) + 1 FROM bids WHERE auction_id = $1", auctionId);
        await ExecuteAsync(
            Engine,
            """
            INSERT INTO bids (auction_id, seq, bidder_id, amount_cents, source, accepted_at, request_id)
            VALUES ($1, $2, $3, $4, 'bidder', now(), $5)
            """,
            auctionId,
            seq,
            Guid.NewGuid(),
            amountCents,
            Guid.NewGuid());
        return seq;
    }

    private static string PasswordFor(string role) => $"{role}-test-only";

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
