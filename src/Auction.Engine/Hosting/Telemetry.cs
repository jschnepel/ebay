using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Auction.Engine.Hosting;

/// <summary>
/// OpenTelemetry for the engine (OBS-01): gRPC call, command transaction and database traces, runtime and
/// database metrics, and logs, exported over OTLP. Off unless <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
/// </summary>
internal static class Telemetry
{
    public static void AddTelemetry(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return;
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("auction-engine"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(aspNet => aspNet.Filter = http => !http.Request.Path.StartsWithSegments("/grpc.health.v1.Health"))
                .AddSource(EngineTelemetry.SourceName, "Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter("Npgsql"))
            .WithLogging()
            .UseOtlpExporter();
    }
}
