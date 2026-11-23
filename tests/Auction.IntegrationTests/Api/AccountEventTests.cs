using System.Net;
using System.Net.Http.Json;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

/// <summary>
/// The API records account activity for the admin event log: sign-ups, sign-ins, sign-outs, and sessions ended by a
/// newer sign-in. Each test reads only its own account's rows.
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

    private static async Task SignOutAsync(HttpClient http)
    {
        using var response = await http.PostAsJsonAsync("/api/v1/account/sign-out", new { }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private Task<List<Row>> RowsForEmailAsync(string email) => ReadAsync("WHERE lower(email) = lower($1) OR user_id = (SELECT id FROM users WHERE normalized_email = upper($1))", email);

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
