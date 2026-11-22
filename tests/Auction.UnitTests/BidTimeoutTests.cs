using Auction.Web.Client;

namespace Auction.UnitTests;

public sealed class BidTimeoutTests
{
    [Fact]
    public async Task A_client_timeout_leaves_the_bid_unconfirmed()
    {
        using var http = new HttpClient(new WaitingHandler())
        {
            BaseAddress = new Uri("https://auction.test/"),
            Timeout = TimeSpan.FromMilliseconds(250),
        };

        var result = await new AuctionApi(http).BidAsync(Guid.NewGuid(), 5000, "camera-bid");

        Assert.Null(result.Value);
        Assert.Equal(Problem.UnconfirmedBid, result.Problem);
    }

    [Fact]
    public async Task Cancelling_a_pending_bid_preserves_the_cancellation()
    {
        var handler = new WaitingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://auction.test/") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var bid = new AuctionApi(http).BidAsync(Guid.NewGuid(), 5000, "camera-bid");
            await handler.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            http.CancelPendingRequests();
            await bid;
        });
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The request should have been cancelled.");
        }
    }
}
