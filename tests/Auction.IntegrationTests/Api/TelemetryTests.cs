using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Auction.IntegrationTests.Database;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Auction.IntegrationTests.Api;

/// <summary>OpenTelemetry export over OTLP (OBS-01), received by a stand-in collector.</summary>
public sealed class TelemetryTests(MigratedDatabase db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_an_OTLP_endpoint_the_API_exports_a_bids_trace_its_KPI_metrics_and_logs_and_traces_the_publish()
    {
        await using var collector = await OtlpCollector.StartAsync();
        await using var api = await db.ApiWithAsync(new Dictionary<string, string>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = collector.Address,
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_BSP_SCHEDULE_DELAY"] = "200",
            ["OTEL_BLRP_SCHEDULE_DELAY"] = "200",
            ["OTEL_METRIC_EXPORT_INTERVAL"] = "500",
        });
        var published = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Auction.Api",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = published.Add,
        };
        ActivitySource.AddActivityListener(listener);
        var buyer = await api.SignUpAsync();
        var id = await (await api.SignUpAsync()).PublishedAsync();
        var traceId = ActivityTraceId.CreateRandom();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/auctions/{id}/bids") { Content = JsonContent.Create(new { amountCents = 1500 }) };
        request.Headers.Add(ApiCalls.KeyHeader, Guid.NewGuid().ToString());
        request.Headers.Add("traceparent", $"00-{traceId.ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        using var response = await buyer.Http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var traceBytes = new byte[16];
        traceId.CopyTo(traceBytes);
        await collector.WaitForAsync("/v1/traces", body => body.AsSpan().IndexOf(traceBytes) >= 0);
        await collector.WaitForAsync("/v1/metrics", body => body.AsSpan().IndexOf("auction.bid.duration"u8) >= 0);
        await collector.WaitForAsync("/v1/logs", body => body.Length > 0);
        await WaitUntilAsync(() => published.Any(span => span.OperationName == "outbox.publish"
            && ((string?)span.GetTagItem("auction.ids"))?.Contains(id.ToString(), StringComparison.Ordinal) == true));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(condition(), "The publish span never appeared.");
    }

    /// <summary>A stand-in OTLP/HTTP collector: it keeps every body posted to /v1/traces, /v1/metrics and /v1/logs.</summary>
    private sealed class OtlpCollector : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<(string Path, byte[] Body)> _received;

        private OtlpCollector(WebApplication app, ConcurrentQueue<(string, byte[])> received, string address)
        {
            _app = app;
            _received = received;
            Address = address;
        }

        public string Address { get; }

        public static async Task<OtlpCollector> StartAsync()
        {
            var received = new ConcurrentQueue<(string, byte[])>();
            var app = WebApplication.CreateBuilder().Build();
            app.MapPost("/v1/{signal}", async (HttpContext http) =>
            {
                using var body = new MemoryStream();
                await http.Request.Body.CopyToAsync(body, http.RequestAborted);
                received.Enqueue((http.Request.Path.Value!, body.ToArray()));
                return Results.Ok();
            });
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var address = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
            probe.Stop();
            app.Urls.Add(address);
            await app.StartAsync(Ct);
            return new OtlpCollector(app, received, address);
        }

        public async Task WaitForAsync(string path, Func<byte[], bool> matches)
        {
            for (var i = 0; i < 300; i++)
            {
                if (_received.Any(r => r.Path == path && matches(r.Body)))
                {
                    return;
                }

                await Task.Delay(100, Ct);
            }

            var seen = string.Join(", ", _received.GroupBy(r => r.Path).Select(g => $"{g.Key} x{g.Count()}"));
            Assert.Fail(new StringBuilder($"Nothing matching arrived at {path}; received: ").Append(seen.Length > 0 ? seen : "nothing").ToString());
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(Ct);
            await _app.DisposeAsync();
        }
    }
}
