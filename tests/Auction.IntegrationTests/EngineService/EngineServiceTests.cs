using Auction.Domain;
using Auction.Engine;
using Auction.IntegrationTests.Database;
using Grpc.Core;
using Grpc.Health.V1;
using static Auction.IntegrationTests.Engine.EngineScenario;
using V1 = Auction.Contracts.V1;

namespace Auction.IntegrationTests.EngineService;

/// <summary>The engine container serves the auction.v1 contract and its health over gRPC.</summary>
public sealed class EngineServiceTests(MigratedDatabase db)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_engine_requires_the_api_identity_and_a_moderator_role()
    {
        var engine = await db.EngineServiceAsync();
        var cancel = new V1.AdminCancelRequest { AuctionId = Guid.NewGuid().ToString(), ActorId = UserId.New().ToString(), Reason = "Review" };
        using var anonymous = engine.ChannelWithoutIdentity();
        var unauthenticated = new V1.AuctionCommandService.AuctionCommandServiceClient(anonymous);
        var missing = await Assert.ThrowsAsync<RpcException>(async () => await unauthenticated.AdminCancelAsync(cancel, cancellationToken: _ct));
        var wrongRole = await Assert.ThrowsAsync<RpcException>(async () => await engine.Commands.AdminCancelAsync(cancel, cancellationToken: _ct));

        Assert.Equal(StatusCode.Unauthenticated, missing.StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, wrongRole.StatusCode);
    }

    [Fact]
    public async Task The_health_service_reports_serving()
    {
        var engine = await db.EngineServiceAsync();

        var reply = await engine.Health.CheckAsync(new HealthCheckRequest(), cancellationToken: _ct);

        Assert.Equal(HealthCheckResponse.Types.ServingStatus.Serving, reply.Status);
    }

    [Fact]
    public async Task A_seller_lists_and_a_buyer_bids_over_grpc()
    {
        var commands = new GrpcCommands((await db.EngineServiceAsync()).Commands);
        var (seller, buyer) = (UserId.New(), UserId.New());

        var created = await commands.CreateDraftAsync(As(seller), Terms(await db.NowAsync()));
        var id = created.AuctionId!.Value;
        var published = await commands.PublishAsync(As(seller), id);
        var bid = await commands.PlaceBidAsync(As(buyer), id, 1500);

        Assert.Equal((CommandError.None, CommandError.None, CommandError.None), (created.Error, published.Error, bid.Error));
        Assert.Equal((buyer, new Money(1000), 1L, 2L), (bid.LeaderId!.Value, bid.CurrentPrice!.Value, bid.BidSequence!.Value, bid.Version!.Value));
        Assert.Equal(buyer.Value, await db.ScalarAsync<Guid>(
            MigratedDatabase.Verifier, "SELECT leader_id FROM auctions WHERE id = $1", id.Value));
    }

    [Fact]
    public async Task Refusals_and_replays_come_back_as_results()
    {
        var commands = new GrpcCommands((await db.EngineServiceAsync()).Commands);
        var seller = UserId.New();
        var id = (await commands.CreateDraftAsync(As(seller), Terms(await db.NowAsync()))).AuctionId!.Value;
        await commands.PublishAsync(As(seller), id);
        var context = As(UserId.New());

        var selfBid = await commands.PlaceBidAsync(As(seller), id, 1500);
        var first = await commands.PlaceBidAsync(context, id, 1500);
        var again = await commands.PlaceBidAsync(context, id, 1500);
        var reused = await commands.PlaceBidAsync(context, id, 1600);

        Assert.Equal(CommandError.SelfBid, selfBid.Error);
        Assert.Equal((false, true), (first.Replayed, again.Replayed));
        Assert.Equal(first.BidSequence, again.BidSequence);
        Assert.Equal(CommandError.KeyReused, reused.Error);
    }

    [Fact]
    public async Task A_draft_refused_by_validation_returns_its_field_errors()
    {
        var commands = new GrpcCommands((await db.EngineServiceAsync()).Commands);
        var terms = Terms(await db.NowAsync()) with { OpeningPrice = new Money(0) };

        var created = await commands.CreateDraftAsync(As(UserId.New()), terms);

        Assert.Equal(CommandError.ValidationFailed, created.Error);
        Assert.Contains(new FieldError("openingPrice", "out_of_range"), created.FieldErrors);
    }

    [Theory]
    [InlineData(FloorType.Reserve, BidEntry.Proxy, "reserve", "proxy")]
    [InlineData(FloorType.MinimumBid, BidEntry.FixedIncrement, "minimum_bid", "fixed_increment")]
    [InlineData(FloorType.Absolute, BidEntry.Proxy, "absolute", "proxy")]
    public async Task Terms_arrive_intact_through_the_contract(FloorType floor, BidEntry entry, string storedFloor, string storedEntry)
    {
        var commands = new GrpcCommands((await db.EngineServiceAsync()).Commands);
        var now = await db.NowAsync();
        var terms = Terms(now, entry) with
        {
            FloorType = floor,
            Reserve = floor == FloorType.Reserve ? new Money(2000) : null,
            Increment = new Money(250),
        };

        var id = (await commands.CreateDraftAsync(As(UserId.New()), terms)).AuctionId!.Value;

        const string sql = "SELECT concat_ws('|', floor_type, bid_entry, coalesce(reserve_cents::text, '-'), increment_cents, " +
                           "extract(epoch FROM ends_at - $2)::int) FROM auctions WHERE id = $1";
        var stored = await db.ScalarAsync<string>(MigratedDatabase.Verifier, sql, id.Value, now);
        Assert.Equal($"{storedFloor}|{storedEntry}|{(floor == FloorType.Reserve ? "2000" : "-")}|250|3600", stored);
    }

    [Theory]
    [InlineData("not-a-uuid", "8c2f7d2e-7d1a-4c1e-9f3b-2b6f2f9f6a10")]
    [InlineData("8c2f7d2e-7d1a-4c1e-9f3b-2b6f2f9f6a10", "")]
    public async Task A_malformed_id_is_an_invalid_argument(string actorId, string auctionId)
    {
        var engine = await db.EngineServiceAsync();
        var request = new V1.PlaceBidRequest { RequestId = Guid.NewGuid().ToString(), ActorId = actorId, AuctionId = auctionId, AmountCents = 1500 };

        var error = await Assert.ThrowsAsync<RpcException>(async () => await engine.Commands.PlaceBidAsync(request, cancellationToken: _ct));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task A_draft_without_terms_is_an_invalid_argument()
    {
        var engine = await db.EngineServiceAsync();
        var request = new V1.CreateDraftRequest { RequestId = Guid.NewGuid().ToString(), ActorId = UserId.New().ToString() };

        var error = await Assert.ThrowsAsync<RpcException>(async () => await engine.Commands.CreateDraftAsync(request, cancellationToken: _ct));

        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task Each_call_is_logged_with_the_callers_correlation_id()
    {
        var engine = await db.EngineServiceAsync();
        var correlationId = $"test-{Guid.NewGuid():N}";
        var request = new V1.CreateDraftRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            ActorId = UserId.New().ToString(),
            Terms = GrpcCommands.ToContract(Terms(await db.NowAsync())),
        };

        await engine.Commands.CreateDraftAsync(request, new Metadata { { "x-correlation-id", correlationId } }, cancellationToken: _ct);

        // Console logging is buffered, so the line may take a moment to appear.
        string? line = null;
        for (var i = 0; i < 50 && line is null; i++)
        {
            line = (await engine.LogsAsync()).Split('\n').SingleOrDefault(l => l.Contains(correlationId, StringComparison.Ordinal));
            await Task.Delay(line is null ? 100 : 0, _ct);
        }

        Assert.NotNull(line);
        Assert.Contains("/auction.v1.AuctionCommandService/CreateDraft", line, StringComparison.Ordinal);
    }
}
