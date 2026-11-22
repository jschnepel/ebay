using System.Diagnostics;

namespace Auction.Engine;

/// <summary>The engine's own spans (OBS-01): one per command transaction, under the gRPC call that sent it.</summary>
public static class EngineTelemetry
{
    public const string SourceName = "Auction.Engine";

    public const string TransactionSpan = "auction.transaction";

    public static readonly ActivitySource Source = new(SourceName);
}
