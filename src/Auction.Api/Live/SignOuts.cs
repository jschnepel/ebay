using Microsoft.AspNetCore.SignalR;

namespace Auction.Api.Live;

/// <summary>
/// Tells the open pages of ended sessions that they were signed out. Best effort: the session is already
/// revoked on the server, so if the message can't go out (Redis down), the page learns on its next request.
/// </summary>
internal static partial class SignOuts
{
    public static async Task TellAsync(IServiceProvider services, IReadOnlyList<Guid> ended, CancellationToken cancellationToken)
    {
        if (ended.Count == 0)
        {
            return;
        }

        try
        {
            var hub = services.GetRequiredService<IHubContext<AuctionHub>>();
            await hub.Clients.Groups([.. ended.Select(AuctionHub.SessionGroup)]).SendAsync(AuctionHub.SignedOutMethod, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogNotSent(services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SignOuts)), e);
        }
    }

    [LoggerMessage(1, LogLevel.Warning, "Couldn't tell ended sessions they were signed out; they find out on their next request")]
    private static partial void LogNotSent(ILogger logger, Exception exception);
}
