using System.Net;
using Auction.IntegrationTests.Database;

namespace Auction.IntegrationTests.Api;

/// <summary>The API serves the Blazor WebAssembly client from the same origin, so the session cookie and the Origin check apply to it.</summary>
public sealed class WebClientHostingTests(MigratedDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/")]
    [InlineData("/sell")]
    [InlineData("/auctions/0195d3a2-7c4e-7000-8000-000000000001")]
    public async Task Every_client_route_loads_the_app_shell_with_a_content_security_policy(string path)
    {
        var api = await db.ApiAsync();

        using var response = await api.Anonymous().GetAsync(new Uri(path, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("_framework/blazor.webassembly", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", policy, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval'", policy.Replace("'wasm-unsafe-eval'", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task The_client_framework_is_served()
    {
        var api = await db.ApiAsync();

        using var response = await api.Anonymous().GetAsync(new Uri("/_framework/blazor.webassembly.js", UriKind.Relative), Ct);
        using var styles = await api.Anonymous().GetAsync(new Uri("/app.css", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, styles.StatusCode);
        Assert.Contains(".lot-cover", await styles.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_api_path_is_a_problem_not_the_app_shell()
    {
        var api = await db.ApiAsync();

        using var response = await api.Anonymous().GetAsync(new Uri("/api/v1/no-such-thing", UriKind.Relative), Ct);

        await response.AssertProblemAsync(HttpStatusCode.NotFound);
    }
}
