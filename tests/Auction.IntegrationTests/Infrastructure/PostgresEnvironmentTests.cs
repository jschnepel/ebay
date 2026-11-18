using Npgsql;
using Testcontainers.PostgreSql;

namespace Auction.IntegrationTests.Infrastructure;

/// <summary>
/// Environment check: SQL guarantees in this project are tested against a real
/// PostgreSQL 18 server (never an in-memory provider). The database clock is the authoritative
/// deadline clock, so these checks pin down the server version and its time semantics.
/// </summary>
public sealed class PostgresEnvironmentTests : IAsyncLifetime
{
    // Same digest-pinned image as deploy/compose.yaml.
    private const string PostgresImage =
        "postgres:18@sha256:2ea8630df176ad090ec92136dee850397c5af8781e4bcf2e4162593691c61bbc";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(PostgresImage).Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Server_is_postgres_18()
    {
        await using var connection = await OpenConnectionAsync();

        Assert.Equal(18, connection.PostgreSqlVersion.Major);
    }

    [Fact]
    public async Task Database_clock_returns_utc_timestamptz()
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);

        var now = (DateTime)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(DateTimeKind.Utc, now.Kind);
        Assert.InRange(now, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }
}
