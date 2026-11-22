using System.Net;

namespace Auction.StackTests;

/// <summary>What the published port serves: the client with its security headers, readiness, and problems for unknown API paths.</summary>
[Collection(StackRuns.Name)]
public sealed class HttpTests(StackUnderTest stack)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_page_comes_with_its_security_headers_and_the_api_reports_ready()
    {
        using var http = stack.Client();

        using var page = await http.GetAsync(new Uri("/", UriKind.Relative), Ct);
        using var ready = await http.GetAsync(new Uri("/ready", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("frame-ancestors 'none'", string.Join(';', page.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(page.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task An_unknown_api_path_is_a_problem_not_the_app_shell()
    {
        using var http = stack.Client();

        using var response = await http.GetAsync(new Uri("/api/v1/no-such-thing", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_cross_site_request_is_refused()
    {
        var user = await stack.SignUpAsync("Target");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/account/sign-out");
        request.Headers.Add("Origin", "https://attacker.example");

        using var response = await user.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
