using System.Diagnostics.Metrics;

namespace Auction.Api.Admin;

/// <summary>
/// The admin latency KPIs (UI-04), measured in process: each sample goes to a rolling five-minute window
/// for the admin page and to a .NET metrics histogram (microseconds) for any metrics exporter.
/// </summary>
public sealed class AdminKpis
{
    public const string MeterName = "Auction.Api";

    private readonly Histogram<long> _apiHistogram;
    private readonly Histogram<long> _bidHistogram;
    private readonly Histogram<long> _engineHistogram;
    private readonly Histogram<long> _publishHistogram;

    public AdminKpis(TimeProvider clock, IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        Api = new LatencyWindow(clock);
        Bid = new LatencyWindow(clock);
        Engine = new LatencyWindow(clock);
        Publish = new LatencyWindow(clock);
        var meter = meters.Create(MeterName);
        _apiHistogram = meter.CreateHistogram<long>("auction.api.request.duration", "us", "API request latency");
        _bidHistogram = meter.CreateHistogram<long>("auction.bid.duration", "us", "Bid latency: API receipt to response, through the engine commit");
        _engineHistogram = meter.CreateHistogram<long>("auction.engine.call.duration", "us", "Engine gRPC call latency");
        _publishHistogram = meter.CreateHistogram<long>("auction.publish.lag", "us", "Commit to live hint sent");
    }

    /// <summary>API request latency, every /api request.</summary>
    public LatencyWindow Api { get; }

    /// <summary>Bid latency: from the API receiving a bid to its response, including the engine's commit.</summary>
    public LatencyWindow Bid { get; }

    /// <summary>Engine gRPC call latency, every command.</summary>
    public LatencyWindow Engine { get; }

    /// <summary>Publish lag: from the outbox event's transaction to its live hint being sent.</summary>
    public LatencyWindow Publish { get; }

    public void RecordApi(TimeSpan elapsed) => Record(Api, _apiHistogram, elapsed);

    public void RecordBid(TimeSpan elapsed) => Record(Bid, _bidHistogram, elapsed);

    public void RecordEngine(TimeSpan elapsed) => Record(Engine, _engineHistogram, elapsed);

    public void RecordPublish(TimeSpan elapsed) => Record(Publish, _publishHistogram, elapsed);

    private static void Record(LatencyWindow window, Histogram<long> histogram, TimeSpan elapsed)
    {
        window.Record(elapsed);
        histogram.Record(elapsed.Ticks / TimeSpan.TicksPerMicrosecond);
    }
}
