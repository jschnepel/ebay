using System.Net.Http.Json;
using System.Text.Json;
using Auction.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Auction.IntegrationTests.Api;

/// <summary>
/// The API in process (production settings, so errors show no developer detail), reached over HTTPS as a
/// browser would. It calls the real engine container over gRPC and the test database as the api role.
/// Page sizes are small so pagination is exercised with a handful of auctions. Every host shares one Redis
/// backplane, as API instances do, so a live hint from whichever host runs the relay reaches them all.
/// </summary>
public sealed class ApiHost(string connectionString, Uri engineAddress, bool demoMode, string redis, IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<IApiAssembly>
{
    /// <summary>A test setting: the client address every request appears to come from (the test server has none).</summary>
    public const string ClientAddressSetting = "Test:ClientAddress";

    public const int DefaultPageSize = 3;
    public const int MaxPageSize = 5;
    public static readonly Uri BaseAddress = new("https://localhost");

    /// <summary>
    /// The same host on a real local port, for a browser: Kestrel instead of the in-memory test server.
    /// </summary>
    public static ApiHost OnLocalPort(
        string connectionString, Uri engineAddress, string redis, out Uri address, IReadOnlyDictionary<string, string>? settings = null)
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var host = new ApiHost(connectionString, engineAddress, demoMode: true, redis, settings);
        host.UseKestrel(port);
        host.StartServer();
        address = new Uri($"http://127.0.0.1:{port}");
        return host;
    }

    public HttpClient Anonymous(bool handleCookies = true) =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = BaseAddress, HandleCookies = handleCookies, AllowAutoRedirect = false });

    private static readonly Dictionary<string, string> Unlimited = new()
    {
        ["Api:RateLimits:SignInPerAccountPerMinute"] = "100000",
        ["Api:RateLimits:SignInPerAddressPerMinute"] = "100000",
        ["Api:RateLimits:SignUpPerAddressPerHour"] = "100000",
        ["Api:RateLimits:WritesPerUserPerMinute"] = "100000",
    };

    /// <summary>A new self-registered account, signed in on its own client: a user, or with role "admin" a visitor admin.</summary>
    public async Task<ApiUser> SignUpAsync(string? displayName = null, string? role = null, string? email = null)
    {
        var http = Anonymous();
        email ??= $"{Guid.NewGuid():N}@example.test";
        using var response = await http.PostAsJsonAsync(
            "/api/v1/account/sign-up", new { displayName = displayName ?? "Test user", email, role }, TestContext.Current.CancellationToken);
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
        // The client's files come from the build output, as they do in development.
        builder.UseStaticWebAssets();
        builder.UseSetting("ConnectionStrings:Api", connectionString);
        builder.UseSetting("Api:EngineAddress", engineAddress.ToString());
        builder.UseSetting("Api:DemoMode", demoMode ? "true" : "false");
        builder.UseSetting("ConnectionStrings:Redis", redis);
        builder.UseSetting("Api:RelayPollInterval", "00:00:00.500");
        builder.UseSetting("Api:RelayRetryDelay", "00:00:00.500");

        // Small pages exercise paging; a test's own settings below may raise them.
        builder.UseSetting("Api:DefaultPageSize", DefaultPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("Api:MaxPageSize", MaxPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Every test signs users up and in from one address, so the limits are out of the way unless a test sets them.
        foreach (var (name, value) in Unlimited.Concat(settings ?? new Dictionary<string, string>()))
        {
            builder.UseSetting(name, value);
        }

        if (settings?.GetValueOrDefault(ClientAddressSetting) is { } address)
        {
            builder.ConfigureServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new ClientAddress(System.Net.IPAddress.Parse(address))));
        }
    }
}

/// <summary>A signed-in user and the client that carries their session cookie.</summary>
public sealed record ApiUser(HttpClient Http, Guid Id, string Email);

/// <summary>Gives every request the same client address, as requests through one proxy would have.</summary>
internal sealed class ClientAddress(System.Net.IPAddress address) : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        app.Use(nextMiddleware => context =>
        {
            context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };
}
