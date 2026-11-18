using Auction.Api.Accounts;
using Auction.Api.Hosting;
using Auction.Contracts.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Auction.Api.Auctions;

/// <summary>
/// Auction reads (anyone; drafts only for their seller) and auction commands (signed-in users, forwarded
/// to the engine with an Idempotency-Key). Admin accounts can't sell or bid.
/// </summary>
internal static class AuctionEndpoints
{
    public sealed record BidRequest(long? AmountCents);

    public static RouteGroupBuilder MapAuctions(this RouteGroupBuilder api)
    {
        var auctions = api.MapGroup("/auctions").WithTags("Auctions");
        auctions.MapGet("", ListAsync).Produces<Page<AuctionSummary>>().ProducesProblem(StatusCodes.Status400BadRequest);
        auctions.MapGet("/{id:guid}", DetailAsync).Produces<AuctionDetail>().ProducesProblem(StatusCodes.Status404NotFound);
        auctions.MapGet("/{id:guid}/bids", BidsAsync).Produces<Page<BidView>>().ProducesProblem(StatusCodes.Status404NotFound);

        var commands = auctions.MapGroup("").RequireAuthorization()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        commands.MapPost("", CreateAsync).Produces<CommandResponse>(StatusCodes.Status201Created);
        commands.MapPut("/{id:guid}", EditAsync).Produces<CommandResponse>();
        commands.MapPost("/{id:guid}/publish", PublishAsync).Produces<CommandResponse>();
        commands.MapPost("/{id:guid}/cancel", CancelAsync).Produces<CommandResponse>();
        commands.MapPost("/{id:guid}/bids", BidAsync).Produces<CommandResponse>(StatusCodes.Status201Created);
        commands.MapDelete("/{id:guid}", DeleteDraftAsync).Produces<CommandResponse>();

        var me = api.MapGroup("/me").WithTags("Me").RequireAuthorization().ProducesProblem(StatusCodes.Status400BadRequest);
        me.MapGet("/auctions", MineAsync).Produces<Page<AuctionSummary>>();
        me.MapGet("/bids", BiddingAsync).Produces<Page<BiddingSummary>>();
        return api;
    }

    private static async Task<IResult> ListAsync(
        int? pageSize, string? cursor, AuctionReader reader, ApiOptions options, CancellationToken cancellationToken) =>
        TryPaging(pageSize, cursor, options, out var size, out var after) is { } problem
            ? problem
            : TypedResults.Ok(await reader.ListAsync(after, size, cancellationToken));

    private static async Task<IResult> MineAsync(
        int? pageSize, string? cursor, HttpContext http, AuctionReader reader, ApiOptions options) =>
        TryPaging(pageSize, cursor, options, out var size, out var after) is { } problem
            ? problem
            : TypedResults.Ok(await reader.MineAsync(http.User.UserIdOf()!.Value, after, size, http.RequestAborted));

    private static async Task<IResult> BiddingAsync(
        int? pageSize, string? cursor, HttpContext http, AuctionReader reader, ApiOptions options) =>
        TryPaging(pageSize, cursor, options, out var size, out var after) is { } problem
            ? problem
            : TypedResults.Ok(await reader.BiddingAsync(http.User.UserIdOf()!.Value, after, size, http.RequestAborted));

    private static IResult? TryPaging(int? pageSize, string? cursor, ApiOptions options, out int size, out (DateTimeOffset, Guid)? after) =>
        Paging.TryParse(pageSize, cursor, options, out size, out after, out var problem) ? null : problem;

    private static async Task<IResult> DetailAsync(Guid id, HttpContext http, AuctionReader reader, CancellationToken cancellationToken) =>
        await reader.DetailAsync(id, http.User.UserIdOf(), cancellationToken) is { } detail ? TypedResults.Ok(detail) : Problems.NotFound();

    private static async Task<IResult> BidsAsync(
        Guid id, int? pageSize, string? cursor, HttpContext http, AuctionReader reader, ApiOptions options)
    {
        if (PageSize(pageSize, options) is not { } size)
        {
            return Problems.BadRequest("invalid_page_size", "pageSize must be at least 1.");
        }

        if (!Cursors.TryDecode(cursor, out long? before))
        {
            return Problems.BadRequest("invalid_cursor", "The cursor is not one this API returned.");
        }

        return await reader.BidsAsync(id, http.User.UserIdOf(), before, size, http.RequestAborted) is { } page
            ? TypedResults.Ok(page)
            : Problems.NotFound();
    }

    private static async Task<IResult> CreateAsync(DraftTermsRequest body, HttpContext http, EngineGateway engine)
    {
        if (http.User.IsAdmin())
        {
            return AdminOnly();
        }

        if (!EngineGateway.TryGetKey(http.Request, out var key))
        {
            return EngineGateway.MissingKey();
        }

        if (!DraftTerms.TryMap(body, out var terms, out var errors))
        {
            return Problems.Validation(errors);
        }

        var actor = EngineGateway.Actor(http);
        var request = new CreateDraftRequest { RequestId = key, ActorId = actor, Terms = terms };
        return await engine.SendAsync(
            http,
            async call => (await engine.Client.CreateDraftAsync(request, call)).Result,
            result => TypedResults.Created($"/api/v1/auctions/{result.AuctionId}", EngineGateway.Response(result, actor)));
    }

    private static async Task<IResult> EditAsync(Guid id, DraftTermsRequest body, HttpContext http, EngineGateway engine)
    {
        if (!EngineGateway.TryGetKey(http.Request, out var key))
        {
            return EngineGateway.MissingKey();
        }

        if (!DraftTerms.TryMap(body, out var terms, out var errors))
        {
            return Problems.Validation(errors);
        }

        var actor = EngineGateway.Actor(http);
        var request = new EditDraftRequest { RequestId = key, ActorId = actor, AuctionId = id.ToString(), Terms = terms };
        return await engine.SendAsync(
            http, async call => (await engine.Client.EditDraftAsync(request, call)).Result, result => Ok(result, actor));
    }

    /// <summary>The owner deletes a draft; a published auction can only be cancelled.</summary>
    private static async Task<IResult> DeleteDraftAsync(Guid id, HttpContext http, EngineGateway engine)
    {
        if (!EngineGateway.TryGetKey(http.Request, out var key))
        {
            return EngineGateway.MissingKey();
        }

        var actor = EngineGateway.Actor(http);
        var request = new DeleteDraftRequest { RequestId = key, ActorId = actor, AuctionId = id.ToString() };
        return await engine.SendAsync(
            http, async call => (await engine.Client.DeleteDraftAsync(request, call)).Result, result => Ok(result, actor));
    }

    private static async Task<IResult> PublishAsync(Guid id, HttpContext http, EngineGateway engine)
    {
        if (!EngineGateway.TryGetKey(http.Request, out var key))
        {
            return EngineGateway.MissingKey();
        }

        var actor = EngineGateway.Actor(http);
        var request = new PublishRequest { RequestId = key, ActorId = actor, AuctionId = id.ToString() };
        return await engine.SendAsync(
            http, async call => (await engine.Client.PublishAsync(request, call)).Result, result => Ok(result, actor));
    }

    private static async Task<IResult> CancelAsync(Guid id, HttpContext http, EngineGateway engine)
    {
        if (!EngineGateway.TryGetKey(http.Request, out var key))
        {
            return EngineGateway.MissingKey();
        }

        var actor = EngineGateway.Actor(http);
        var request = new CancelRequest { RequestId = key, ActorId = actor, AuctionId = id.ToString() };
        return await engine.SendAsync(
            http, async call => (await engine.Client.CancelAsync(request, call)).Result, result => Ok(result, actor));
    }

    // The bidder is always the signed-in user; any bidder or actor id in the body is ignored.
    private static async Task<IResult> BidAsync(Guid id, BidRequest body, HttpContext http, EngineGateway engine, AuctionReader reader)
    {
        var started = TimeProvider.System.GetTimestamp();
        var result = await PlaceBidAsync(id, body, http, engine, reader);
        http.RequestServices.GetRequiredService<Admin.AdminKpis>().RecordBid(TimeProvider.System.GetElapsedTime(started));
        return result;
    }

    private static async Task<IResult> PlaceBidAsync(Guid id, BidRequest body, HttpContext http, EngineGateway engine, AuctionReader reader)
    {
        if (http.User.IsAdmin())
        {
            // A draft stays hidden from admins too; only a visible auction gets the admin refusal.
            return await reader.StatusAsync(id, viewer: null, http.RequestAborted) is null ? Problems.NotFound() : AdminOnly();
        }

        if (!EngineGateway.TryGetKey(http.Request, out var key))
        {
            return EngineGateway.MissingKey();
        }

        if (body.AmountCents is not { } amount)
        {
            return Problems.Validation(new Dictionary<string, string[]> { ["amountCents"] = ["required"] });
        }

        var actor = EngineGateway.Actor(http);
        var request = new PlaceBidRequest { RequestId = key, ActorId = actor, AuctionId = id.ToString(), AmountCents = amount };
        return await engine.SendAsync(
            http,
            async call => (await engine.Client.PlaceBidAsync(request, call)).Result,
            result => TypedResults.Created($"/api/v1/auctions/{id}/bids", EngineGateway.Response(result, actor, isBid: true)));
    }

    private static Ok<CommandResponse> Ok(CommandResult result, string actor) => TypedResults.Ok(EngineGateway.Response(result, actor));

    private static IResult AdminOnly() =>
        Problems.Of(StatusCodes.Status403Forbidden, "admin_only", "Admin accounts have admin privileges only; they can't sell or bid.");

    /// <summary>The requested page size cut to the maximum, the default when absent, or null when below 1.</summary>
    private static int? PageSize(int? requested, ApiOptions options) => requested switch
    {
        null => options.DefaultPageSize,
        < 1 => null,
        _ => Math.Min(requested.Value, options.MaxPageSize),
    };
}
