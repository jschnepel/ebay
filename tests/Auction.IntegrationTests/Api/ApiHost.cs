using System.Net.Http.Json;
using System.Text.Json;
using Auction.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Auction.IntegrationTests.Api;

/// <summary>
/// The API in process (production settings, so errors show no developer detail), reached over HTTPS as a
/// browser would. It calls the real engine container over gRPC and the test database as the api role.
/// Page sizes are small so pagination is exercised with a handful of auctions.
/// </summary>
public sealed class ApiHost(string connectionString, Uri engineAddress, bool demoMode) : WebApplicationFactory<IApiAssembly>
{
    public const int DefaultPageSize = 3;
    public const int MaxPageSize = 5;
    public static readonly Uri BaseAddress = new("https://localhost");

    public HttpClient Anonymous(bool handleCookies = true) =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = BaseAddress, HandleCookies = handleCookies, AllowAutoRedirect = false });

    /// <summary>A new self-registered user, signed in on their own client.</summary>
    public async Task<ApiUser> SignUpAsync(string? displayName = null)
    {
        var http = Anonymous();
        var email = $"{Guid.NewGuid():N}@example.test";
        using var response = await http.PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName = displayName ?? "Test user", email }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var account = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return new ApiUser(http, account.GetProperty("id").GetGuid(), email);
    }

    /// <summary>Signs in on a new client; the caller checks the status.</summary>
    public async Task<(HttpClient Http, HttpResponseMessage Response)> SignInAsync(string email, string? password = null)
    {
        var http = Anonymous();
        var response = await http.PostAsJsonAsync("/api/v1/account/sign-in", new { email, password }, TestContext.Current.CancellationToken);
        return (http, response);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:Api", connectionString);
        builder.UseSetting("Api:EngineAddress", engineAddress.ToString());
        builder.UseSetting("Api:DemoMode", demoMode ? "true" : "false");
        builder.UseSetting("Api:DefaultPageSize", DefaultPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Api:MaxPageSize", MaxPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>A signed-in user and the client that carries their session cookie.</summary>
public sealed record ApiUser(HttpClient Http, Guid Id, string Email);
