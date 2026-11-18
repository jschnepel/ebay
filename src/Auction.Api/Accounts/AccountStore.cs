using Auction.Domain;
using Npgsql;

namespace Auction.Api.Accounts;

/// <summary>The account kinds stored in <c>users.kind</c>.</summary>
internal static class AccountKinds
{
    public const string SelfRegistered = "self_registered";
    public const string Admin = "admin";
}

internal sealed record Account(UserId Id, string DisplayName, string Kind, string? PasswordHash, bool Disabled);

/// <summary>Accounts in <c>auction.users</c>, read and created as the api role.</summary>
internal sealed class AccountStore(NpgsqlDataSource dataSource)
{
    private const string FindSql =
        "SELECT id, display_name, kind, password_hash, disabled_at IS NOT NULL FROM auction.users WHERE normalized_email = $1";

    // The unique index on normalized_email decides a race between two sign-ups for one email.
    private const string RegisterSql =
        "INSERT INTO auction.users (id, email, normalized_email, display_name, kind) VALUES ($1, $2, $3, $4, 'self_registered') " +
        "ON CONFLICT (normalized_email) DO NOTHING";

    private const string ActiveSql = "SELECT disabled_at IS NULL FROM auction.users WHERE id = $1";

    // One transaction: the change and its audit row land together or not at all.
    private const string SetDisabledSql = """
        WITH changed AS (
            UPDATE auction.users SET disabled_at = CASE WHEN $2 THEN coalesce(disabled_at, now()) ELSE NULL END
            WHERE id = $1
            RETURNING id)
        INSERT INTO auction.admin_audit (admin_id, action, target_type, target_id, reason, at)
        SELECT $3, CASE WHEN $2 THEN 'disable' ELSE 'enable' END, 'user', id, $4, now() FROM changed
        """;

    /// <summary>Emails are unique ignoring letter case.</summary>
    public static string Normalize(string email) => email.Trim().ToUpperInvariant();

    public async Task<Account?> FindAsync(string email, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(FindSql);
        command.Parameters.Add(new NpgsqlParameter { Value = Normalize(email) });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var passwordHash = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3);
        return new Account(new UserId(reader.GetGuid(0)), reader.GetString(1), reader.GetString(2), passwordHash, reader.GetBoolean(4));
    }

    /// <summary>Whether the account still exists and isn't disabled; checked on every signed-in request.</summary>
    public async Task<bool> IsActiveAsync(UserId id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ActiveSql);
        command.Parameters.Add(new NpgsqlParameter { Value = id.Value });
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>Disables or enables an account and audits it; false when there is no such user.</summary>
    public async Task<bool> SetDisabledAsync(Guid id, bool disabled, UserId admin, string reason, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(SetDisabledSql);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = disabled });
        command.Parameters.Add(new NpgsqlParameter { Value = admin.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = reason });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Creates a self-registered account, or returns null when the email is already taken.</summary>
    public async Task<Account?> RegisterAsync(string email, string displayName, CancellationToken cancellationToken)
    {
        var id = UserId.New();
        await using var command = dataSource.CreateCommand(RegisterSql);
        command.Parameters.Add(new NpgsqlParameter { Value = id.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = email.Trim() });
        command.Parameters.Add(new NpgsqlParameter { Value = Normalize(email) });
        command.Parameters.Add(new NpgsqlParameter { Value = displayName });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1
            ? new Account(id, displayName, AccountKinds.SelfRegistered, null, false)
            : null;
    }
}
