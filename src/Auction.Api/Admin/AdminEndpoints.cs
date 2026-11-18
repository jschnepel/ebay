using Auction.Api.Accounts;
using Auction.Api.Auctions;
using Auction.Api.Hosting;
using Auction.Api.Live;
using Auction.Contracts.V1;
using Grpc.Health.V1;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace Auction.Api.Admin;

public sealed record ReasonRequest(string? Reason);

/// <summary>The moderation list's query: a title, seller name or id fragment, a status, and the page.</summary>
public sealed record AuctionSearch(string? Q, string? Status, int? PageSize, string? Cursor);

public sealed record HealthItem(string Name, string State, string? Detail);

public sealed record AdminHealth(IReadOnlyList<HealthItem> Components, long OutboxBacklog);

public sealed record AdminKpiView(int WindowSeconds, LatencySummary ApiRequest, LatencySummary Bid, LatencySummary EngineCall, LatencySummary PublishLag);

/// <summary>
/// The admin area (UI-04, ADM-01). Every route answers 404 to anyone who isn't an admin, so its existence
/// isn't revealed. Moderation goes to the engine with the admin as the actor and a required reason, which
/// the engine audits; disabling a user is the API's own write, audited in the same transaction.
/// </summary>
internal static class AdminEndpoints
{
    public const int ReasonMaxLength = 500;

    public static RouteGroupBuilder MapAdmin(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Admin").AddEndpointFilter(AdminOnlyAsync)
            .ProducesProblem(StatusCodes.Status404NotFound);
        admin.MapGet("/activity", ActivityAsync).Produces<Activity>();
        admin.MapGet("/health", HealthAsync).Produces<AdminHealth>();
        admin.MapGet("/kpis", Kpis).Produces<AdminKpiView>();
        admin.MapGet("/auctions", AuctionsAsync).Produces<Page<ModeratedAuction>>().ProducesProblem(StatusCodes.Status400BadRequest);
        admin.MapPost("/auctions/{id:guid}/suspend", (Guid id, ReasonRequest body, HttpContext http, EngineGateway engine) =>
            ModerateAsync(id, body, http, engine, Moderation.Suspend));
        admin.MapPost("/auctions/{id:guid}/resume", (Guid id, ReasonRequest body, HttpContext http, EngineGateway engine) =>
            ModerateAsync(id, body, http, engine, Moderation.Resume));
        admin.MapPost("/auctions/{id:guid}/cancel", (Guid id, ReasonRequest body, HttpContext http, EngineGateway engine) =>
            ModerateAsync(id, body, http, engine, Moderation.Cancel));
        admin.MapGet("/users", UsersAsync).Produces<Page<UserSummary>>().ProducesProblem(StatusCodes.Status400BadRequest);
        admin.MapPost("/users/{id:guid}/disable", (Guid id, ReasonRequest body, HttpContext http, AccountStore accounts) =>
            SetDisabledAsync(id, body, http, accounts, disabled: true));
        admin.MapPost("/users/{id:guid}/enable", (Guid id, ReasonRequest body, HttpContext http, AccountStore accounts) =>
            SetDisabledAsync(id, body, http, accounts, disabled: false));
        return api;
    }

    private static async ValueTask<object?> AdminOnlyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.User.IsAdmin() ? await next(context) : Problems.Of(StatusCodes.Status404NotFound, "not_found", "Not found.");

    private static async Task<Ok<Activity>> ActivityAsync(AdminReader reader, Presence presence, CancellationToken cancellationToken) =>
        TypedResults.Ok(await reader.ActivityAsync(presence.OnlineUsers, cancellationToken));

    private static async Task<Ok<AdminHealth>> HealthAsync(
        AdminReader reader, Health.HealthClient engine, RedisHealth redis, CancellationToken cancellationToken)
    {
        var components = new List<HealthItem> { new("API", "ok", null) };
        components.Add(await ProbeAsync("Engine", async () =>
            (await engine.CheckAsync(new HealthCheckRequest(), deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: cancellationToken)).Status
                == HealthCheckResponse.Types.ServingStatus.Serving));
        long backlog = -1;
        components.Add(await ProbeAsync("PostgreSQL", async () =>
        {
            backlog = await reader.OutboxBacklogAsync(cancellationToken);
            return true;
        }));
        components.Add(await redis.CheckAsync());
        return TypedResults.Ok(new AdminHealth(components, backlog));
    }

    private static Ok<AdminKpiView> Kpis(AdminKpis kpis) => TypedResults.Ok(new AdminKpiView(
        LatencyWindow.Seconds, kpis.Api.Summarize(), kpis.Bid.Summarize(), kpis.Engine.Summarize(), kpis.Publish.Summarize()));

    private static async Task<IResult> AuctionsAsync(
        [AsParameters] AuctionSearch search, AdminReader reader, ApiOptions options, CancellationToken cancellationToken)
    {
        if (!Paging.TryParse(search.PageSize, search.Cursor, options, out var size, out var after, out var problem))
        {
            return problem;
        }

        var status = string.IsNullOrWhiteSpace(search.Status) ? null : search.Status;
        return TypedResults.Ok(await reader.AuctionsAsync(search.Q, status, after, size, cancellationToken));
    }

    private static async Task<IResult> UsersAsync(
        string? q, int? pageSize, string? cursor, AdminReader reader, ApiOptions options, CancellationToken cancellationToken)
    {
        if (!Paging.TryParse(pageSize, cursor, options, out var size, out var after, out var problem))
        {
            return problem;
        }

        return TypedResults.Ok(await reader.UsersAsync(q, after, size, cancellationToken));
    }

    private static async Task<IResult> ModerateAsync(Guid id, ReasonRequest body, HttpContext http, EngineGateway engine, Moderation action)
    {
        if (!EngineGateway.TryGetKey(http.Request, out var key))
        {
            return EngineGateway.MissingKey();
        }

        if (ReasonOf(body) is not { } reason)
        {
            return ReasonRequired();
        }

        var actor = EngineGateway.Actor(http);
        return await engine.SendAsync(http, call => action.SendAsync(engine.Client, key, actor, id, reason, call), result =>
            TypedResults.Ok(EngineGateway.Response(result, actor)));
    }

    private static async Task<IResult> SetDisabledAsync(Guid id, ReasonRequest body, HttpContext http, AccountStore accounts, bool disabled)
    {
        if (ReasonOf(body) is not { } reason)
        {
            return ReasonRequired();
        }

        var admin = http.User.UserIdOf()!.Value;
        if (admin.Value == id)
        {
            return Problems.Of(StatusCodes.Status409Conflict, "own_account", "An admin can't disable or enable their own account.");
        }

        return await accounts.SetDisabledAsync(id, disabled, admin, reason, http.RequestAborted)
            ? TypedResults.NoContent()
            : Problems.Of(StatusCodes.Status404NotFound, "not_found", "No such user.");
    }

    private static string? ReasonOf(ReasonRequest body) =>
        body.Reason?.Trim() is { Length: > 0 and <= ReasonMaxLength } reason ? reason : null;

    private static IResult ReasonRequired() =>
        Problems.Validation(new Dictionary<string, string[]> { ["reason"] = [$"required, 1 to {ReasonMaxLength} characters"] });

    private static async Task<HealthItem> ProbeAsync(string name, Func<Task<bool>> probe)
    {
        try
        {
            return await probe() ? new HealthItem(name, "ok", null) : new HealthItem(name, "down", "not serving");
        }
        catch (Exception e) when (e is Grpc.Core.RpcException or NpgsqlException or TimeoutException)
        {
            return new HealthItem(name, "down", e.GetType().Name);
        }
    }

    /// <summary>The three moderation commands, each a different engine call with the same shape.</summary>
    private sealed record Moderation(Func<AuctionCommandService.AuctionCommandServiceClient, ModerationArgs, Grpc.Core.CallOptions, Task<CommandResult>> Call)
    {
        public static Moderation Suspend { get; } = new(async (c, a, o) =>
            (await c.SuspendAsync(new SuspendRequest { RequestId = a.Key, ActorId = a.Actor, AuctionId = a.Id, Reason = a.Reason }, o)).Result);

        public static Moderation Resume { get; } = new(async (c, a, o) =>
            (await c.ResumeAsync(new ResumeRequest { RequestId = a.Key, ActorId = a.Actor, AuctionId = a.Id, Reason = a.Reason }, o)).Result);

        public static Moderation Cancel { get; } = new(async (c, a, o) =>
            (await c.AdminCancelAsync(new AdminCancelRequest { RequestId = a.Key, ActorId = a.Actor, AuctionId = a.Id, Reason = a.Reason }, o)).Result);

        public Task<CommandResult> SendAsync(
            AuctionCommandService.AuctionCommandServiceClient client, string key, string actor, Guid id, string reason, Grpc.Core.CallOptions options) =>
            Call(client, new ModerationArgs(key, actor, id.ToString(), reason), options);
    }

    private sealed record ModerationArgs(string Key, string Actor, string Id, string Reason);
}
