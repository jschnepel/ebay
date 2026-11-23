using System.Net;
using Auction.Web.Client;

namespace Auction.UnitTests;

/// <summary>A deploy leaves the site unreachable for a few seconds; reads ride it out, writes don't repeat themselves.</summary>
public sealed class ClientGatewayRetryTests
{
    private const string Updating = "The site is updating. Try again in a moment.";

    [Fact]
    public async Task A_GET_that_meets_a_gateway_error_is_retried_until_it_succeeds()
    {
        var server = new QueuedAnswers(HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var waits = new List<TimeSpan>();
        var api = new AuctionApi(server.Client(), wait => { waits.Add(wait); return Task.CompletedTask; });

        var account = await api.MeAsync();

        Assert.NotNull(account);
        Assert.Equal(3, server.Requests);
        // Each wait is its step give or take a quarter, so many open tabs don't all come back at the same moment.
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0], TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(1.25));
        Assert.InRange(waits[1], TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5));
        Assert.NotEqual(TimeSpan.FromSeconds(1), waits[0]);
    }

    [Fact]
    public async Task A_GET_that_keeps_failing_gives_up_after_three_retries()
    {
        var server = new QueuedAnswers(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway);
        var api = new AuctionApi(server.Client(), _ => Task.CompletedTask);

        var result = await api.AdminHealthAsync();

        Assert.Equal(Updating, result.Message);
        Assert.Equal(4, server.Requests);
    }

    [Fact]
    public async Task A_write_is_not_retried()
    {
        var server = new QueuedAnswers(HttpStatusCode.BadGateway, HttpStatusCode.OK);
        var api = new AuctionApi(server.Client(), _ => Task.CompletedTask);

        var result = await api.SignOutAsync();

        Assert.Equal(Updating, result.Message);
        Assert.Equal(1, server.Requests);
    }

    /// <summary>Answers each request with the next status; a success carries a minimal account.</summary>
    private sealed class QueuedAnswers(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);

        public int Requests { get; private set; }

        public HttpClient Client() => new(this) { BaseAddress = new Uri("https://site.test/") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var status = _statuses.Dequeue();
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new StringContent(
                    """{"id":"0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b","displayName":"Ada","kind":"self_registered"}""",
                    System.Text.Encoding.UTF8,
                    "application/json");
            }

            return Task.FromResult(response);
        }
    }
}
