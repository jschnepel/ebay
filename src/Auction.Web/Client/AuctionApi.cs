using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Auction.Web.Client;

/// <summary>
/// Every call the client makes to the JSON API. Commands carry an Idempotency-Key chosen by the caller, so
/// retrying a failed request with the same key never runs it twice.
/// </summary>
public sealed class AuctionApi(HttpClient http)
{
    private const string Base = "api/v1/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Account?> MeAsync() => (await SendAsync<Account>(HttpMethod.Get, "account/me")).Value;

    public Task<ApiResult<Account>> SignUpAsync(string displayName, string email) =>
        SendAsync<Account>(HttpMethod.Post, "account/sign-up", new { displayName, email });

    public Task<ApiResult<Account>> SignInAsync(string email, string? password) =>
        SendAsync<Account>(HttpMethod.Post, "account/sign-in", new { email, password = string.IsNullOrEmpty(password) ? null : password });

    public Task<ApiResult<object>> SignOutAsync() => SendAsync<object>(HttpMethod.Post, "account/sign-out");

    public Task<ApiResult<Page<AuctionSummary>>> ListAsync(string? cursor) =>
        SendAsync<Page<AuctionSummary>>(HttpMethod.Get, WithCursor("auctions", cursor));

    public Task<ApiResult<AuctionDetail>> DetailAsync(Guid id) => SendAsync<AuctionDetail>(HttpMethod.Get, $"auctions/{id}");

    public Task<ApiResult<Page<BidView>>> BidsAsync(Guid id, string? cursor = null) =>
        SendAsync<Page<BidView>>(HttpMethod.Get, WithCursor($"auctions/{id}/bids", cursor));

    public Task<ApiResult<Page<AuctionSummary>>> MineAsync(string? cursor) =>
        SendAsync<Page<AuctionSummary>>(HttpMethod.Get, WithCursor("me/auctions", cursor));

    public Task<ApiResult<Page<BiddingSummary>>> MyBidsAsync(string? cursor) =>
        SendAsync<Page<BiddingSummary>>(HttpMethod.Get, WithCursor("me/bids", cursor));

    public Task<ApiResult<CommandResponse>> CreateDraftAsync(DraftTerms terms, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Post, "auctions", terms, key);

    public Task<ApiResult<CommandResponse>> EditDraftAsync(Guid id, DraftTerms terms, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Put, $"auctions/{id}", terms, key);

    public Task<ApiResult<CommandResponse>> PublishAsync(Guid id, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Post, $"auctions/{id}/publish", key: key);

    public Task<ApiResult<CommandResponse>> CancelAsync(Guid id, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Post, $"auctions/{id}/cancel", key: key);

    public Task<ApiResult<CommandResponse>> BidAsync(Guid id, long amountCents, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Post, $"auctions/{id}/bids", new { amountCents }, key);

    public Task<ApiResult<CommandResponse>> DeleteDraftAsync(Guid id, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Delete, $"auctions/{id}", key: key);

    public Task<ApiResult<Activity>> AdminActivityAsync() => SendAsync<Activity>(HttpMethod.Get, "admin/activity");

    public Task<ApiResult<AdminHealth>> AdminHealthAsync() => SendAsync<AdminHealth>(HttpMethod.Get, "admin/health");

    public Task<ApiResult<AdminKpis>> AdminKpisAsync() => SendAsync<AdminKpis>(HttpMethod.Get, "admin/kpis");

    public Task<ApiResult<Page<ModeratedAuction>>> AdminAuctionsAsync(string? query, string? status, string? cursor) =>
        SendAsync<Page<ModeratedAuction>>(HttpMethod.Get, WithCursor($"admin/auctions?q={Uri.EscapeDataString(query ?? "")}&status={Uri.EscapeDataString(status ?? "")}", cursor));

    /// <summary>suspend, resume or cancel, with the reason the seller and bidders are told.</summary>
    public Task<ApiResult<CommandResponse>> ModerateAsync(Guid id, string action, string reason, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Post, $"admin/auctions/{id}/{action}", new { reason }, key);

    public Task<ApiResult<Page<UserSummary>>> AdminUsersAsync(string? query, string? cursor) =>
        SendAsync<Page<UserSummary>>(HttpMethod.Get, WithCursor($"admin/users?q={Uri.EscapeDataString(query ?? "")}", cursor));

    public Task<ApiResult<object>> SetUserDisabledAsync(Guid id, bool disabled, string reason) =>
        SendAsync<object>(HttpMethod.Post, $"admin/users/{id}/{(disabled ? "disable" : "enable")}", new { reason });

    private static string WithCursor(string path, string? cursor)
    {
        if (cursor is null)
        {
            return path;
        }

        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{path}{separator}cursor={Uri.EscapeDataString(cursor)}";
    }

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object? body = null, string? key = null)
    {
        using var request = new HttpRequestMessage(method, Base + path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            return new ApiResult<T>(default, Problem.Unreachable);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return new ApiResult<T>(default, await ReadProblemAsync(response));
            }

            return response.StatusCode == HttpStatusCode.NoContent
                ? new ApiResult<T>(default, null)
                : new ApiResult<T>(await response.Content.ReadFromJsonAsync<T>(Json), null);
        }
    }

    private static async Task<Problem> ReadProblemAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>(Json);
            return problem is null ? Fallback(response) : problem with { Status = (int)response.StatusCode };
        }
        catch (JsonException)
        {
            return Fallback(response);
        }
    }

    private static Problem Fallback(HttpResponseMessage response) =>
        new() { Status = (int)response.StatusCode, Title = $"The request failed ({(int)response.StatusCode})." };
}
