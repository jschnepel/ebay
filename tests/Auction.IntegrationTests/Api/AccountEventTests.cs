using System.Net;
using System.Net.Http.Json;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

/// <summary>
/// The API records account activity for the admin event log: sign-ups, sign-ins, failed sign-ins with their cause,
/// sign-outs, and sessions ended by a newer sign-in or a disable. Each test reads only its own account's rows.
/// </summary>
public sealed class AccountEventTests(MigratedDatabase db)
{
    [Fact]
    public async Task Signing_up_signing_in_and_signing_out_are_each_recorded_with_the_account_and_email()
    {
        var api = await db.ApiAsync();
        var user = await api.SignUpAsync("Recorded user");
        await SignOutAsync(user.Http);
        var (again, response) = await api.SignInAsync(user.Email);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SignOutAsync(again);

        var rows = await RowsForEmailAsync(user.Email);

        Assert.Equal(["sign_up", "sign_out", "sign_in", "sign_out"], rows.Select(r => r.Type));
        Assert.All(rows, r => Assert.Equal(user.Id, r.UserId));
        Assert.All(rows, r => Assert.NotNull(r.SessionId));
        Assert.Equal(rows[2].SessionId, rows[3].SessionId);
    }

    [Theory]
    [InlineData("unknown_email")]
    [InlineData("wrong_password")]
    [InlineData("no_password_given")]
    [InlineData("account_disabled")]
    public async Task A_failed_sign_in_records_why(string cause)
    {
        var api = await db.ApiAsync();
        string email;
        HttpResponseMessage response;
        switch (cause)
        {
            case "unknown_email":
                email = $"{Guid.NewGuid():N}@example.test";
                (_, response) = await api.SignInAsync(email);
                break;
            case "wrong_password":
                email = await db.InsertPasswordAccountAsync("seeded", "the-right-password");
                (_, response) = await api.SignInAsync(email, "a-wrong-password");
                break;
            case "no_password_given":
                email = await db.InsertPasswordAccountAsync("seeded", "the-right-password");
                (_, response) = await api.SignInAsync(email);
                break;
            default:
                var visitor = await api.SignUpAsync("Soon disabled");
                var admin = await api.SignUpAsync("Disabling admin", role: "admin");
                using (var disable = await admin.Http.PostAsJsonAsync($"/api/v1/admin/users/{visitor.Id}/disable", new { reason = "test" }, TestContext.Current.CancellationToken))
                {
                    Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
                }

                email = visitor.Email;
                (_, response) = await api.SignInAsync(email);
                break;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var failed = Assert.Single(await RowsForEmailAsync(email), r => r.Type == "sign_in_failed");
        Assert.Equal(cause, failed.Detail);
        Assert.Equal(cause == "unknown_email", failed.UserId is null);
    }

    [Fact]
    public async Task Too_many_attempts_is_recorded_against_the_account_the_email_belongs_to()
    {
        var api = await db.ApiWithAsync(new Dictionary<string, string> { ["Api:RateLimits:SignInPerAccountPerMinute"] = "1" });
        var user = await api.SignUpAsync("Busy user");

        (_, var first) = await api.SignInAsync(user.Email);
        (_, var second) = await api.SignInAsync(user.Email);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var refused = Assert.Single(await RowsForEmailAsync(user.Email), r => r.Type == "sign_in_failed");
        Assert.Equal(("too_many_attempts", (Guid?)user.Id), (refused.Detail, refused.UserId));
    }

    [Fact]
    public async Task A_sign_in_elsewhere_records_the_session_it_replaced()
    {
        var api = await db.ApiAsync();
        var user = await api.SignUpAsync("Two browsers");
        var first = (await RowsForEmailAsync(user.Email)).Single(r => r.Type == "sign_up").SessionId;

        (_, var response) = await api.SignInAsync(user.Email);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var replaced = Assert.Single(await RowsForEmailAsync(user.Email), r => r.Type == "session_replaced");
        Assert.Equal((first, (Guid?)user.Id), (replaced.SessionId, replaced.UserId));
    }

    [Fact]
    public async Task A_disable_records_each_session_it_ended()
    {
        var api = await db.ApiAsync();
        var visitor = await api.SignUpAsync("Ended twice");
        var admin = await api.SignUpAsync("Ending admin", role: "admin");
        // A second, still-open session: sign-in ends the first, so open the second without signing in again.
        var sessions = await db.ScalarAsync<long>(MigratedDatabase.Migrator, "SELECT count(*) FROM sessions WHERE user_id = $1 AND revoked_at IS NULL", visitor.Id);
        Assert.Equal(1, sessions);
        await db.ExecuteAsync(MigratedDatabase.Migrator, "INSERT INTO sessions (id, user_id) VALUES (gen_random_uuid(), $1)", visitor.Id);

        using (var disable = await admin.Http.PostAsJsonAsync($"/api/v1/admin/users/{visitor.Id}/disable", new { reason = "test" }, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        }

        var ended = (await RowsForUserAsync(visitor.Id)).Where(r => r.Type == "session_ended").ToList();
        Assert.Equal(2, ended.Count);
        Assert.All(ended, r => Assert.Equal("account_disabled", r.Detail));
        Assert.Equal(2, ended.Select(r => r.SessionId).Distinct().Count());
    }

    [Fact]
    public async Task No_password_is_ever_stored()
    {
        var api = await db.ApiAsync();
        var email = await db.InsertPasswordAccountAsync("seeded", "the-right-password");
        (_, var response) = await api.SignInAsync(email, "a-distinctive-wrong-password");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var leaks = await db.ScalarAsync<long>(
            MigratedDatabase.Migrator, "SELECT count(*) FROM account_events WHERE account_events::text LIKE '%distinctive%'");

        Assert.Equal(0, leaks);
    }

    private static async Task SignOutAsync(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync("/api/v1/account/sign-out", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private Task<List<Row>> RowsForEmailAsync(string email) => ReadAsync("WHERE lower(email) = lower($1) OR user_id = (SELECT id FROM users WHERE normalized_email = upper($1))", email);

    private Task<List<Row>> RowsForUserAsync(Guid id) => ReadAsync("WHERE user_id = $1", id);

    private async Task<List<Row>> ReadAsync(string where, object value)
    {
        await using var connection = await db.OpenAsync(MigratedDatabase.Migrator);
        await using var command = new Npgsql.NpgsqlCommand($"SELECT type, user_id, detail, session_id FROM account_events {where} ORDER BY id", connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = value });
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<Row>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var token = TestContext.Current.CancellationToken;
            rows.Add(new Row(
                reader.GetString(0),
                await reader.IsDBNullAsync(1, token) ? null : reader.GetGuid(1),
                await reader.IsDBNullAsync(2, token) ? null : reader.GetString(2),
                await reader.IsDBNullAsync(3, token) ? null : reader.GetGuid(3)));
        }

        return rows;
    }

    private sealed record Row(string Type, Guid? UserId, string? Detail, Guid? SessionId);
}
