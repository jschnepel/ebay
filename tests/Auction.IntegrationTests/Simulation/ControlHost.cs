using System.Net.Http.Headers;
using System.Text.Json;
using Auction.IntegrationTests.Api;
using Auction.Simulator.Control;
using Microsoft.AspNetCore.Builder;

namespace Auction.IntegrationTests.Simulation;

/// <summary>The simulator's control endpoint in process on a local port, as <c>simulator serve</c> runs it.</summary>
public sealed class ControlHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private ControlHost(WebApplication app, Uri address)
    {
        _app = app;
        Address = address;
    }

    public Uri Address { get; }

    public static async Task<ControlHost> StartAsync(ControlSettings settings, int? port = null)
    {
        var address = new Uri($"http://127.0.0.1:{port ?? FreePort()}/");
        var app = ControlServer.Build(settings, []);
        app.Urls.Add(address.ToString());
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new ControlHost(app, address);
    }

    public static HttpClient Client(Uri address, string token) => new()
    {
        BaseAddress = address,
        DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
    };

    public static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        return ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>Polls the run's state until the condition holds, for up to a minute.</summary>
    public static async Task<JsonElement> WaitForAsync(
        HttpClient http, Func<JsonElement, bool> condition, string url = "runs/current", string? property = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        while (true)
        {
            var body = await http.GetJsonAsync(url);
            var run = property is null ? body : body.GetProperty(property);
            if (condition(run))
            {
                return run;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Timed out waiting on the simulator: {run}");
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TestContext.Current.CancellationToken);
        await _app.DisposeAsync();
    }
}
