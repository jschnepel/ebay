using Auction.Api.Accounts;
using Auction.Api.Hosting;
using Auction.Contracts.V1;
using Grpc.Core;

namespace Auction.Api.Auctions;

/// <summary>
/// Sends one command to the engine and turns its recorded result into an HTTP response. The actor is the
/// signed-in user and the request id is the client's Idempotency-Key, so a retry after a timeout replays
/// the engine's recorded outcome instead of running the command twice.
/// </summary>
internal sealed partial class EngineGateway(
    AuctionCommandService.AuctionCommandServiceClient client, ApiOptions options, Admin.AdminKpis kpis, Caching.AuctionCache cache,
    ILogger<EngineGateway> logger)
{
    public const string KeyHeader = "Idempotency-Key";

    // The engine puts this id on every log line for the call, so its logs match the API request.
    private const string CorrelationHeader = "x-correlation-id";

    public AuctionCommandService.AuctionCommandServiceClient Client => client;

    /// <summary>The signed-in user, as the engine's actor_id.</summary>
    public static string Actor(HttpContext http) =>
        http.User.UserIdOf()?.Value.ToString() ?? throw new InvalidOperationException("The endpoint requires a signed-in user.");

    public static bool TryGetKey(HttpRequest request, out string key)
    {
        key = request.Headers[KeyHeader].ToString();
        return key.Length > 0;
    }

    public static IResult MissingKey() =>
        Problems.BadRequest("key_required", $"Send an {KeyHeader} header; reuse it to retry the same request.");

    public async Task<IResult> SendAsync(HttpContext http, Func<CallOptions, Task<CommandResult>> call, Func<CommandResult, IResult> accepted)
    {
        var callOptions = new CallOptions(
            headers: new Metadata { { CorrelationHeader, http.TraceIdentifier }, { "x-auction-role", http.User.IsAdmin() ? "admin" : "participant" } },
            deadline: DateTime.UtcNow + options.EngineDeadline,
            cancellationToken: http.RequestAborted);
        CommandResult result;
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            result = await call(callOptions);
        }
        catch (RpcException e) when (e.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            LogEngineUnavailable(logger, e.StatusCode, e);
            return Unavailable(http);
        }
        finally
        {
            kpis.RecordEngine(TimeProvider.System.GetElapsedTime(started));
        }

        if (!result.Succeeded)
        {
            return Refused(http, result);
        }

        // Read-your-writes through the cache: any read after this response sees at least this version.
        if (result.HasVersion && Guid.TryParse(result.AuctionId, out var auction))
        {
            await cache.AnnounceAsync(auction, result.Version);
        }

        return accepted(result);
    }

    /// <summary>The response body for an accepted command; <c>youLead</c> only for a bid.</summary>
    public static CommandResponse Response(CommandResult result, string actor, bool isBid = false) => new()
    {
        AuctionId = Guid.Parse(result.AuctionId),
        Version = result.HasVersion ? result.Version : null,
        BidSequence = result.HasBidSequence ? result.BidSequence : null,
        CurrentPriceCents = result.HasCurrentPriceCents ? result.CurrentPriceCents : null,
        YouLead = isBid ? result.LeaderId == actor : null,
        Replayed = result.Replayed,
    };

    private static IResult Refused(HttpContext http, CommandResult result) => result.Error switch
    {
        CommandError.InvalidKey => Problems.BadRequest("invalid_key", $"The {KeyHeader} must be 1 to 255 characters."),
        CommandError.KeyReused => Problem(422, "key_reused", $"This {KeyHeader} was already used for a different request."),
        CommandError.Busy => Unavailable(http),
        // Owner-only actions on someone else's auction don't reveal that it exists or who owns it.
        CommandError.NotFound or CommandError.NotOwner => Problems.NotFound(),
        CommandError.InvalidState => Problem(409, "invalid_state", "The auction can't do that in its current state."),
        CommandError.ValidationFailed => Problems.Validation(result.FieldErrors
            .GroupBy(e => DraftTerms.RequestField(e.Field))
            .ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray())),
        CommandError.HasBids => Problem(409, "has_bids", "The auction already has a bid, so it can't be cancelled."),
        CommandError.AuctionNotActive => Problem(409, "not_active", "The auction isn't open for bids."),
        CommandError.AuctionEnded => Problem(409, "ended", "The auction has ended."),
        CommandError.SelfBid => Problem(409, "self_bid", "You can't bid on your own auction."),
        CommandError.AboveCap => Problem(422, "above_cap", "The amount is above the largest amount accepted."),
        CommandError.BelowMinimum => Problem(422, "below_minimum", "The amount is below the minimum next bid."),
        CommandError.WrongIncrementAmount => Problem(422, "wrong_amount", "A fixed-increment bid must be exactly the next step."),
        CommandError.MaximumUnchanged => Problem(422, "maximum_unchanged", "Your new maximum must be higher than your current maximum."),
        CommandError.MaximumLowered => Problem(422, "maximum_lowered", "A maximum can't be lowered."),
        CommandError.DraftLimit => Problem(409, "draft_limit", "You have the most open drafts allowed; publish or delete one first."),
        CommandError.PublishLimit => Problem(429, "publish_limit", "You have published the most auctions allowed today; try again tomorrow (UTC)."),
        CommandError.ImageLimit => Problem(409, "image_limit", "A listing has at most four images."),
        _ => throw new InvalidOperationException($"Unexpected engine result {result.Error}."),
    };

    private static IResult Problem(int status, string code, string title) => Problems.Of(status, code, title);

    // The outcome may be unknown to the API. Retry with the same key to recover a committed result.
    private static IResult Unavailable(HttpContext http)
    {
        http.Response.Headers.RetryAfter = "1";
        return Problem(503, "busy", $"The auction service is busy; retry with the same {KeyHeader}.");
    }

    [LoggerMessage(1, LogLevel.Warning, "The engine did not answer ({Status}); the client may retry with the same key")]
    private static partial void LogEngineUnavailable(ILogger logger, StatusCode status, Exception exception);
}
