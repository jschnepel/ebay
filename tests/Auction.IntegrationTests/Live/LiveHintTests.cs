using System.Diagnostics;
using System.Net;
using System.Threading.Channels;
using Auction.IntegrationTests.Api;
using Auction.IntegrationTests.Database;
using Microsoft.AspNetCore.SignalR.Client;

namespace Auction.IntegrationTests.Live;

/// <summary>
/// Live hints end to end: the engine's outbox row, the relay, the Redis backplane and a SignalR client
/// (RT-01, OUT-01). Every API host shares the backplane, so whichever host's relay holds the lock reaches
/// clients on all of them.
/// </summary>
public sealed class LiveHintTests(MigratedDatabase db)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_bid_reaches_watchers_of_the_auction_and_of_the_directory_with_the_new_version_within_two_seconds()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync();
        await using var auctionWatcher = await HintWatcher.StartAsync(api, watch: id);
        await using var directoryWatcher = await HintWatcher.StartAsync(api, watch: null);

        using (var bid = await buyer.Http.BidAsync(id, 1500))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        var version = (await api.Anonymous().GetJsonAsync($"/api/v1/auctions/{id}")).GetProperty("version").GetInt64();
        Assert.Equal(version, (await auctionWatcher.NextForAsync(id, Bound, minimumVersion: version)).Version);
        Assert.Equal(version, (await directoryWatcher.NextForAsync(id, Bound, minimumVersion: version)).Version);
    }

    [Fact]
    public async Task An_event_whose_transaction_commits_last_is_not_skipped_and_holds_back_later_ones()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var first = await seller.PublishedAsync();
        var second = await seller.PublishedAsync();
        await using var watcher = await HintWatcher.StartAsync(api, first, second);

        // The lower id is taken first but commits last.
        await using var late = await db.OpenAsync(MigratedDatabase.Migrator);
        await using var lateTransaction = await late.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var lateId = await InsertEventAsync(late, first, 1_000);
        long earlyId;
        await using (var early = await db.OpenAsync(MigratedDatabase.Migrator))
        {
            earlyId = await InsertEventAsync(early, second, 1_001);
        }

        Assert.True(lateId < earlyId);
        await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
        Assert.False(watcher.Saw(second, 1_001));
        Assert.Null(await db.ScalarAsync<DateTime?>(MigratedDatabase.Migrator, "SELECT published_at FROM outbox_events WHERE id = $1", earlyId));

        await lateTransaction.CommitAsync(TestContext.Current.CancellationToken);

        var a = await watcher.NextForAsync(first, Bound, minimumVersion: 1_000);
        var b = await watcher.NextForAsync(second, Bound, minimumVersion: 1_001);
        Assert.True(a.Order < b.Order, "the event that committed last was published after the one it held back");
    }

    [Fact]
    public async Task A_bid_event_marked_pending_again_sends_the_same_version_twice()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var buyer = await api.SignUpAsync();
        var id = await seller.PublishedAsync();
        await using var watcher = await HintWatcher.StartAsync(api, id);
        using (var bid = await buyer.Http.BidAsync(id, 1500))
        {
            Assert.Equal(HttpStatusCode.Created, bid.StatusCode);
        }

        // The bid's own version: the publish hint can still arrive after the watcher subscribes.
        var version = (await api.Anonymous().GetJsonAsync($"/api/v1/auctions/{id}")).GetProperty("version").GetInt64();
        Assert.Equal(version, (await watcher.NextForAsync(id, Bound, minimumVersion: version)).Version);

        // Receiving the hint can precede the relay's write. Finish the first publication before simulating a lost stamp.
        await WaitForPublicationAsync(id, version);
        var reset = await db.ExecuteAsync(MigratedDatabase.Api,
            "UPDATE outbox_events SET published_at = NULL WHERE auction_id = $1 AND auction_version = $2 AND published_at IS NOT NULL", id, version);
        Assert.Equal(1, reset);

        Assert.Equal(version, (await watcher.NextForAsync(id, Bound, minimumVersion: version)).Version);

        await WaitForPublicationAsync(id, version);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public async Task The_hub_refuses_a_foreign_origin(string method)
    {
        var api = await db.ApiAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), method == "POST" ? "/hubs/auctions/negotiate?negotiateVersion=1" : "/hubs/auctions");
        request.Headers.Add("Origin", "https://evil.example");
        using var control = new HttpRequestMessage(HttpMethod.Post, "/hubs/auctions/negotiate?negotiateVersion=1");
        control.Headers.Add("Origin", ApiHost.BaseAddress.GetLeftPart(UriPartial.Authority));

        using var refused = await api.Anonymous().SendAsync(request, TestContext.Current.CancellationToken);
        using var accepted = await api.Anonymous().SendAsync(control, TestContext.Current.CancellationToken);

        await refused.AssertProblemAsync(HttpStatusCode.Forbidden, "foreign_origin");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    private async Task WaitForPublicationAsync(Guid auctionId, long version)
    {
        var clock = Stopwatch.StartNew();
        while (await db.ScalarAsync<DateTime?>(MigratedDatabase.Migrator,
            "SELECT published_at FROM outbox_events WHERE auction_id = $1 AND auction_version = $2", auctionId, version) is null)
        {
            Assert.True(clock.Elapsed < Bound, $"The relay did not stamp auction {auctionId}, version {version}, within {Bound.TotalSeconds} seconds.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<long> InsertEventAsync(Npgsql.NpgsqlConnection connection, Guid auctionId, long version)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            "INSERT INTO outbox_events (auction_id, auction_version, type, payload) VALUES ($1, $2, 'test.event', '{}'::jsonb) RETURNING id", connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = auctionId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = version });
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

/// <summary>A SignalR client on the in-process API that records every hint it receives, in order.</summary>
internal sealed class HintWatcher : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly Channel<Hint> _hints = Channel.CreateUnbounded<Hint>();
    private readonly List<Hint> _seen = [];
    private int _order;

    private HintWatcher(HubConnection connection) => _connection = connection;

    public sealed record Hint(Guid AuctionId, long Version, int Order);

    private sealed record Changed(Guid AuctionId, long Version);

    /// <summary>Watches the given auctions, or the whole directory when none are given.</summary>
    public static async Task<HintWatcher> StartAsync(ApiHost api, params Guid[]? watch)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(ApiHost.BaseAddress, "/hubs/auctions"), options =>
            {
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            })
            .Build();
        var watcher = new HintWatcher(connection);
        connection.On<Changed>("changed", change =>
        {
            var hint = new Hint(change.AuctionId, change.Version, Interlocked.Increment(ref watcher._order));
            lock (watcher._seen)
            {
                watcher._seen.Add(hint);
            }

            watcher._hints.Writer.TryWrite(hint);
        });
        await connection.StartAsync(TestContext.Current.CancellationToken);
        if (watch is { Length: > 0 })
        {
            foreach (var id in watch)
            {
                await connection.InvokeAsync("Watch", id, TestContext.Current.CancellationToken);
            }
        }
        else
        {
            await connection.InvokeAsync("WatchDirectory", TestContext.Current.CancellationToken);
        }

        return watcher;
    }

    public bool Saw(Guid auctionId, long version)
    {
        lock (_seen)
        {
            return _seen.Any(h => h.AuctionId == auctionId && h.Version == version);
        }
    }

    /// <summary>The next hint for the auction at or above the version, or a failure after the bound.</summary>
    public async Task<Hint> NextForAsync(Guid auctionId, TimeSpan bound, long minimumVersion = 0)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(bound);
        try
        {
            while (true)
            {
                var hint = await _hints.Reader.ReadAsync(timeout.Token);
                if (hint.AuctionId == auctionId && hint.Version >= minimumVersion)
                {
                    return hint;
                }
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail($"No hint for {auctionId} (version >= {minimumVersion}) within {bound.TotalSeconds} s.");
            throw;
        }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
