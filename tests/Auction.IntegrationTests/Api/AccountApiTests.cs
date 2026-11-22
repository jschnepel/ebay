using System.Net;
using System.Net.Http.Json;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

public sealed class AccountApiTests(MigratedDatabase db)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signing_up_signs_the_visitor_in_and_their_email_signs_them_back_in()
    {
        var api = await db.ApiAsync();
        var user = await api.SignUpAsync("Ada");

        var me = await user.Http.GetJsonAsync("/api/v1/account/me");
        Assert.Equal((user.Id, "Ada", "self_registered"),
            (me.GetProperty("id").GetGuid(), me.GetProperty("displayName").GetString(), me.GetProperty("kind").GetString()));

        using var signedOut = await user.Http.PostAsync(new Uri("/api/v1/account/sign-out", UriKind.Relative), null, _ct);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await user.Http.StatusOfGetAsync("/api/v1/account/me"));

        var (http, response) = await api.SignInAsync(user.Email.ToUpperInvariant());
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(user.Id, (await http.GetJsonAsync("/api/v1/account/me")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task An_email_that_already_exists_in_any_letter_case_gets_409_and_one_account()
    {
        var api = await db.ApiAsync();
        var user = await api.SignUpAsync();

        using var retry = await api.Anonymous().PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName = "Someone else", email = user.Email.ToUpperInvariant() }, _ct);

        await retry.AssertProblemAsync(HttpStatusCode.Conflict, "email_taken");
        Assert.Equal(1, await db.ScalarAsync<long>(
            MigratedDatabase.Migrator, "SELECT count(*) FROM users WHERE normalized_email = upper($1)", user.Email));
    }

    [Fact]
    public async Task Sign_up_with_a_missing_name_or_a_malformed_email_is_refused_with_field_errors()
    {
        var api = await db.ApiAsync();

        using var response = await api.Anonymous().PostAsJsonAsync("/api/v1/account/sign-up", new { displayName = " ", email = "not-an-email" }, _ct);

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        var errors = (await response.JsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("displayName", out _));
        Assert.True(errors.TryGetProperty("email", out _));
    }

    [Fact]
    public async Task A_visitor_who_signs_up_as_admin_gets_the_admin_area_and_signs_back_in_with_the_email()
    {
        var api = await db.ApiAsync();
        var admin = await api.SignUpAsync("Grace", role: "admin");

        Assert.Equal("visitor_admin", (await admin.Http.GetJsonAsync("/api/v1/account/me")).GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.OK, await admin.Http.StatusOfGetAsync("/api/v1/admin/activity"));

        using var signedOut = await admin.Http.PostAsync(new Uri("/api/v1/account/sign-out", UriKind.Relative), null, _ct);
        var (http, response) = await api.SignInAsync(admin.Email);
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, await http.StatusOfGetAsync("/api/v1/admin/activity"));
    }

    [Fact]
    public async Task A_visitor_admin_cannot_bid_sell_or_upload()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var auction = await seller.PublishedAsync();
        var admin = await api.SignUpAsync(role: "admin");

        using var draft = await admin.Http.PostCommandAsync("/api/v1/auctions", ApiCalls.Terms());
        using var bid = await admin.Http.BidAsync(auction, 10_000);
        using var upload = await admin.Http.PostAsJsonAsync("/api/v1/images", TestImages.Upload(TestImages.Png(), "image/png"), _ct);

        Assert.Equal(
            (HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden),
            (draft.StatusCode, bid.StatusCode, upload.StatusCode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("user")]
    public async Task Sign_up_without_a_role_or_as_a_user_creates_a_user(string? role)
    {
        var api = await db.ApiAsync();
        var user = await api.SignUpAsync(role: role);

        Assert.Equal("self_registered", (await user.Http.GetJsonAsync("/api/v1/account/me")).GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.NotFound, await user.Http.StatusOfGetAsync("/api/v1/admin/activity"));
    }

    [Fact]
    public async Task An_unknown_role_is_a_validation_error()
    {
        var api = await db.ApiAsync();

        using var response = await api.Anonymous().PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName = "Eve", email = $"{Guid.NewGuid():N}@example.test", role = "owner" }, _ct);

        await response.AssertProblemAsync(HttpStatusCode.UnprocessableEntity);
        Assert.Equal("invalid", (await response.JsonAsync()).GetProperty("errors").GetProperty("role")[0].GetString());
    }

    [Fact]
    public async Task Outside_demo_mode_no_role_can_be_chosen()
    {
        var hardened = await db.ApiAsync(demoMode: false);

        using var response = await hardened.Anonymous().PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName = "Mallory", email = $"{Guid.NewGuid():N}@example.test", role = "admin" }, _ct);

        await response.AssertProblemAsync(HttpStatusCode.Forbidden, "sign_up_closed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("user")]
    [InlineData("admin")]
    public async Task An_account_made_without_a_password_signs_in_whatever_is_typed_in_the_password_box(string? role)
    {
        var api = await db.ApiAsync();
        var account = await api.SignUpAsync(role: role);

        var (_, typed) = await api.SignInAsync(account.Email, "something typed by mistake");

        Assert.Equal(HttpStatusCode.OK, typed.StatusCode);
    }

    [Theory]
    [InlineData("seeded")]
    [InlineData("simulator")]
    [InlineData("admin")]
    public async Task Password_accounts_never_sign_in_by_email_alone(string kind)
    {
        var api = await db.ApiAsync();
        var email = await db.InsertPasswordAccountAsync(kind, "correct horse battery staple");

        var (_, emailOnly) = await api.SignInAsync(email);
        var (_, wrong) = await api.SignInAsync(email, "wrong password");
        var (http, right) = await api.SignInAsync(email, "correct horse battery staple");

        Assert.Equal(
            (HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.OK),
            (emailOnly.StatusCode, wrong.StatusCode, right.StatusCode));
        Assert.Equal(kind, (await http.GetJsonAsync("/api/v1/account/me")).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task An_unknown_email_is_refused_like_a_wrong_password()
    {
        var api = await db.ApiAsync();

        var (_, response) = await api.SignInAsync($"{Guid.NewGuid():N}@example.test");

        await response.AssertProblemAsync(HttpStatusCode.Unauthorized, "sign_in_failed");
    }

    [Fact]
    public async Task Outside_demo_mode_only_password_accounts_can_sign_in()
    {
        var demo = await db.ApiAsync();
        var visitor = await demo.SignUpAsync();
        var hardened = await db.ApiAsync(demoMode: false);
        var email = await db.InsertPasswordAccountAsync("seeded", "a long generated password");

        using var signUp = await hardened.Anonymous().PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName = "New", email = $"{Guid.NewGuid():N}@example.test" }, _ct);
        var (_, emailOnly) = await hardened.SignInAsync(visitor.Email);
        var (_, password) = await hardened.SignInAsync(email, "a long generated password");

        await signUp.AssertProblemAsync(HttpStatusCode.Forbidden, "sign_up_closed");
        Assert.Equal((HttpStatusCode.Unauthorized, HttpStatusCode.OK), (emailOnly.StatusCode, password.StatusCode));
    }

    [Fact]
    public async Task The_session_cookie_is_HttpOnly_SameSite_Strict_and_Secure_over_https()
    {
        var api = await db.ApiAsync();

        using var response = await api.Anonymous(handleCookies: false).PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName = "Cookie", email = $"{Guid.NewGuid():N}@example.test" }, _ct);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Anonymous_calls_to_signed_in_endpoints_get_401()
    {
        var anonymous = (await db.ApiAsync()).Anonymous();
        var id = Guid.NewGuid();

        Assert.Equal(HttpStatusCode.Unauthorized, await anonymous.StatusOfGetAsync("/api/v1/account/me"));
        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, "/api/v1/account/sign-out", null),
            (HttpMethod.Post, "/api/v1/auctions", ApiCalls.Terms()),
            (HttpMethod.Put, $"/api/v1/auctions/{id}", ApiCalls.Terms()),
            (HttpMethod.Post, $"/api/v1/auctions/{id}/publish", null),
            (HttpMethod.Post, $"/api/v1/auctions/{id}/cancel", null),
            (HttpMethod.Post, $"/api/v1/auctions/{id}/bids", new { amountCents = 1000 }),
        })
        {
            using var response = await anonymous.CommandAsync(method, url, body);
            await response.AssertProblemAsync(HttpStatusCode.Unauthorized);
        }
    }
}
