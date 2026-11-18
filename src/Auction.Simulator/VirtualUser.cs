using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Auction.Simulator;

/// <summary>The API's answers the simulator reads.</summary>
public sealed record AccountView(Guid Id, string DisplayName, string Kind);

public sealed record AuctionView(Guid Id, string Status, string BidEntry, long OpeningPriceCents, long IncrementCents, long MinimumNextBidCents, long? CurrentPriceCents, DateTimeOffset EndsAt, long Version);

public sealed record CommandView(Guid AuctionId, long? Version, bool? YouLead, bool Replayed);

/// <summary>The result of one HTTP call: its status, its Problem Details code if refused, and how long it took.</summary>
public sealed record CallResult(HttpStatusCode Status, string? Code, TimeSpan Elapsed, JsonElement? Body)
{
    public bool Succeeded => (int)Status is >= 200 and < 300;

    /// <summary>Nothing was recorded: the same key may be sent again.</summary>
    public bool Retryable => Status is HttpStatusCode.ServiceUnavailable or 0;
}

/// <summary>
/// One simulated person: their own cookie jar (SIM-01), the ordinary password sign-in, and same-origin JSON
/// with an Origin header, like a browser (SIM-05).
/// </summary>
public sealed class VirtualUser : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public VirtualUser(Uri baseAddress)
    {
        _http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) { BaseAddress = baseAddress };
        _http.DefaultRequestHeaders.Add("Origin", baseAddress.GetLeftPart(UriPartial.Authority));
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public string Email { get; private set; } = "";

    public AccountView? Account { get; private set; }

    /// <summary>Signs in and confirms that /account/me reports the same seeded simulator account.</summary>
    public async Task<bool> SignInAsync(string email, string password, CancellationToken cancellationToken)
    {
        Email = email;
        var signIn = await SendAsync(HttpMethod.Post, "api/v1/account/sign-in", new { email, password }, null, cancellationToken);
        if (!signIn.Succeeded)
        {
            return false;
        }

        var me = await SendAsync(HttpMethod.Get, "api/v1/account/me", null, null, cancellationToken);
        Account = me.Body?.Deserialize<AccountView>(Json);
        var claimed = signIn.Body?.Deserialize<AccountView>(Json);
        return me.Succeeded && Account is { Kind: "simulator" } && Account.Id == claimed?.Id;
    }

    public async Task<AuctionView?> AuctionAsync(Guid id, CancellationToken cancellationToken) =>
        (await SendAsync(HttpMethod.Get, $"api/v1/auctions/{id}", null, null, cancellationToken)).Body?.Deserialize<AuctionView>(Json);

    public Task<CallResult> CreateDraftAsync(object terms, string key, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, "api/v1/auctions", terms, key, cancellationToken);

    public Task<CallResult> PublishAsync(Guid id, string key, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"api/v1/auctions/{id}/publish", null, key, cancellationToken);

    public Task<CallResult> BidAsync(Guid id, long amountCents, string key, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, $"api/v1/auctions/{id}/bids", new { amountCents }, key, cancellationToken);

    public void Dispose() => _http.Dispose();

    private async Task<CallResult> SendAsync(HttpMethod method, string path, object? body, string? key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        var started = TimeProvider.System.GetTimestamp();
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            JsonElement? json = text.Length > 0 && response.Content.Headers.ContentType?.MediaType?.EndsWith("json", StringComparison.Ordinal) == true
                ? JsonDocument.Parse(text).RootElement.Clone()
                : null;
            string? code = null;
            if (!response.IsSuccessStatusCode && json is { } problem && problem.TryGetProperty("code", out var c))
            {
                code = c.GetString();
            }

            return new CallResult(response.StatusCode, code, elapsed, json);
        }
        catch (HttpRequestException)
        {
            return new CallResult(0, "transport", TimeProvider.System.GetElapsedTime(started), null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CallResult(0, "timeout", TimeProvider.System.GetElapsedTime(started), null);
        }
    }
}
