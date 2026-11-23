using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Auction.Web.Client;

/// <summary>
/// Every call the client makes to the JSON API. Commands carry an Idempotency-Key chosen by the caller, so
/// retrying a failed request with the same key never runs it twice.
/// </summary>
public sealed class AuctionApi(HttpClient http, Func<TimeSpan, Task>? wait = null)
{
    private const string Base = "api/v1/";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // A deploy leaves the API unreachable for a few seconds; reads wait it out, writes leave the retry to the person.
    private static readonly TimeSpan[] GatewayRetries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly Func<TimeSpan, Task> _wait = wait ?? (delay => Task.Delay(delay));

    public async Task<Account?> MeAsync() => (await SendAsync<Account>(HttpMethod.Get, "account/me")).Value;

    /// <summary>Creates a demo account; <paramref name="role"/> is "user" or "admin".</summary>
    public Task<ApiResult<Account>> SignUpAsync(string displayName, string email, string role) =>
        SendAsync<Account>(HttpMethod.Post, "account/sign-up", new { displayName, email, role });

    public Task<ApiResult<Account>> SignInAsync(string email, string? password) =>
        SendAsync<Account>(HttpMethod.Post, "account/sign-in", new { email, password = string.IsNullOrEmpty(password) ? null : password });

    public Task<ApiResult<object>> SignOutAsync() => SendAsync<object>(HttpMethod.Post, "account/sign-out");

    public Task<ApiResult<Page<AuctionSummary>>> ListAsync(string? cursor) => SearchAsync(null, null, cursor);

    /// <summary>The directory with an optional title keyword and status (active, ending_soon, closed).</summary>
    public Task<ApiResult<Page<AuctionSummary>>> SearchAsync(string? query, string? status, string? cursor)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
        {
            filters.Add($"q={Uri.EscapeDataString(query.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            filters.Add($"status={Uri.EscapeDataString(status)}");
        }

        var path = filters.Count == 0 ? "auctions" : $"auctions?{string.Join('&', filters)}";
        return SendAsync<Page<AuctionSummary>>(HttpMethod.Get, WithCursor(path, cursor));
    }

    public Task<ApiResult<AuctionDetail>> DetailAsync(Guid id) => SendAsync<AuctionDetail>(HttpMethod.Get, $"auctions/{id}");

    /// <summary>One page of an auction's bid history, newest first; <paramref name="pageSize"/> defaults to the API's.</summary>
    public Task<ApiResult<Page<BidView>>> BidsAsync(Guid id, string? cursor = null, int? pageSize = null) =>
        SendAsync<Page<BidView>>(HttpMethod.Get, WithCursor(pageSize is { } size ? $"auctions/{id}/bids?pageSize={size}" : $"auctions/{id}/bids", cursor));

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

    public Task<ApiResult<NotificationPage>> NotificationsAsync(string? cursor) =>
        SendAsync<NotificationPage>(HttpMethod.Get, WithCursor("me/notifications", cursor));

    public Task<ApiResult<object>> MarkNotificationReadAsync(long id) =>
        SendAsync<object>(HttpMethod.Post, $"me/notifications/{id}/read");

    /// <summary>Uploads an image's bytes as base64 inside JSON; the server checks the real type.</summary>
    public Task<ApiResult<UploadedImage>> UploadImageAsync(string contentType, byte[] data) =>
        SendAsync<UploadedImage>(HttpMethod.Post, "images", new { contentType, dataBase64 = Convert.ToBase64String(data) });

    public Task<ApiResult<CommandResponse>> AttachImageAsync(Guid auctionId, Guid imageId, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Post, $"auctions/{auctionId}/images", new { imageId }, key);

    public Task<ApiResult<CommandResponse>> RemoveImageAsync(Guid auctionId, Guid imageId, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Delete, $"auctions/{auctionId}/images/{imageId}", key: key);

    public Task<ApiResult<CommandResponse>> SetCoverImageAsync(Guid auctionId, Guid imageId, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Post, $"auctions/{auctionId}/images/{imageId}/cover", key: key);

    public Task<ApiResult<CommandResponse>> ReorderImagesAsync(Guid auctionId, IReadOnlyList<Guid> imageIds, string key) =>
        SendAsync<CommandResponse>(HttpMethod.Put, $"auctions/{auctionId}/images", new { imageIds }, key);

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

    public Task<ApiResult<Page<UserSummary>>> AdminUsersAsync(string? query, string? kind, string? state, string? cursor, int pageSize) =>
        SendAsync<Page<UserSummary>>(HttpMethod.Get, WithCursor(
            $"admin/users?q={Uri.EscapeDataString(query ?? "")}&kind={Uri.EscapeDataString(kind ?? "")}&state={Uri.EscapeDataString(state ?? "")}&pageSize={pageSize}",
            cursor));

    public Task<ApiResult<object>> SetUserDisabledAsync(Guid id, bool disabled, string reason) =>
        SendAsync<object>(HttpMethod.Post, $"admin/users/{id}/{(disabled ? "disable" : "enable")}", new { reason });

    public Task<ApiResult<Page<AuditEntry>>> AdminAuditAsync(string? query, string? cursor) =>
        SendAsync<Page<AuditEntry>>(HttpMethod.Get, WithCursor($"admin/audit?q={Uri.EscapeDataString(query ?? "")}", cursor));

    public Task<ApiResult<SimulatorView>> AdminSimulatorAsync() => SendAsync<SimulatorView>(HttpMethod.Get, "admin/simulator");

    public Task<ApiResult<SimulatorRun>> StartSimulatorAsync(string profile) =>
        SendAsync<SimulatorRun>(HttpMethod.Post, "admin/simulator/runs", new { profile });

    public Task<ApiResult<SimulatorRun>> StopSimulatorAsync() => SendAsync<SimulatorRun>(HttpMethod.Delete, "admin/simulator/runs/current");

    /// <summary>Pauses the live marketplace for up to 30 minutes.</summary>
    public Task<ApiResult<SimulatorRun>> PauseLiveAsync() => SendAsync<SimulatorRun>(HttpMethod.Delete, "admin/simulator/live");

    public Task<ApiResult<SimulatorRun>> ResumeLiveAsync() => SendAsync<SimulatorRun>(HttpMethod.Post, "admin/simulator/live");

    public Task<ApiResult<DemoState>> DemoStateAsync() => SendAsync<DemoState>(HttpMethod.Get, "admin/demo");

    /// <summary>Asks for the demo to return to its starting state now; the demo-clock service runs it within seconds.</summary>
    public Task<ApiResult<DemoState>> ResetDemoAsync() => SendAsync<DemoState>(HttpMethod.Post, "admin/demo/reset", new { });

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
        HttpResponseMessage response;
        try
        {
            response = await SendOnceAsync(method, path, body, key);
            for (var retry = 0; method == HttpMethod.Get && IsGatewayError(response.StatusCode) && retry < GatewayRetries.Length; retry++)
            {
                response.Dispose();
                await _wait(GatewayRetries[retry]);
                response = await SendOnceAsync(method, path, body, key);
            }
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

    // A sent request can't be sent again, so each attempt builds its own.
    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, string? key)
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

        return await http.SendAsync(request);
    }

    private static bool IsGatewayError(HttpStatusCode status) =>
        status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

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
