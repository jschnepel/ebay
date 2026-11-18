using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Auction.IntegrationTests.Database;
using Microsoft.AspNetCore.Identity;

namespace Auction.IntegrationTests.Api;

/// <summary>The HTTP calls the API tests repeat: commands with an Idempotency-Key, listings, JSON bodies.</summary>
internal static class ApiCalls
{
    public const string KeyHeader = "Idempotency-Key";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A command with a fresh key unless <paramref name="key"/> is given; <c>""</c> sends none.</summary>
    public static async Task<HttpResponseMessage> CommandAsync(
        this HttpClient http, HttpMethod method, string url, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        key ??= Guid.NewGuid().ToString();
        if (key.Length > 0)
        {
            request.Headers.Add(KeyHeader, key);
        }

        return await http.SendAsync(request, Ct);
    }

    public static Task<HttpResponseMessage> PostCommandAsync(this HttpClient http, string url, object? body = null, string? key = null) =>
        http.CommandAsync(HttpMethod.Post, url, body, key);

    public static object Terms(
        string title = "Film camera", long openingPriceCents = 1000, string floorType = "reserve", long? reserveCents = 2000,
        string bidEntry = "proxy", TimeSpan? startIn = null) =>
        new
        {
            title,
            description = "A 35 mm rangefinder in working order.",
            openingPriceCents,
            incrementCents = 100,
            floorType,
            reserveCents,
            bidEntry,
            startsAt = DateTimeOffset.UtcNow + (startIn ?? TimeSpan.FromMinutes(-1)),
            endsAt = DateTimeOffset.UtcNow + (startIn ?? TimeSpan.Zero) + TimeSpan.FromHours(1),
        };

    public static async Task<Guid> CreateDraftAsync(this HttpClient http, object? terms = null)
    {
        using var response = await http.PostCommandAsync("/api/v1/auctions", terms ?? Terms());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("auctionId").GetGuid();
    }

    /// <summary>A draft created and published by <paramref name="seller"/>; active at once unless it starts later.</summary>
    public static async Task<Guid> PublishedAsync(this ApiUser seller, object? terms = null)
    {
        var id = await seller.Http.CreateDraftAsync(terms);
        using var published = await seller.Http.PostCommandAsync($"/api/v1/auctions/{id}/publish");
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return id;
    }

    public static Task<HttpResponseMessage> BidAsync(this HttpClient http, Guid auctionId, long amountCents, string? key = null) =>
        http.PostCommandAsync($"/api/v1/auctions/{auctionId}/bids", new { amountCents }, key);

    // Read as text, which leaves a buffered body readable again.
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    public static async Task<JsonElement> GetJsonAsync(this HttpClient http, string url)
    {
        using var response = await http.GetAsync(new Uri(url, UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.JsonAsync();
    }

    public static async Task<HttpStatusCode> StatusOfGetAsync(this HttpClient http, string url)
    {
        using var response = await http.GetAsync(new Uri(url, UriKind.Relative), Ct);
        return response.StatusCode;
    }

    /// <summary>Asserts a Problem Details response with the given status and <c>code</c> extension.</summary>
    public static async Task AssertProblemAsync(this HttpResponseMessage response, HttpStatusCode status, string? code = null)
    {
        // Buffered, so the caller can read the body again after this check.
        await response.Content.LoadIntoBufferAsync(Ct);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.JsonAsync();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        if (code is not null)
        {
            Assert.Equal(code, problem.GetProperty("code").GetString());
        }
    }

    /// <summary>A password account inserted as the schema owner, as seeding will do.</summary>
    public static async Task<string> InsertPasswordAccountAsync(this MigratedDatabase db, string kind, string password)
    {
        var email = $"{kind}-{Guid.NewGuid():N}@example.test";
        var hash = new PasswordHasher<object>().HashPassword(new object(), password);
        await db.ExecuteAsync(
            MigratedDatabase.Migrator,
            "INSERT INTO users (id, email, normalized_email, display_name, kind, password_hash) VALUES ($1, $2, upper($2), $3, $4, $5)",
            Guid.CreateVersion7(), email, kind + " account", kind, hash);
        return email;
    }
}
