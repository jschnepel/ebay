namespace Auction.IntegrationTests.Database;

/// <summary>
/// The account events behind the admin event log are append-only, like accepted bids: the trigger holds even for
/// the table owner, so only the demo reset's TRUNCATE clears them.
/// </summary>
public sealed class AccountEventTableTests(MigratedDatabase db)
{
    [Fact]
    public async Task An_account_event_can_be_neither_changed_nor_removed()
    {
        var id = await db.ScalarAsync<long>(
            MigratedDatabase.Migrator, "INSERT INTO account_events (type, email, detail) VALUES ('sign_in_failed', 'a@example.test', 'unknown_email') RETURNING id");

        var changed = await db.RefusedAsync(MigratedDatabase.Migrator, "UPDATE account_events SET detail = 'wrong_password' WHERE id = $1", id);
        var removed = await db.RefusedAsync(MigratedDatabase.Migrator, "DELETE FROM account_events WHERE id = $1", id);

        Assert.Equal("account_events_append_only", changed.ConstraintName);
        Assert.Equal("account_events_append_only", removed.ConstraintName);
    }

    [Fact]
    public async Task An_account_event_has_a_known_type()
    {
        var error = await db.RefusedAsync(MigratedDatabase.Migrator, "INSERT INTO account_events (type) VALUES ('password_reset')");

        Assert.Equal("ck_account_events_type", error.ConstraintName);
    }

    [Fact]
    public async Task An_account_event_is_stamped_when_it_is_written()
    {
        var recent = await db.ScalarAsync<bool>(
            MigratedDatabase.Migrator, "INSERT INTO account_events (type) VALUES ('sign_up') RETURNING at > clock_timestamp() - interval '1 minute'");

        Assert.True(recent);
    }
}
