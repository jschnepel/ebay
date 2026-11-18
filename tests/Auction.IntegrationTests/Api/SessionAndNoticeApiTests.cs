using System.Net;
using System.Net.Http.Json;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

/// <summary>One active session per account (AUTH-02) and each user's own notifications (NOT-01), over HTTP.</summary>
public sealed class SessionAndNoticeApiTests(MigratedDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signing_in_elsewhere_ends_the_earlier_session_and_leaves_other_accounts_alone()
    {
        var api = await db.ApiAsync();
        var first = await api.SignUpAsync();
        var other = await api.SignUpAsync();

        var (second, signIn) = await api.SignInAsync(first.Email);

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await first.Http.StatusOfGetAsync("/api/v1/account/me"));
        Assert.Equal(HttpStatusCode.OK, await second.StatusOfGetAsync("/api/v1/account/me")); // control
        Assert.Equal(HttpStatusCode.OK, await other.Http.StatusOfGetAsync("/api/v1/account/me")); // control
    }

    [Fact]
    public async Task Signing_out_revokes_the_session_on_the_server_so_a_copied_cookie_stops_working()
    {
        var api = await db.ApiAsync();
        var user = await api.SignUpAsync();
        var cookie = await CookieOfAsync(api, user.Email);
        var replay = api.Anonymous(handleCookies: false);

        Assert.Equal(HttpStatusCode.OK, await WithCookieAsync(replay, HttpMethod.Get, "/api/v1/account/me", cookie)); // control
        Assert.Equal(HttpStatusCode.NoContent, await WithCookieAsync(replay, HttpMethod.Post, "/api/v1/account/sign-out", cookie));

        Assert.Equal(HttpStatusCode.Unauthorized, await WithCookieAsync(replay, HttpMethod.Get, "/api/v1/account/me", cookie));
    }

    [Fact]
    public async Task Only_the_outbid_bidder_is_notified_and_reading_it_clears_the_unread_count()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var first = await api.SignUpAsync();
        var second = await api.SignUpAsync();
        var id = await seller.PublishedAsync(ApiCalls.Terms(bidEntry: "fixed_increment", floorType: "minimum_bid", reserveCents: null));
        using (var bid = await first.Http.BidAsync(id, 1000))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        using (var outbid = await second.Http.BidAsync(id, 1100))
        {
            Assert.Equal(HttpStatusCode.Created, outbid.StatusCode);
        }

        var notices = await first.Http.GetJsonAsync("/api/v1/me/notifications");
        var notice = Assert.Single(notices.GetProperty("items").EnumerateArray());
        Assert.Equal(("outbid", id, 1L), (notice.GetProperty("kind").GetString(), notice.GetProperty("auctionId").GetGuid(), notices.GetProperty("unread").GetInt64()));
        Assert.Empty((await second.Http.GetJsonAsync("/api/v1/me/notifications")).GetProperty("items").EnumerateArray());
        Assert.Empty((await seller.Http.GetJsonAsync("/api/v1/me/notifications")).GetProperty("items").EnumerateArray());

        var noticeId = notice.GetProperty("id").GetInt64();
        using (var foreign = await second.Http.PostCommandAsync($"/api/v1/me/notifications/{noticeId}/read"))
        {
            await foreign.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        }

        using (var read = await first.Http.PostCommandAsync($"/api/v1/me/notifications/{noticeId}/read"))
        {
            Assert.Equal(HttpStatusCode.NoContent, read.StatusCode);
        }

        Assert.Equal(0, (await first.Http.GetJsonAsync("/api/v1/me/notifications")).GetProperty("unread").GetInt64());
    }

    [Fact]
    public async Task Sign_ups_sign_ins_and_writes_over_their_limits_get_429_while_others_continue()
    {
        await using var limited = await db.ApiWithAsync(new Dictionary<string, string>
        {
            ["Api:RateLimits:SignUpPerAddressPerHour"] = "2",
            ["Api:RateLimits:WritesPerUserPerMinute"] = "3",
            ["Api:RateLimits:LockoutFailures"] = "3",
        });
        var seller = await limited.SignUpAsync();
        var bystander = await limited.SignUpAsync();
        var target = await db.InsertPasswordAccountAsync("seeded", "right-password");

        using var thirdSignUp = await limited.Anonymous().PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName = "Late", email = $"{Guid.NewGuid():N}@example.test" }, Ct);
        await thirdSignUp.AssertProblemAsync(HttpStatusCode.TooManyRequests, "rate_limited");
        Assert.True(thirdSignUp.Headers.RetryAfter is not null);

        var writes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var draft = await seller.Http.PostCommandAsync("/api/v1/auctions", ApiCalls.Terms());
            writes.Add(draft.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.Created, HttpStatusCode.TooManyRequests], writes);
        using (var other = await bystander.Http.PostCommandAsync("/api/v1/auctions", ApiCalls.Terms()))
        {
            Assert.Equal(HttpStatusCode.Created, other.StatusCode); // control: limits are per user
        }

        for (var i = 0; i < 3; i++)
        {
            var (_, wrong) = await limited.SignInAsync(target, "wrong-password");
            await wrong.AssertProblemAsync(HttpStatusCode.Unauthorized, "sign_in_failed");
        }

        var (_, locked) = await limited.SignInAsync(target, "right-password");
        await locked.AssertProblemAsync(HttpStatusCode.TooManyRequests, "too_many_attempts");
    }

    // The session cookie as the server set it, to replay from a client that doesn't manage cookies.
    private static async Task<string> CookieOfAsync(ApiHost api, string email)
    {
        var client = api.Anonymous(handleCookies: false);
        using var response = await client.PostAsJsonAsync("/api/v1/account/sign-in", new { email }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("auction_session=", StringComparison.Ordinal)).Split(';')[0];
    }

    private static async Task<HttpStatusCode> WithCookieAsync(HttpClient client, HttpMethod method, string url, string cookie)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", cookie);
        using var response = await client.SendAsync(request, Ct);
        return response.StatusCode;
    }
}
