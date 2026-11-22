using System.Diagnostics;
using Auction.Api.Admin;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Auction.Api.Hosting;

/// <summary>
/// OpenTelemetry for the API (OBS-01): request, engine-call, database and publish traces, the KPI
/// histograms and runtime metrics, and logs, exported over OTLP. Off unless <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
/// is set (the optional Aspire dashboard in Compose); the admin KPIs never depend on it.
/// </summary>
internal static class Telemetry
{
    public const string SourceName = "Auction.Api";

    /// <summary>Spans the API starts itself: the outbox relay's publish path.</summary>
    public static readonly ActivitySource Source = new(SourceName);

    public static void AddTelemetry(this WebApplicationBuilder builder)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            return;
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("auction-api"))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(aspNet => aspNet.Filter = http => !IsProbe(http.Request.Path))
                .AddHttpClientInstrumentation()
                .AddSource(SourceName, "Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(AdminKpis.MeterName, "Npgsql"))
            .WithLogging()
            .UseOtlpExporter();
    }

    // Compose and the platform poll these every few seconds; their spans would bury the real traffic.
    private static bool IsProbe(PathString path) => path.StartsWithSegments("/health") || path.StartsWithSegments("/ready");
}
