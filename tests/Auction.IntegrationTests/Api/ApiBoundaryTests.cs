using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Auction.IntegrationTests.Database;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Auction.IntegrationTests.Api;

/// <summary>Cross-site protection, error format, pagination bounds, query counts and the OpenAPI contract.</summary>
public sealed partial class ApiBoundaryTests(MigratedDatabase db)
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_unsafe_request_from_a_foreign_origin_or_as_a_form_post_is_refused()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auctions") { Content = JsonContent.Create(ApiCalls.Terms()) };
        foreign.Headers.Add("Origin", "https://evil.example");
        foreign.Headers.Add(ApiCalls.KeyHeader, Guid.NewGuid().ToString());
        using var foreignResponse = await seller.Http.SendAsync(foreign, _ct);

        using var form = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auctions")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["title"] = "Film camera" }),
        };
        form.Headers.Add(ApiCalls.KeyHeader, Guid.NewGuid().ToString());
        using var formResponse = await seller.Http.SendAsync(form, _ct);

        using var sameOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auctions") { Content = JsonContent.Create(ApiCalls.Terms()) };
        sameOrigin.Headers.Add("Origin", "https://localhost");
        sameOrigin.Headers.Add(ApiCalls.KeyHeader, Guid.NewGuid().ToString());
        using var control = await seller.Http.SendAsync(sameOrigin, _ct);

        await foreignResponse.AssertProblemAsync(HttpStatusCode.Forbidden, "foreign_origin");
        await formResponse.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType, "json_only");
        Assert.Equal(HttpStatusCode.Created, control.StatusCode);
    }

    [Fact]
    public async Task Errors_are_Problem_Details_without_internals()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();

        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auctions")
        {
            Content = new StringContent("{ \"title\": ", Encoding.UTF8, "application/json"),
        };
        malformed.Headers.Add(ApiCalls.KeyHeader, Guid.NewGuid().ToString());
        using var badJson = await seller.Http.SendAsync(malformed, _ct);
        using var unknown = await api.Anonymous().GetAsync(new Uri($"/api/v1/auctions/{Guid.NewGuid()}", UriKind.Relative), _ct);

        await badJson.AssertProblemAsync(HttpStatusCode.BadRequest);
        await unknown.AssertProblemAsync(HttpStatusCode.NotFound, "not_found");
        foreach (var response in new[] { badJson, unknown })
        {
            var body = await response.Content.ReadAsStringAsync(_ct);
            Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
            Assert.DoesNotContain(" at ", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Listing_pages_are_bounded_and_page_through_every_auction_once_in_a_fixed_order()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var listed = new List<Guid>();
        for (var i = 0; i < ApiHost.MaxPageSize + 1; i++)
        {
            listed.Add(await seller.PublishedAsync());
        }

        var anonymous = api.Anonymous();
        var byDefault = await anonymous.GetJsonAsync("/api/v1/auctions");
        var oversized = await anonymous.GetJsonAsync("/api/v1/auctions?pageSize=1000");
        Assert.Equal(ApiHost.DefaultPageSize, byDefault.GetProperty("items").GetArrayLength());
        Assert.Equal(ApiHost.MaxPageSize, oversized.GetProperty("items").GetArrayLength());

        var seen = new List<(DateTimeOffset EndsAt, Guid Id)>();
        string? cursor = null;
        do
        {
            var page = await anonymous.GetJsonAsync("/api/v1/auctions?pageSize=4" + (cursor is null ? "" : $"&cursor={cursor}"));
            seen.AddRange(page.GetProperty("items").EnumerateArray()
                .Select(a => (a.GetProperty("endsAt").GetDateTimeOffset(), a.GetProperty("id").GetGuid())));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal(seen.Count, seen.Select(a => a.Id).Distinct().Count());
        Assert.Equal(seen.OrderBy(a => a.EndsAt).ThenBy(a => a.Id.ToString()).Select(a => a.Id), seen.Select(a => a.Id));
        Assert.All(listed, id => Assert.Contains(id, seen.Select(a => a.Id)));
    }

    [Theory]
    [InlineData("/api/v1/auctions?pageSize=0")]
    [InlineData("/api/v1/auctions?cursor=not-a-cursor")]
    public async Task A_bad_page_size_or_cursor_is_refused_with_400(string url)
    {
        using var response = await (await db.ApiAsync()).Anonymous().GetAsync(new Uri(url, UriKind.Relative), _ct);

        await response.AssertProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Each_read_runs_a_fixed_number_of_queries_whatever_the_page_size()
    {
        var api = await db.ApiAsync();
        var seller = await api.SignUpAsync();
        var id = await seller.PublishedAsync();
        for (var i = 0; i < 3; i++)
        {
            using var bid = await (await api.SignUpAsync()).Http.BidAsync(id, 2000 + (i * 1000));
        }

        var anonymous = api.Anonymous();
        Assert.Equal(1, await QueriesAsync(anonymous, "/api/v1/auctions?pageSize=1"));
        Assert.Equal(1, await QueriesAsync(anonymous, $"/api/v1/auctions?pageSize={ApiHost.MaxPageSize}"));
        Assert.Equal(1, await QueriesAsync(anonymous, $"/api/v1/auctions/{id}"));

        // The bid list is for admins. One request first, so the session check is answered from its cache after that.
        var admin = (await api.SignUpAsync(role: "admin")).Http;
        await admin.GetJsonAsync("/api/v1/account/me");
        Assert.Equal(2, await QueriesAsync(admin, $"/api/v1/auctions/{id}/bids?pageSize=1", emptyPool: true));
        Assert.Equal(2, await QueriesAsync(admin, $"/api/v1/auctions/{id}/bids?pageSize={ApiHost.MaxPageSize}"));
    }

    [Fact]
    public async Task The_OpenAPI_document_lists_exactly_the_routes_the_API_serves()
    {
        var api = await db.ApiAsync();
        var document = await api.Anonymous().GetJsonAsync("/openapi/v1.json");

        var documented = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject().Select(op => $"{op.Name.ToUpperInvariant()} {path.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();
        var served = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(m => $"{m} {RouteConstraint().Replace(e.RoutePattern.RawText!, "}").TrimEnd('/')}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(served);
        Assert.Equal(served, documented);
    }

    [GeneratedRegex(":[a-z]+}")]
    private static partial Regex RouteConstraint();

    /// <summary>
    /// Counts the database commands one request runs, by tracing it under a trace id of its own. With
    /// <paramref name="emptyPool"/> the request has to open a fresh connection, as it may under load.
    /// </summary>
    private async Task<int> QueriesAsync(HttpClient http, string url, bool emptyPool = false)
    {
        var traceId = ActivityTraceId.CreateRandom();
        var count = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "Npgsql" or "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                // Npgsql also traces opening a physical connection; only spans that carry a query are commands.
                if (activity.Source.Name == "Npgsql" && activity.TraceId == traceId && activity.GetTagItem("db.query.text") is not null)
                {
                    Interlocked.Increment(ref count);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        if (emptyPool)
        {
            (await db.ApiAsync()).Services.GetRequiredService<Npgsql.NpgsqlDataSource>().Clear();
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        using var response = await http.SendAsync(request, _ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return count;
    }
}
